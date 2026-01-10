using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OnCallDurableDemo.Models
{
    public class OnCallConfig
    {
        public Dictionary<string, int> Requirements { get; set; }
        public List<StepRule> Steps { get; set; }
    }

    public class StepRule
    {
        public int StepNumber { get; set; }
        public bool IsParallel { get; set; }
        public List<StepAction> Actions { get; set; } = new List<StepAction>();
    }

    public class StepAction
    {
        public string Mode { get; set; } // "Voice", "Sms"
        public int WaitTimeMinutes { get; set; }
        public int RepeatCount { get; set; }
    }

    public class UserProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
    }

    public class UserAcceptInput
    {
        public string UserId { get; set; }
        public string Group { get; set; }
        public string MainInstanceId { get; set; }
    }

    public class WebhookRequest
    {
        public string InstanceId { get; set; }
        public string UserId { get; set; }
        public int Status { get; set; }
    }

    public class TwilioInput
    {
        public List<string> UserIds { get; set; }
        public string Mode { get; set; }
        public string InstanceId { get; set; }
    }

    public class UserPhoneNumber
    {
        public string User { get; set; }
        public string Phone { get; set; }
    }

    public static class UserInfo
    {
        public static readonly Dictionary<string, string> UserPhoneNumber = new()
        {
            { "A1", "+15550000014" }, { "A2", "+15550000014" }, { "A3", "+15550000014" },
            { "A4", "+15550000014" }, { "A5", "+15550000014" }, { "A6", "+15550000014" },
            { "B1", "+15550000014" }, { "B2", "+15550000014" }, { "B3", "+15550000014" },
            { "B4", "+15550000014" }, { "B5", "+15550000014" }, { "B6", "+15550000014" },
            { "C1", "+15550000014" }, {"C2", "+15550000014" }
        };
    }
}
