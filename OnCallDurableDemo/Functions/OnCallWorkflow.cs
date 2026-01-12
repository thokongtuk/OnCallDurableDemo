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

            // 🎯 GLOBAL LISTENER
            var globalStopSignalTask = context.WaitForExternalEvent<string>("StopWait");

            // ================= STEP LOOP =================
            foreach (var step in config.Steps.OrderBy(s => s.StepNumber))
            {
                if (isMissionSuccess) break;
                logger.LogInformation($"\u001b[35m===== STARTING STEP {step.StepNumber} =====\u001b[0m");

                // ================= ACTION LOOP =================
                foreach (var action in step.Actions)
                {
                    if (isMissionSuccess) break;
                    logger.LogInformation($"\u001b[33m--- Starting Action Mode: {action.Mode} ---\u001b[0m");

                    // ================= 🔄 ATTEMPT LOOP (RETRY) =================
                    for (int attempt = 0; attempt <= action.RepeatCount; attempt++)
                    {
                        if (isMissionSuccess) break;

                        string attemptLog = action.RepeatCount > 0 ? $"(Attempt {attempt + 1}/{action.RepeatCount + 1})" : "";
                        logger.LogInformation($"\u001b[36m>>> Starting Cycle: {action.Mode} {attemptLog}\u001b[0m");

                        // ❗ Reset Memory ที่นี่! 
                        // เพื่อให้ในรอบ Repeat (Attempt 2) ระบบจะ "ลืม" ว่าเคยดึง A1-A4 ไปแล้ว
                        // และยอมดึง A1-A4 ออกมาให้เราโทรซ้ำอีกรอบ
                        await context.Entities.CallEntityAsync(entityId, "ResetStepMemory");

                        int batchRound = 1;

                        // ================= BATCH LOOP (Inside Attempt) =================
                        while (true)
                        {
                            // 1. Check Quota
                            var currentState = await context.Entities.CallEntityAsync<OnCallEntity>(entityId, "GetState");
                            LogCurrentStatus(logger, currentState);

                            if (IsMissionComplete(currentState))
                            {
                                isMissionSuccess = true;
                                endReason = "Mission Complete";
                                goto EndOfWorkflow;
                            }

                            // 2. Get Users (จะดึงคนเดิมได้ เพราะเรา ResetStepMemory แล้วข้างบน)
                            var usersInBatch = await context.Entities.CallEntityAsync<List<string>>(entityId, "GetBatchUsers", step.IsParallel);

                            if (usersInBatch.Count == 0)
                            {
                                // หมดคนในรอบนี้แล้ว (ครบ A1..A5 แล้ว) -> จบ Batch Loop เพื่อไปเริ่ม Attempt ถัดไป (ถ้ามี)
                                logger.LogInformation($"   [Cycle Finished] No more candidates for {action.Mode} {attemptLog}.");
                                break;
                            }

                            logger.LogInformation($"   [Batch {batchRound}] Processing: {string.Join(", ", usersInBatch)}");

                            // Filter Pending (ใครที่รับงานไปแล้ว จะถูกตัดออกที่นี่)
                            var pendingUsers = await context.Entities.CallEntityAsync<List<string>>(entityId, "FilterPendingUsers", usersInBatch);

                            if (pendingUsers.Count == 0)
                            {
                                logger.LogInformation($"   ✅ Batch {batchRound} fully responded. Next Batch.");
                                batchRound++;
                                continue; // ไป Batch ถัดไป
                            }

                            // Execute Activity
                            logger.LogInformation($"      👉 Sending {action.Mode} {attemptLog} to {pendingUsers.Count} users...");
                            await context.CallActivityAsync("Activity_SimulateTwilioCall", new TwilioInput()
                            {
                                Mode = action.Mode,
                                UserIds = pendingUsers,
                                InstanceId = context.InstanceId
                            });

                            // --- WAIT LOGIC ---
                            if (action.WaitTimeMinutes > 0)
                            {
                                var waitStartTime = context.CurrentUtcDateTime;
                                var expiryTime = context.CurrentUtcDateTime.AddMinutes(action.WaitTimeMinutes);
                                logger.LogInformation($"         ⏳ Waiting {action.WaitTimeMinutes} mins...");

                                using var cts = new CancellationTokenSource();
                                var timerTask = context.CreateTimer(expiryTime, CancellationToken.None);

                                var winner = await Task.WhenAny(timerTask, globalStopSignalTask);

                                if (winner == globalStopSignalTask)
                                {
                                    bool isReallyComplete = await context.Entities.CallEntityAsync<bool>(entityId, "IsMissionComplete");
                                    if (isReallyComplete)
                                    {
                                        logger.LogInformation($"         ⚡ STOP Verified! Closing Job.");
                                        cts.Cancel();
                                        isMissionSuccess = true;
                                        goto EndOfWorkflow;
                                    }
                                    else
                                    {
                                        logger.LogWarning($"         ⚠️ Signal received but not complete. Waiting timer...");
                                        globalStopSignalTask = context.WaitForExternalEvent<string>("StopWait");
                                        await timerTask;
                                    }
                                }
                                else
                                {
                                    logger.LogInformation($"         ⏰ Timer expired.");
                                }
                            }
                            // ------------------

                            batchRound++;

                        } // End Batch Loop (While True)

                    } // End Attempt Loop

                } // End Action Loop

                logger.LogInformation($"[Step {step.StepNumber}] All Actions Finished.");

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
        // 4. WEBHOOK & MEDIATOR (COMPLETE VERSION)
        // ------------------------------------------------------------------
        [Function("HandleOnCallResponse")]
        public static async Task<HttpResponseData> Webhook(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/response")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext ctx)
        {
            var logger = ctx.GetLogger("Webhook");

            var body = await req.ReadFromJsonAsync<WebhookRequest>();
            if (body == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

            // =================================================================================
            // 🎨 LOG 1: Status Color Logic (เขียว/แดง เฉพาะคำ)
            // =================================================================================
            string statusWithColor;
            if (body.Status == 1)
            {
                // \u001b[32m = เขียว, \u001b[36m = ฟ้า (สี Base ของบรรทัดนี้)
                statusWithColor = "\u001b[32mAvailable\u001b[36m (Accepted)";
            }
            else
            {
                // \u001b[31m = แดง, \u001b[36m = ฟ้า
                statusWithColor = "\u001b[31mUnavailable\u001b[36m (Declined)";
            }

            // Log เป็นสีฟ้า (Cyan) ทั้งบรรทัด แต่คำสถานะจะเด้งสีตาม Logic ด้านบน
            logger.LogInformation($"\u001b[36m[Webhook] Received for User: {body.UserId} | Status: {statusWithColor} ({body.Status})\u001b[0m");

            // =================================================================================
            // 🧩 GROUP MAPPING LOGIC
            // =================================================================================
            string group = "Unknown";
            if (body.UserId.StartsWith("A")) group = "GroupA";
            else if (body.UserId.StartsWith("B")) group = "GroupB";
            else if (body.UserId.StartsWith("C")) group = "GroupC";

            if (group == "Unknown")
            {
                logger.LogError($"[Webhook] Error: Could not determine group for User {body.UserId}");
            }
            else
            {
                logger.LogInformation($"[Webhook] User {body.UserId} mapped to group: {group}");
            }

            // =================================================================================
            // 🔄 CALL MEDIATOR (ENTITY INTERACTION)
            // =================================================================================
            var mediatorName = body.Status == 1 ? "UserAcceptOrchestrator" : "UserDeclineOrchestrator";
            var input = new UserAcceptInput
            {
                Group = group,
                UserId = body.UserId,
                MainInstanceId = body.InstanceId
            };

            string opId = await client.ScheduleNewOrchestrationInstanceAsync(mediatorName, input);

            // รอผลลัพธ์จาก Mediator (Entity update)
            var result = await client.WaitForInstanceCompletionAsync(opId, true, CancellationToken.None);

            if (result.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
            {
                var outputString = result.ReadOutputAs<string>();

                // Log Result Color (เขียว/แดง ตามผลลัพธ์ Entity)
                if (outputString != null && outputString.StartsWith("Error"))
                    logger.LogInformation($"\u001b[31m[Webhook] Result from Entity: {outputString}\u001b[0m");
                else
                    logger.LogInformation($"\u001b[32m[Webhook] Result from Entity: {outputString}\u001b[0m");

                // =================================================================================
                // 🚀 STOP SIGNAL LOGIC (WITH SAFE GUARD)
                // =================================================================================
                if (outputString != null && outputString.Contains("MissionComplete"))
                {
                    logger.LogInformation($"[Webhook] 🚀 Mission Complete detected! Raising 'StopWait' to {body.InstanceId}");

                    try
                    {
                        // ส่ง Event มาตรฐาน "StopWait" ไปบอก Orchestrator หลัก
                        await client.RaiseEventAsync(body.InstanceId, "StopWait", "STOP");
                    }
                    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
                    {
                        // ⚠️ ถ้า Orchestrator จบไปแล้ว (เช่น โควตาเต็มพอดีกับที่มีคนกดมาพร้อมกัน)
                        // ให้ถือว่าเป็นเรื่องปกติ ไม่ต้อง throw error ให้รก Log
                        logger.LogWarning($"[Webhook] ⚠️ Orchestrator {body.InstanceId} has already completed or failed. Signal ignored.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[Webhook] ❌ Unexpected error raising event: {ex.Message}");
                    }
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