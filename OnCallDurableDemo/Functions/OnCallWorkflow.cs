using DurableTask.Core.Entities;
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

        [Function("OnCallOrchestrator")]
        public static async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger("OnCallOrchestrator");

            var config = await context.CallActivityAsync<OnCallConfig>("Activity_GetConfig");
            var entityId = new EntityInstanceId(nameof(OnCallEntity), context.InstanceId);
            await context.Entities.CallEntityAsync(entityId, "Initialize", config.Requirements);

            bool isMissionSuccess = false;
            string endReason = "Workflow Finished";

            // ==================================================================================
            // 🎯 GLOBAL STOP LISTENER: ประกาศตัวแปรรอรับสัญญาณไว้นอก Loop (ระดับ Step)
            // ==================================================================================
            // สร้าง Task รอรับ Event "StopWait" เตรียมไว้ก่อนเลย
            var globalStopSignalTask = context.WaitForExternalEvent<string>("StopWait");

            foreach (var step in config.Steps.OrderBy(s => s.StepNumber))
            {
                if (isMissionSuccess) break;

                logger.LogInformation($"\u001b[35m===== STARTING STEP {step.StepNumber} =====\u001b[0m");

                await context.Entities.CallEntityAsync(entityId, "ResetStepMemory");

                int batchRound = 1;
                while (true) // --- BATCH LOOP ---
                {
                    // 1. Check Quota (เช็คก่อนเริ่ม)
                    var currentState = await context.Entities.CallEntityAsync<OnCallEntity>(entityId, "GetState");
                    LogCurrentStatus(logger, currentState);

                    if (IsMissionComplete(currentState))
                    {
                        isMissionSuccess = true;
                        endReason = "Mission Complete";
                        goto EndOfWorkflow;
                    }

                    // 2. Get Users
                    var usersInBatch = await context.Entities.CallEntityAsync<List<string>>(entityId, "GetBatchUsers", step.IsParallel);
                    if (usersInBatch.Count == 0)
                    {
                        logger.LogWarning($"[Step {step.StepNumber}] No more candidates. Next step.");
                        break;
                    }
                    logger.LogInformation($"--- [Step {step.StepNumber} | Batch {batchRound}] ---");

                    foreach (var action in step.Actions) // --- ACTION LOOP ---
                    {
                        if (isMissionSuccess) break;

                        for (int i = 0; i <= action.RepeatCount; i++) // --- RETRY LOOP ---
                        {
                            // Double Check
                            bool isComplete = await context.Entities.CallEntityAsync<bool>(entityId, "IsMissionComplete");
                            if (isComplete) { isMissionSuccess = true; endReason = "Mission Complete"; goto EndOfWorkflow; }

                            var pendingUsers = await context.Entities.CallEntityAsync<List<string>>(entityId, "FilterPendingUsers", usersInBatch);
                            if (pendingUsers.Count == 0) { goto EndOfBatch; }

                            // Execute Activity
                            string attemptInfo = action.RepeatCount > 0 ? $"(Attempt {i + 1}/{action.RepeatCount + 1})" : "";
                            logger.LogInformation($"   👉 Action: {action.Mode} {attemptInfo} -> Sending... | Processing: {string.Join(", ", pendingUsers)}");

                            // ✅ ใช้ InstanceId ปกติ (ไม่ต้อง Dynamic)
                            await context.CallActivityAsync("Activity_SimulateTwilioCall", new TwilioInput()
                            {
                                Mode = action.Mode,
                                UserIds = pendingUsers,
                                InstanceId = context.InstanceId
                            });

                            // ---------------------------------------------------------------
                            // 🔥 WAIT LOGIC (ใช้ Global Listener)
                            // ---------------------------------------------------------------
                            if (action.WaitTimeMinutes > 0)
                            {
                                var waitStartTime = context.CurrentUtcDateTime;
                                var expiryTime = context.CurrentUtcDateTime.AddMinutes(action.WaitTimeMinutes);
                                logger.LogInformation($"\n      ⏳ Waiting {action.WaitTimeMinutes} mins (Using Global Listener)...");

                                // สร้าง Timer เฉพาะกิจสำหรับรอบนี้
                                using var cts = new CancellationTokenSource();
                                var timerTask = context.CreateTimer(expiryTime, CancellationToken.None);

                                // 🏁 RACE: แข่งกันระหว่าง "Timer ของรอบนี้" vs "Stop Signal ที่รอมาตั้งแต่ต้น"
                                var winner = await Task.WhenAny(timerTask, globalStopSignalTask);

                                if (winner == globalStopSignalTask)
                                {
                                    // 🛑 ได้รับสัญญาณ STOP! (ไม่ว่าจะมาจาก Batch ไหนก็ตาม)
                                    // เนื่องจาก Task นี้ถูกประกาศไว้นอก Loop มันจึงรับสัญญาณได้ตลอดเวลา

                                    bool isReallyComplete = await context.Entities.CallEntityAsync<bool>(entityId, "IsMissionComplete");
                                    if (isReallyComplete)
                                    {
                                        var timeSpent = context.CurrentUtcDateTime - waitStartTime;
                                        logger.LogInformation($"      ⚡ STOP Verified! (Waited: {timeSpent.TotalSeconds:F2}s). Closing Job.");

                                        cts.Cancel(); // ฆ่า Timer ทิ้ง
                                        isMissionSuccess = true;
                                        goto EndOfWorkflow;
                                    }
                                    else
                                    {
                                        // ⚠️ สัญญาณหลอก (False Alarm) หรือ สัญญาณเก่า
                                        logger.LogWarning($"      ⚠️ Signal received but job NOT complete. Resetting Listener...");

                                        // Reset Listener: สร้างตัวรอรับใหม่ เพื่อรอสัญญาณครั้งถัดไป
                                        // (เพราะตัวเก่า Completed ไปแล้ว เราต้องสร้างใหม่เพื่อให้รอต่อได้)
                                        globalStopSignalTask = context.WaitForExternalEvent<string>("StopWait");

                                        // ❗ สำคัญ: เราไม่ Break Loop ตรงนี้ แต่เราจะวนกลับไปเช็ค Timer ต่อ
                                        // ในทางปฏิบัติ การเรียก WhenAny ใหม่กับ Timer เดิมที่ยังไม่หมดเวลา ทำได้เลย
                                        // แต่เพื่อความง่าย เราจะข้ามไปรอบถัดไปเลยก็ได้ หรือจะรอ Timer ต่อก็ได้
                                        // ในที่นี้ขอเลือก "รอจน Timer หมด" เพื่อความชัวร์
                                        await timerTask;
                                        logger.LogInformation($"      ⏰ Timer expired (after false alarm).");
                                    }
                                }
                                else
                                {
                                    // ⏰ Timer ชนะ (หมดเวลา)
                                    // สังเกตว่าเรา *ไม่* Cancel globalStopSignalTask เพราะเราจะใช้มันต่อใน Batch หน้า!
                                    logger.LogInformation($"      ⏰ Timer expired.");
                                }
                            }
                            // ---------------------------------------------------------------
                        }
                    }

                    EndOfBatch:
                    batchRound++;
                } // End Batch Loop

                logger.LogInformation($"[Step {step.StepNumber}] Step Finished.");
            } // End Step Loop

            EndOfWorkflow:
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

                logger.LogInformation($"\u001b[33m      📞 [Dispatch] Mode: {input.Mode} | Count: {userPhonenumbers.Count} Targets:\u001b[0m");
                foreach (var x in userPhonenumbers)
                {
                    // สั่ง Log แยกคำสั่งกัน บรรทัดใครบรรทัดมัน
                    logger.LogInformation($"\u001b[33m         ➡️  {x.User} [{x.Phone}]\u001b[0m");
                }
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
                var outputString = result.ReadOutputAs<string>();

                // Log Result Color
                if (outputString != null && outputString.StartsWith("Error"))
                    logger.LogInformation($"\u001b[31m[Webhook] Result from Entity: {outputString}\u001b[0m");
                else
                    logger.LogInformation($"\u001b[32m[Webhook] Result from Entity: {outputString}\u001b[0m");

                // ✅ CLEAN LOGIC: ถ้า Entity บอกว่าครบ -> ส่ง "StopWait" ไปบอก Orchestrator
                if (outputString != null && outputString.Contains("MissionComplete"))
                {
                    logger.LogInformation($"[Webhook] 🚀 Mission Complete! Raising 'StopWait' to {body.InstanceId}");
                    await client.RaiseEventAsync(body.InstanceId, "StopWait", "STOP");
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