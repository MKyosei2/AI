using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// すべての判定（障害物/禁止/避けたい）を AABB + flags + cost に統一した最小表現。
    /// </summary>
    [System.Serializable]
    public struct VolumeLite
    {
        public Bounds aabb;
        public NavFlags flags;
        public float costAdd; // SoftAvoid用（0でもOK）

        public VolumeLite(Bounds bounds, NavFlags flags, float costAdd = 0f)
        {
            this.aabb = bounds;
            this.flags = flags;
            this.costAdd = costAdd;
        }
    }
}
