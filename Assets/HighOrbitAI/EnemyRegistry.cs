using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public static class EnemyRegistry
    {
        static readonly List<Transform> enemies = new List<Transform>(256);

        public static void Register(Transform t)
        {
            if (t == null) return;
            if (!enemies.Contains(t)) enemies.Add(t);
        }

        public static void Unregister(Transform t)
        {
            if (t == null) return;
            enemies.Remove(t);
        }

        public static IReadOnlyList<Transform> GetAll() => enemies;
    }
}
