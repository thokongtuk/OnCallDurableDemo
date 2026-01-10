using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OnCallDurableDemo.Models;

namespace OnCallDurableDemo.Entities
{
    [JsonObject(MemberSerialization.OptIn)]
    public class OnCallEntity
    {
        // --- State Properties ---
        [JsonProperty("req")] 
        public Dictionary<string, int> Requirements { get; set; }
        [JsonProperty("acceptedCount")] 
        public Dictionary<string, int> AcceptedCount { get; set; } = new Dictionary<string, int>();
        [JsonProperty("called")] 
        public HashSet<string> CalledUsers { get; set; } = new HashSet<string>();
        [JsonProperty("currentBatchUsers")]
        public HashSet<string> CurrentBatchUsers { get; set; } = new HashSet<string>();
        [JsonProperty("acceptedUsers")] 
        public HashSet<string> AcceptedUsers { get; set; } = new HashSet<string>();
        [JsonProperty("declinedUsers")] 
        public HashSet<string> DeclinedUsers { get; set; } = new HashSet<string>();

        // --- Init & Reset ---
        public void Initialize(Dictionary<string, int> req)
        {
            Requirements = req;
            AcceptedCount = req.Keys.ToDictionary(k => k, k => 0);
            CalledUsers = new HashSet<string>();
            AcceptedUsers = new HashSet<string>();
            DeclinedUsers = new HashSet<string>();
            CurrentBatchUsers = new HashSet<string>();
        }

        public void ResetStepMemory()
        {
            CalledUsers.Clear();
            CurrentBatchUsers.Clear();
        }

        public void Delete()
        {
            Requirements = null; AcceptedCount = null; CalledUsers = null;
            AcceptedUsers = null; DeclinedUsers = null; CurrentBatchUsers = null;
        }

        // --- Core Logic ---
        public List<string> GetBatchUsers(bool isParallel)
        {
            CurrentBatchUsers.Clear();

            var allUsers = MockRepository.GetAllUsers();
            var batch = new List<string>();

            if (Requirements == null) return batch;

            foreach (var group in Requirements.Keys)
            {
                int current = AcceptedCount.ContainsKey(group) ? AcceptedCount[group] : 0;
                int needed = Requirements[group] - current;
                if (needed <= 0) continue;

                int takeCount = isParallel ? needed : 1;

                var candidates = allUsers
                    .Where(u => u.Group == group &&
                                !CalledUsers.Contains(u.Id) &&
                                !AcceptedUsers.Contains(u.Id) &&
                                !DeclinedUsers.Contains(u.Id))
                    .Select(u => u.Id)
                    .Take(takeCount)
                    .ToList();

                foreach (var uid in candidates)
                {
                    batch.Add(uid);
                    CalledUsers.Add(uid);
                    CurrentBatchUsers.Add(uid);
                }
            }
            return batch;
        }

        public List<string> FilterPendingUsers(List<string> currentBatchUsers)
        {
            return currentBatchUsers
                .Where(userId => !AcceptedUsers.Contains(userId) && !DeclinedUsers.Contains(userId))
                .ToList();
        }

        public bool IsMissionComplete()
        {
            if (Requirements == null) return false;
            return Requirements.All(r => (AcceptedCount.ContainsKey(r.Key) ? AcceptedCount[r.Key] : 0) >= r.Value);
        }

        // --- User Interaction ---
        public Task<string> UserAccepted(UserAcceptInput input)
        {
            if (!CurrentBatchUsers.Contains(input.UserId))
            {
                return Task.FromResult("Error:ResponseExpired (Not in current batch)");
            }

            if (AcceptedUsers.Contains(input.UserId) || DeclinedUsers.Contains(input.UserId))
            {
                return Task.FromResult("Error:AlreadyResponded");
            }

            if (DeclinedUsers.Contains(input.UserId)) DeclinedUsers.Remove(input.UserId);

            if (!AcceptedUsers.Contains(input.UserId))
            {
                if (AcceptedCount.ContainsKey(input.Group))
                {
                    if (AcceptedCount[input.Group] >= Requirements[input.Group]) return Task.FromResult("Error:QuotaFull");
                    AcceptedCount[input.Group]++;
                    AcceptedUsers.Add(input.UserId);
                }
            }
            if (IsMissionComplete()) return Task.FromResult("MissionComplete");
            return Task.FromResult("Success");
        }

        public Task<string> UserDeclined(UserAcceptInput input)
        {
            if (!CurrentBatchUsers.Contains(input.UserId))
            {
                return Task.FromResult("Error:ResponseExpired (Not in current batch)");
            }

            if (AcceptedUsers.Contains(input.UserId) || DeclinedUsers.Contains(input.UserId))
            {
                return Task.FromResult("Error:AlreadyResponded");
            }
            DeclinedUsers.Add(input.UserId);
            return Task.FromResult("Success");
        }

        public Task<OnCallEntity> GetState() => Task.FromResult(this);

        [Function(nameof(OnCallEntity))]
        public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<OnCallEntity>();
    }
}