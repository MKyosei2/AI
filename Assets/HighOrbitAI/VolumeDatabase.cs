using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// VolumeLiteの集約と、StaticBVH + DynamicHash を統一した問い合わせ窓口。
    /// </summary>
    public class VolumeDatabase
    {
        public readonly List<VolumeLite> volumes = new List<VolumeLite>(4096);

        // どのvolumeが静的/動的かを分けて登録
        readonly List<int> staticIds = new List<int>(4096);
        readonly List<int> dynamicIds = new List<int>(1024);

        StaticBVH staticBvh;
        SpatialHash3D dynamicHash;

        // バッファ（GC削減）
        readonly List<int> tmpIds = new List<int>(256);

        public void Initialize(float dynamicCellSize)
        {
            dynamicHash = new SpatialHash3D(dynamicCellSize);
        }

        public int AddVolume(VolumeLite v)
        {
            int id = volumes.Count;
            volumes.Add(v);
            return id;
        }

        public void RegisterStatic(int id) => staticIds.Add(id);

        public void RegisterDynamic(int id) => dynamicIds.Add(id);

        public void BuildStaticBVH()
        {
            staticBvh = new StaticBVH(volumes);
            staticBvh.Build(staticIds);
        }

        public void RebuildDynamicHash()
        {
            dynamicHash.Clear();
            for (int i = 0; i < dynamicIds.Count; i++)
            {
                int id = dynamicIds[i];
                dynamicHash.Insert(id, volumes[id].aabb);
            }
        }

        public void UpdateDynamicBounds(int id, Bounds newBounds)
        {
            var v = volumes[id];
            v.aabb = newBounds;
            volumes[id] = v;
        }

        /// <summary>
        /// 点が侵入禁止か/通行不可か/追加コストはいくらか を返す
        /// </summary>
        public void EvaluatePoint(Vector3 p, float queryRadius, out NavFlags flags, out float costAdd)
        {
            flags = NavFlags.None;
            costAdd = 0f;

            Bounds area = new Bounds(p, Vector3.one * (queryRadius * 2f));

            // Static
            tmpIds.Clear();
            staticBvh?.QueryAabb(area, tmpIds);
            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if (!vol.aabb.Contains(p)) continue;
                flags |= vol.flags;
                costAdd += vol.costAdd;
                if ((flags & NavFlags.KeepOut) != 0) return; // 侵入禁止なら早期
            }

            // Dynamic
            tmpIds.Clear();
            dynamicHash?.Query(area, tmpIds);
            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if (!vol.aabb.Contains(p)) continue;
                flags |= vol.flags;
                costAdd += vol.costAdd;
                if ((flags & NavFlags.KeepOut) != 0) return;
            }
        }

        /// <summary>
        /// 線分が禁止/障害物に当たるか（Cruiseのエッジ検証用）
        /// </summary>
        public bool SegmentHitsHard(Vector3 p0, Vector3 p1)
        {
            if (staticBvh == null) return false;

            tmpIds.Clear();
            staticBvh.QuerySegment(p0, p1, tmpIds);
            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if ((vol.flags & (NavFlags.Blocked | NavFlags.KeepOut)) == 0) continue;
                if (GeometryUtil.SegmentIntersectsAabb(p0, p1, vol.aabb))
                    return true;
            }
            return false;
        }

        /// <summary>線分上をサンプルしてSoftAvoidの追加コストを見積もる</summary>
        public float EstimateSoftCostOnSegment(Vector3 p0, Vector3 p1, int samples = 5, float queryRadius = 0.5f)
        {
            if (samples <= 0) return 0f;
            float sum = 0f;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 p = Vector3.Lerp(p0, p1, t);
                EvaluatePoint(p, queryRadius, out var flags, out float cost);
                if ((flags & NavFlags.SoftAvoid) != 0) sum += cost;
            }
            return sum;
        }
    }
}
