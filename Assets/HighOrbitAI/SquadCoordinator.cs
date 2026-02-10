using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public static class SquadCoordinator
    {
        struct AgentState
        {
            public int agentId;
            public SquadRole role;
            public Transform currentTarget;
            public float lastUpdateTime;
        }

        static readonly Dictionary<string, Dictionary<int, AgentState>> squads
            = new Dictionary<string, Dictionary<int, AgentState>>(64);

        const float DefaultStaleSeconds = 1.5f;

        static Dictionary<int, AgentState> GetOrCreateSquad(string squadId)
        {
            if (string.IsNullOrEmpty(squadId)) squadId = "_default";
            if (!squads.TryGetValue(squadId, out var map))
            {
                map = new Dictionary<int, AgentState>(32);
                squads[squadId] = map;
            }
            return map;
        }

        public static void Register(string squadId, int agentId, SquadRole role)
        {
            var map = GetOrCreateSquad(squadId);
            map[agentId] = new AgentState
            {
                agentId = agentId,
                role = role,
                currentTarget = null,
                lastUpdateTime = Time.time
            };
        }

        public static void Unregister(string squadId, int agentId)
        {
            if (string.IsNullOrEmpty(squadId)) squadId = "_default";
            if (squads.TryGetValue(squadId, out var map))
            {
                map.Remove(agentId);
                if (map.Count == 0) squads.Remove(squadId);
            }
        }

        public static void UpdateAssignment(string squadId, int agentId, Transform target)
        {
            var map = GetOrCreateSquad(squadId);
            if (!map.TryGetValue(agentId, out var s))
                s = new AgentState { agentId = agentId, role = SquadRole.Interceptor };

            s.currentTarget = target;
            s.lastUpdateTime = Time.time;
            map[agentId] = s;
        }

        public static int CountAssigned(string squadId, Transform target, float staleSeconds = DefaultStaleSeconds)
        {
            if (target == null) return 0;
            if (string.IsNullOrEmpty(squadId)) squadId = "_default";
            if (!squads.TryGetValue(squadId, out var map)) return 0;

            float now = Time.time;
            int count = 0;
            foreach (var kv in map)
            {
                var s = kv.Value;
                if ((now - s.lastUpdateTime) > staleSeconds) continue;
                if (s.currentTarget == target) count++;
            }
            return count;
        }

        public static int CountAssignedByRole(string squadId, Transform target, SquadRole role, float staleSeconds = DefaultStaleSeconds)
        {
            if (target == null) return 0;
            if (string.IsNullOrEmpty(squadId)) squadId = "_default";
            if (!squads.TryGetValue(squadId, out var map)) return 0;

            float now = Time.time;
            int count = 0;
            foreach (var kv in map)
            {
                var s = kv.Value;
                if ((now - s.lastUpdateTime) > staleSeconds) continue;
                if (s.role != role) continue;
                if (s.currentTarget == target) count++;
            }
            return count;
        }
    }
}
