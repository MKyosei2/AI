using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// VolumeLite（AABB + flags + cost）を一元管理するDB。
    /// - 静的：BVH
    /// - 動的：SpatialHash（AABB更新）
    ///
    /// 条件付き（ドア/液体など）も “動的” として登録し、
    /// flags/cost を Update で差し替える（Collider enable/disable を前提にしない）。
    /// </summary>
    public class VolumeDatabase
    {
        public readonly List<VolumeLite> volumes = new List<VolumeLite>(4096);

        readonly List<int> staticIds = new List<int>(4096);
        readonly List<int> dynamicIds = new List<int>(1024);

        StaticBVH staticBvh;
        SpatialHash3D dynamicHash;

        readonly List<int> tmpIds = new List<int>(256);

        /// <summary>
        /// DB内容が変わるたびに増えるリビジョン。
        /// ルートAI側で「障害物状態が変わった」検知に使える。
        /// </summary>
        public int revision { get; private set; } = 0;

        public void Initialize(float dynamicCellSize)
        {
            dynamicHash = new SpatialHash3D(dynamicCellSize);
        }

        public void ClearAll()
{
    volumes.Clear();
    staticIds.Clear();
    dynamicIds.Clear();
    staticBvh = null;
    dynamicHash?.Clear();
    revision++;
}

public int AddVolume(VolumeLite v)
        {
            int id = volumes.Count;
            volumes.Add(v);
            revision++;
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
            if (dynamicHash == null) return;

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
            revision++;
        }

        public void UpdateDynamicState(int id, NavFlags flags, float costAdd)
        {
            var v = volumes[id];
            v.flags = flags;
            v.costAdd = costAdd;
            volumes[id] = v;
            revision++;
        }

        public void UpdateDynamicAll(int id, Bounds newBounds, NavFlags flags, float costAdd)
        {
            var v = volumes[id];
            v.aabb = newBounds;
            v.flags = flags;
            v.costAdd = costAdd;
            volumes[id] = v;
            revision++;
        }

        public void EvaluatePoint(Vector3 p, float queryRadius, out NavFlags flags, out float costAdd)
        {
            flags = NavFlags.None;
            costAdd = 0f;

            Bounds area = new Bounds(p, Vector3.one * (queryRadius * 2f));

            // static
            tmpIds.Clear();
            staticBvh?.QueryAabb(area, tmpIds);
            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if (vol.flags == NavFlags.None) continue;
                if (!vol.aabb.Contains(p)) continue;

                flags |= vol.flags;
                costAdd += vol.costAdd;

                if ((flags & NavFlags.KeepOut) != 0) return;
            }

            // dynamic (including conditional)
            tmpIds.Clear();
            dynamicHash?.Query(area, tmpIds);
            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if (vol.flags == NavFlags.None) continue;
                if (!vol.aabb.Contains(p)) continue;

                flags |= vol.flags;
                costAdd += vol.costAdd;

                if ((flags & NavFlags.KeepOut) != 0) return;
            }
        }

        /// <summary>
        /// 静的のみの“硬い衝突”判定（互換用）
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

        /// <summary>
        /// 静的＋動的（条件付き含む）の“硬い衝突”判定。
        /// ドアが閉まった/液体が通行不可になった等を次の探索で自然に回避できる。
        /// </summary>
        public bool SegmentHitsHardAny(Vector3 p0, Vector3 p1)
        {
            if (SegmentHitsHard(p0, p1)) return true;
            return SegmentHitsHardDynamic(p0, p1);
        }

        /// <summary>
        /// 動的（SpatialHash）に対して線分衝突チェック。
        /// ※SpatialHashはsegment queryを持たないので、segment AABBで候補を絞ってから精密判定する。
        /// </summary>
        public bool SegmentHitsHardDynamic(Vector3 p0, Vector3 p1)
        {
            if (dynamicHash == null) return false;

            // segment AABB
            Vector3 min = Vector3.Min(p0, p1);
            Vector3 max = Vector3.Max(p0, p1);
            Bounds area = new Bounds((min + max) * 0.5f, (max - min) + Vector3.one * 0.01f);

            tmpIds.Clear();
            dynamicHash.Query(area, tmpIds);

            for (int i = 0; i < tmpIds.Count; i++)
            {
                var vol = volumes[tmpIds[i]];
                if ((vol.flags & (NavFlags.Blocked | NavFlags.KeepOut)) == 0) continue;
                if (GeometryUtil.SegmentIntersectsAabb(p0, p1, vol.aabb))
                    return true;
            }
            return false;
        }

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
