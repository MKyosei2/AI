using UnityEngine;

namespace HighOrbitAI
{
    public static class GeometryUtil
    {
        /// <summary>Boundsを一様に膨らませる（KeepOutの周辺範囲など）</summary>
        public static Bounds Inflate(Bounds b, float margin)
        {
            b.Expand(new Vector3(margin * 2f, margin * 2f, margin * 2f));
            return b;
        }

        /// <summary>点がAABB内か（UnityのBounds.Containsは境界含む）</summary>
        public static bool Contains(Bounds b, Vector3 p) => b.Contains(p);

        /// <summary>
        /// 線分 vs AABB（スラブ法）: ざっくり衝突判定用（Cruiseエッジ検証など）
        /// </summary>
        public static bool SegmentIntersectsAabb(Vector3 p0, Vector3 p1, Bounds b)
        {
            Vector3 dir = p1 - p0;
            Vector3 inv = new Vector3(
                Mathf.Abs(dir.x) < 1e-6f ? float.PositiveInfinity : 1f / dir.x,
                Mathf.Abs(dir.y) < 1e-6f ? float.PositiveInfinity : 1f / dir.y,
                Mathf.Abs(dir.z) < 1e-6f ? float.PositiveInfinity : 1f / dir.z
            );

            Vector3 min = b.min;
            Vector3 max = b.max;

            float t1 = (min.x - p0.x) * inv.x;
            float t2 = (max.x - p0.x) * inv.x;
            float tmin = Mathf.Min(t1, t2);
            float tmax = Mathf.Max(t1, t2);

            t1 = (min.y - p0.y) * inv.y;
            t2 = (max.y - p0.y) * inv.y;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
            tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));

            t1 = (min.z - p0.z) * inv.z;
            t2 = (max.z - p0.z) * inv.z;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
            tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));

            // tが[0,1]区間に交差していれば線分と交差
            return tmax >= Mathf.Max(0f, tmin) && tmin <= 1f;
        }

        /// <summary>
        /// AABBから点までの最短ベクトル（点が外にある場合、境界への押し戻し方向に使う）
        /// </summary>
        public static Vector3 ClosestVectorToOutside(Bounds b, Vector3 p)
        {
            // bの外に出すための最小押し戻し（単純版）
            Vector3 min = b.min;
            Vector3 max = b.max;

            float dxMin = p.x - min.x;
            float dxMax = max.x - p.x;
            float dyMin = p.y - min.y;
            float dyMax = max.y - p.y;
            float dzMin = p.z - min.z;
            float dzMax = max.z - p.z;

            // 内部にいる前提で、最も近い面方向へ押し出す
            float m = dxMin; Vector3 v = new Vector3(-1,0,0);
            if (dxMax < m) { m = dxMax; v = new Vector3( 1,0,0); }
            if (dyMin < m) { m = dyMin; v = new Vector3(0,-1,0); }
            if (dyMax < m) { m = dyMax; v = new Vector3(0, 1,0); }
            if (dzMin < m) { m = dzMin; v = new Vector3(0,0,-1); }
            if (dzMax < m) { m = dzMax; v = new Vector3(0,0, 1); }

            return v * Mathf.Max(0.001f, m);
        }
    }
}
