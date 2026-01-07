using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using OnCallDurableDemo.Entities; // ✅ เรียกใช้ Entities
using OnCallDurableDemo.Models;   // ✅ เรียกใช้ Models
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OnCallDurableDemo.Functions
{
    public static class OnCallWorkflow
    {
        // ------------------------------------------------------------------
        // 1. HTTP STARTER
        // ------------------------------------------------------------------
        [Function("StartOnCall")]
        public static async Task<HttpResponseData> Start(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "start")] HttpRequestData req,
            [DurableClient] DurableTaskClient starter,
            FunctionContext executionContext)
        {
            string instanceId = await starter.ScheduleNewOrchestrationInstanceAsync("OnCallOrchestrator");
            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        // ------------------------------------------------------------------
        // 2. ORCHESTRATOR
        // ------------------------------------------------------------------
        [Function("OnCallOrchestrator")]
        public static async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger("OnCallOrchestrator");

            var config = await context.CallActivityAsync<OnCallConfig>("Activity_GetConfig");
            var entityId = new EntityInstanceId(nameof(OnCallEntity), context.InstanceId);
            await context.Entities.CallEntityAsync(entityId, "Initialize", config.Requirements);

            bool isMissionSuccess = false;
            string endReason = "Workflow Finished";

            // ================= LOOP STEPS (Step 1 -> Step 2) =================
            foreach (var step in config.Steps.OrderBy(s => s.StepNumber))
            {
                if (isMissionSuccess) break;

                logger.LogInformation($"\u001b[35m===== STARTING STEP {step.StepNumber} ({(step.IsParallel ? "Parallel" : "Serial")}) =====\u001b[0m");

                // ล้างความจำ CalledUsers เฉพาะตอน "ขึ้น Step ใหม่" เท่านั้น
                // เพื่อให้ Step 2 สามารถโทรหาคนที่ Step 1 เคยโทรไปแล้วได้ (ถ้า Business ต้องการแบบนั้น)
                // แต่ถ้าอยากให้คนที่เคยโทรแล้วใน Step 1 ไม่ถูกโทรซ้ำใน Step 2 ให้ลบบรรทัดนี้ออกครับ
                await context.Entities.CallEntityAsync(entityId, "ResetStepMemory");

                // ================= REPLENISHMENT LOOP (วนลูปเติมคนจนกว่าจะครบ) =================
                // 🆕 นี่คือส่วนที่เพิ่มเข้ามาครับ เพื่อแก้ปัญหา "ทำรอบเดียวแล้วหนีไปเลย"
                int batchRound = 1;
                while (true)
                {
                    // 1. เช็คก่อนว่าครบหรือยัง ถ้าครบแล้ว Break ออกจาก Step นี้เลย
                    var currentState = await context.Entities.CallEntityAsync<OnCallEntity>(entityId, "GetState");
                    LogCurrentStatus(logger, currentState);

                    if (IsMissionComplete(currentState))
                    {
                        isMissionSuccess = true;
                        endReason = "Mission Complete";
                        goto EndOfWorkflow;
                    }

                    // 2. ดึงคนมา 1 Batch (ตาม Logic: Parallel=ดึงจนเต็ม Need, Serial=ดึง 1)
                    // คนที่เคยถูกเรียกใน Batch ก่อนหน้า (CalledUsers) จะไม่ถูกดึงซ้ำใน Step นี้
                    var usersInBatch = await context.Entities.CallEntityAsync<List<string>>(entityId, "GetBatchUsers", step.IsParallel);

                    if (usersInBatch.Count == 0)
                    {
                        logger.LogWarning($"[Step {step.StepNumber}] No more candidates available in DB. Moving to next step.");
                        break; // หมดคนให้เรียกใน Step นี้แล้ว -> ข้ามไป Step ถัดไป
                    }

                    logger.LogInformation($"--- [Step {step.StepNumber} | Batch {batchRound}] Processing Users: {string.Join(", ", usersInBatch)} ---");

                    // ================= LOOP ACTIONS (Call -> Text) =================
                    foreach (var action in step.Actions)
                    {
                        if (isMissionSuccess) break;

                        // ================= LOOP REPEAT (Retry Action) =================
                        for (int i = 0; i <= action.RepeatCount; i++)
                        {
                            // เช็คจบงานทุกครั้งก่อนทำ Action เผื่อมีคนตอบเข้ามาระหว่างรอ
                            bool isComplete = await context.Entities.CallEntityAsync<bool>(entityId, "IsMissionComplete");
                            if (isComplete) { isMissionSuccess = true; endReason = "Mission Complete"; goto EndOfWorkflow; }

                            // 🔍 Filter: ตัดคนที่ตอบแล้ว (Accepted/Declined) ออกจาก Batch นี้
                            var pendingUsers = await context.Entities.CallEntityAsync<List<string>>(entityId, "FilterPendingUsers", usersInBatch);

                            if (pendingUsers.Count == 0)
                            {
                                logger.LogInformation($"   ✅ All users in this batch responded. Stop processing actions for this batch.");
                                goto EndOfBatch; // กระโดดไปจบ Batch นี้ เพื่อไปดึงคนใหม่ (Replenish)
                            }

                            // Execute Action
                            string attemptInfo = action.RepeatCount > 0 ? $"(Attempt {i + 1}/{action.RepeatCount + 1})" : "";
                            logger.LogInformation($"   👉 Action: {action.Mode} {attemptInfo} -> Sending to {pendingUsers.Count} users.");

                            await context.CallActivityAsync("Activity_CallExternalApi", new TwilioInput() { Mode = action.Mode, UserIds = pendingUsers });

                            // ✅ แก้ไขส่วนรอเวลา (Wait Time): รอเวลา OR รอสัญญาณ "StopWait"
                            if (action.WaitTimeMinutes > 0)
                            {
                                logger.LogInformation($"      ⏳ Waiting {action.WaitTimeMinutes} mins (Or until Mission Complete)...");

                                var timerTask = context.CreateTimer(context.CurrentUtcDateTime.AddMinutes(action.WaitTimeMinutes), CancellationToken.None);

                                var stopSignalTask = context.WaitForExternalEvent<object>("StopWait");

                                var winner = await Task.WhenAny(timerTask, stopSignalTask);

                                if (winner == stopSignalTask)
                                {
                                    logger.LogInformation($"      ⚡ Received 'StopWait' Signal! Skipping remaining wait time.");
                                }
                                else
                                {
                                    logger.LogInformation($"      ⏰ Timer expired.");
                                }
                            }
                        }
                    } 

                EndOfBatch:
                    batchRound++;
                    // วนกลับไป while(true) ด้านบน -> เพื่อเช็ค Quota และดึงคนชุดใหม่ (B3, A3) มาทำต่อ

                } // End Replenishment Loop

                logger.LogInformation($"[Step {step.StepNumber}] Step Finished.");

            } // End Steps Loop

        EndOfWorkflow:

            // ... (Code ส่วน Report และ Save ลง DB เหมือนเดิม) ...
            var finalState = await context.Entities.CallEntityAsync<OnCallEntity>(entityId, "GetState");
            GenerateFinalReport(logger, finalState, isMissionSuccess, endReason);

            await context.CallActivityAsync("Activity_SaveOnCallSummary", new { Status = isMissionSuccess ? "Success" : "Failed", Reason = endReason });
            await context.Entities.SignalEntityAsync(entityId, "Delete", null);
        }

        // ------------------------------------------------------------------
        // 3. ACTIVITIES
        // ------------------------------------------------------------------
        [Function("Activity_GetConfig")]
        public static OnCallConfig GetConfig([ActivityTrigger] object input) => MockRepository.GetConfigFromDb();

        [Function("Activity_SimulateTwilioCall")]
        public static async Task SimulateTwilioCall([ActivityTrigger] object input, FunctionContext ctx)
        {
            var logger = ctx.GetLogger("Twilio");
            // ปรับ input ให้รับ dynamic หรือ object เพื่อความง่าย
            logger.LogWarning($"[Twilio] Simulating calls... (Wait 2s)");

            await Task.Delay(2000);
        }

        [Function("Activity_CallExternalApi")]
        public static async Task<string> CallExternalApi([ActivityTrigger] TwilioInput input, FunctionContext ctx)
        {
            var logger = ctx.GetLogger("ApiCall");

            using var httpClient = new HttpClient();

            try
            {
                string url = "";
                int timeoutSeconds = 30;
                var userPhonenumbers = input.UserIds.Select(u =>
                new UserPhoneNumber
                {
                    User = u,
                    Phone = UserInfo.UserPhoneNumber.ContainsKey(u) ? UserInfo.UserPhoneNumber[u] : ""
                }).ToList();

                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                var requestBodyObj = new
                {
                    EventName = "OnCallNotification",
                    Message = "Test Initiate Call/SMS",
                    Resources = userPhonenumbers
                };
                //var requestBodyObj = new
                //{
                //    Name = "OnCallNotification",
                //    Email = "test_call-out@example.com"
                //};

                if (input.Mode == "Voice")
                {
                    url = "https://ce72aa1460b0.ngrok-free.app/api/Call/bulk-call";
                    //url = "https://postman-echo.com/post";
                    logger.LogWarning($"      [Mock Voice] Sending to {string.Join(",", input.UserIds)}");
                }
                else if (input.Mode == "Sms")
                {
                    url = "https://ce72aa1460b0.ngrok-free.app/api/Sms/send-bulk-named";
                    //url = "https://postman-echo.com/post";
                    logger.LogWarning($"      [Mock SMS] Sending to  {string.Join(",", input.UserIds)}");
                }

                var requestBody = System.Text.Json.JsonSerializer.Serialize(requestBodyObj);

                // Only POST method
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation($"[API] Successfully called {url} - Status: {response.StatusCode}");
                    return responseContent;
                }
                else
                {
                    logger.LogError($"[API] Failed to call {url} - Status: {response.StatusCode}, Response: {responseContent}");

                    // Fallback for Twilio calls
                    if (url.Contains("twilio.com"))
                    {
                        logger.LogError($"[Twilio] API call failed, falling back to simulation");
                        await Task.Delay(2000);
                        return "Fallback: Simulated calls";
                    }

                    throw new HttpRequestException($"API call failed with status {response.StatusCode}: {responseContent}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[API] Exception during POST request: {ex.Message}");

                // Fallback for Twilio calls
                if (ex.Message.Contains("twilio") || ctx.GetLogger("TwilioApi") == logger)
                {
                    logger.LogError($"[Twilio] Exception occurred, falling back to simulation: {ex.Message}");
                    await Task.Delay(2000);
                    return "Fallback: Simulated calls";
                }

                throw;
            }
        }

        [Function("Activity_SaveOnCallSummary")]
        public static void SaveOnCallSummary([ActivityTrigger] object input, FunctionContext ctx)
        {
            ctx.GetLogger("DB").LogInformation($"\u001b[32m[DB] Summary Saved.\u001b[0m");
        }

        // ------------------------------------------------------------------
        // 4. WEBHOOK & MEDIATOR
        // ------------------------------------------------------------------
        [Function("HandleOnCallResponse")]
        public static async Task<HttpResponseData> Webhook(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/response")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext ctx)
        {
            var logger = ctx.GetLogger("Webhook"); // ✅ เพิ่ม Logger

            var body = await req.ReadFromJsonAsync<WebhookRequest>();
            if (body == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

            // ✅ LOG 1: รับ Request เข้ามา ดูทันทีว่าส่งอะไรมา สถานะคืออะไร
            string statusText = body.Status == 1 ? "Available (Accepted)" : "Unavailable (Declined)";
            logger.LogInformation($"\u001b[36m[Webhook] Received for User: {body.UserId} | Status: {statusText} ({body.Status})\u001b[0m");

            // -----------------------------------------------------------
            // 🔧 FIX BUG: ระบุ Group ให้ถูกต้อง (รองรับ A, B และ C)
            // -----------------------------------------------------------
            string group = "Unknown";
            if (body.UserId.StartsWith("A")) group = "GroupA";
            else if (body.UserId.StartsWith("B")) group = "GroupB";
            else if (body.UserId.StartsWith("C")) group = "GroupC";

            // Log เตือนถ้าหากลุ่มไม่เจอ
            if (group == "Unknown")
            {
                logger.LogError($"[Webhook] Error: Could not determine group for User {body.UserId}");
                // อาจจะ return bad request หรือให้ process ต่อแล้วแต่ business logic
            }
            else
            {
                logger.LogInformation($"[Webhook] User {body.UserId} mapped to group: {group}");
            }
            // -----------------------------------------------------------

            var mediatorName = body.Status == 1 ? "UserAcceptOrchestrator" : "UserDeclineOrchestrator";

            // ใส่ Group ที่ถูกต้องเข้าไปใน Input
            var input = new UserAcceptInput
            {
                Group = group,
                UserId = body.UserId,
                MainInstanceId = body.InstanceId
            };

            // ... (ส่วนการเรียก Orchestrator เหมือนเดิม) ...
            string opId = await client.ScheduleNewOrchestrationInstanceAsync(mediatorName, input);
            var result = await client.WaitForInstanceCompletionAsync(opId, true, CancellationToken.None);

            // ✅ ตรวจสอบผลลัพธ์ ถ้าเป็น MissionComplete ให้ส่งสัญญาณไปปลุก Main Orchestrator
            if (result.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
            {
                // SerializedOutput จะเป็น JSON string เช่น "\"MissionComplete\"" หรือ "\"Success\""
                var outputString = result.ReadOutputAs<string>(); // ใช้ Helper หรือ ToString()

                logger.LogInformation($"[Webhook] Result from Entity: {outputString}");

                if (outputString != null && outputString.Contains("MissionComplete"))
                {
                    logger.LogInformation($"[Webhook] 🚀 Mission Complete detected! Raising 'StopWait' event to Main Orchestrator ({body.InstanceId})");

                    // ส่ง Event ไปที่ Main Workflow เพื่อให้หลุดจาก Timer
                    await client.RaiseEventAsync(body.InstanceId, "StopWait", null);
                }
            }

            var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await resp.WriteStringAsync($"Processed: {result.SerializedOutput}");
            return resp;
        }

        [Function("UserAcceptOrchestrator")]
        public static async Task<string> UserAcceptOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var input = context.GetInput<UserAcceptInput>();
            return await context.Entities.CallEntityAsync<string>(new EntityInstanceId(nameof(OnCallEntity), input.MainInstanceId), "UserAccepted", input);
        }

        [Function("UserDeclineOrchestrator")]
        public static async Task<string> UserDeclineOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var input = context.GetInput<UserAcceptInput>();
            return await context.Entities.CallEntityAsync<string>(new EntityInstanceId(nameof(OnCallEntity), input.MainInstanceId), "UserDeclined", input);
        }

        // ------------------------------------------------------------------
        // 5. HELPER METHODS
        // ------------------------------------------------------------------
        private static void LogCurrentStatus(ILogger logger, OnCallEntity state)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var req in state.Requirements)
            {
                int got = state.AcceptedCount.ContainsKey(req.Key) ? state.AcceptedCount[req.Key] : 0;
                int needed = req.Value - got;
                string color = needed > 0 ? "\u001b[33m" : "\u001b[32m";
                sb.Append($"{color}[{req.Key}: Need {needed} (Got {got}/{req.Value})]\u001b[0m ");
            }
            logger.LogInformation($"\u001b[36m STATUS: {sb}\u001b[0m");
        }

        private static bool IsMissionComplete(OnCallEntity state)
        {
            return state.Requirements.All(r => (state.AcceptedCount.ContainsKey(r.Key) ? state.AcceptedCount[r.Key] : 0) >= r.Value);
        }

        private static void GenerateFinalReport(ILogger logger, OnCallEntity finalState, bool isSuccess, string reason)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"\u001b[32m=== FINAL REPORT ({isSuccess}) : {reason} ===\u001b[0m");
            foreach (var group in finalState.Requirements.Keys)
            {
                int got = finalState.AcceptedCount.ContainsKey(group) ? finalState.AcceptedCount[group] : 0;
                sb.AppendLine($"\u001b[32m{group}: Got {got}/{finalState.Requirements[group]}\u001b[0m");
            }
            logger.LogInformation(sb.ToString());
        }
    }
}