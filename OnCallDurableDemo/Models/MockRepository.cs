using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OnCallDurableDemo.Models
{
    public static class MockRepository
    {
        public static OnCallConfig GetConfigFromDb()
        {
            return new OnCallConfig
            {
                Requirements = new Dictionary<string, int>
                {
                    { "GroupA", 1 }
                    //, { "GroupB", 2 }
                    //, { "GroupC", 1 }
                },
                Steps = new List<StepRule>
                {
                    // Step 1: Parallel (Voice -> SMS)
                    new StepRule
                    {
                        StepNumber = 1,
                        IsParallel = true,
                        Actions = new List<StepAction>
                        {
                            new StepAction { Mode = "Voice", WaitTimeMinutes = 1, RepeatCount = 0 },
                            //new StepAction { Mode = "Sms", WaitTimeMinutes = 1, RepeatCount = 1 }
                        }
                    },
                    // Step 2: Serial (Voice -> SMS)
                    new StepRule
                    {
                        StepNumber = 2,
                        IsParallel = false,
                        Actions = new List<StepAction>
                        {
                            new StepAction { Mode = "Voice", WaitTimeMinutes = 1, RepeatCount = 0 },
                            //new StepAction { Mode = "Sms", WaitTimeMinutes = 1, RepeatCount = 0 }
                        }
                    }
                }
            };
        }

        public static List<UserProfile> GetAllUsers()
        {
            var users = new List<UserProfile>();
            for (int i = 1; i <= 1; i++) users.Add(new UserProfile { Id = $"A{i}", Name = $"User A-{i}", Group = "GroupA" });
            for (int i = 1; i <= 3; i++) users.Add(new UserProfile { Id = $"B{i}", Name = $"User B-{i}", Group = "GroupB" });
            for (int i = 1; i <= 2; i++) users.Add(new UserProfile { Id = $"C{i}", Name = $"User C-{i}", Group = "GroupC" });
            return users;
        }
    }
}
