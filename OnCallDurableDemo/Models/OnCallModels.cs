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
        public string UserId { get; set; }
        public string InstanceId { get; set; }
    }
}
