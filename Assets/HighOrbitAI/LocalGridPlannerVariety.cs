using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class LocalGridPlannerVariety
    {
        public float cellSize = 6f;
        public float localRadius = 140f;
        public int maxNodes = 22000;
        public float tieEpsilon = 0.01f;

        struct Rec
        {
            public float g;
            public float f;
            public List<int> parents;
        }

        readonly Dictionary<int, Rec> recs = new Dictionary<int, Rec>(20000);
        readonly List<int> open = new List<int>(20000);
        readonly HashSet<int> closed = new HashSet<int>();

        Vector3Int dims;
        Vector3 origin;

        int ToIndex(Vector3Int c) => c.x + c.y * dims.x + c.z * dims.x * dims.y;

        Vector3Int IndexToCell(int idx)
        {
            int xy = dims.x * dims.y;
            int z = idx / xy;
            int rem = idx - z * xy;
            int y = rem / dims.x;
            int x = rem - y * dims.x;
            return new Vector3Int(x, y, z);
        }

        bool InBounds(Vector3Int c) => (uint)c.x < (uint)dims.x && (uint)c.y < (uint)dims.y && (uint)c.z < (uint)dims.z;

        Vector3 CellCenter(Vector3Int c) => origin + new Vector3((c.x + 0.5f) * cellSize, (c.y + 0.5f) * cellSize, (c.z + 0.5f) * cellSize);

        Vector3Int WorldToCell(Vector3 p)
        {
            Vector3 lp = p - origin;
            return new Vector3Int(
                Mathf.FloorToInt(lp.x / cellSize),
                Mathf.FloorToInt(lp.y / cellSize),
                Mathf.FloorToInt(lp.z / cellSize)
            );
        }

        public bool FindPath(Vector3 startPos, Vector3 goalPos, VolumeDatabase db, float agentRadius,
            float maxClimbRate, float maxSpeed, float desiredY, float ceilingY,
            int seed, List<Vector3> outPath)
        {
            outPath.Clear();
            recs.Clear();
            open.Clear();
            closed.Clear();

            float d = localRadius * 2f;
            dims = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 10, 160),
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 10, 120),
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 10, 160)
            );

            origin = startPos - new Vector3(dims.x * cellSize, dims.y * cellSize, dims.z * cellSize) * 0.5f;

            Vector3Int sCell = WorldToCell(startPos);
            Vector3Int gCell = WorldToCell(goalPos);

            if (!InBounds(sCell) || !InBounds(gCell)) return false;

            int sIdx = ToIndex(sCell);
            int gIdx = ToIndex(gCell);

            recs[sIdx] = new Rec { g = 0f, f = Heu(startPos, goalPos), parents = new List<int>(1) { -1 } };
            open.Add(sIdx);

            int expansions = 0;

            while (open.Count > 0)
            {
                int cur = PopBest(open);
                if (cur == gIdx)
                {
                    Reconstruct(cur, seed, outPath);
                    outPath.Reverse();
                    return true;
                }

                open.Remove(cur);
                closed.Add(cur);

                var curCell = IndexToCell(cur);
                Vector3 curPos = CellCenter(curCell);

                expansions++;
                if (expansions > maxNodes) break;

                for (int ni = 0; ni < 6; ni++)
                {
                    var nCell = curCell + Neighbor6(ni);
                    if (!InBounds(nCell)) continue;

                    Vector3 nPos = CellCenter(nCell);

                    if (nPos.y > ceilingY + 0.001f) continue;

                    float dy = nPos.y - curPos.y;
                    float dist = Vector3.Distance(curPos, nPos);
                    float dt = Mathf.Max(0.001f, dist / Mathf.Max(0.1f, maxSpeed));
                    if (dy / dt > maxClimbRate) continue;

                    db.EvaluatePoint(nPos, agentRadius, out var flags, out float costAdd);
                    if ((flags & NavFlags.KeepOut) != 0) continue;
                    if ((flags & NavFlags.Blocked) != 0) continue;

                    float altPenalty = Mathf.Max(0f, nPos.y - desiredY);
                    altPenalty = altPenalty * altPenalty * 0.08f;

                    int nIdx = ToIndex(nCell);
                    if (closed.Contains(nIdx)) continue;

                    float ng = recs[cur].g + dist + costAdd + altPenalty;

                    if (!recs.TryGetValue(nIdx, out var r))
                    {
                        r = new Rec { g = ng, f = ng + Heu(nPos, goalPos), parents = new List<int>(2) { cur } };
                        recs[nIdx] = r;
                        open.Add(nIdx);
                    }
                    else
                    {
                        if (ng + tieEpsilon < r.g)
                        {
                            r.g = ng;
                            r.f = ng + Heu(nPos, goalPos);
                            r.parents.Clear();
                            r.parents.Add(cur);
                            recs[nIdx] = r;
                        }
                        else if (Mathf.Abs(ng - r.g) <= tieEpsilon)
                        {
                            if (!r.parents.Contains(cur))
                                r.parents.Add(cur);
                            recs[nIdx] = r;
                        }

                        if (!open.Contains(nIdx)) open.Add(nIdx);
                    }
                }
            }

            return false;
        }

        float Heu(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        int PopBest(List<int> openList)
        {
            int best = openList[0];
            float bestF = recs[best].f;
            for (int i = 1; i < openList.Count; i++)
            {
                int n = openList[i];
                float f = recs[n].f;
                if (f < bestF) { best = n; bestF = f; }
            }
            return best;
        }

        void Reconstruct(int goalIdx, int seed, List<Vector3> outPath)
        {
            var rng = new System.Random(seed);
            int cur = goalIdx;
            while (cur >= 0 && recs.TryGetValue(cur, out var r))
            {
                outPath.Add(CellCenter(IndexToCell(cur)));
                if (r.parents == null || r.parents.Count == 0) break;

                int parent = (r.parents.Count == 1) ? r.parents[0] : r.parents[rng.Next(0, r.parents.Count)];
                cur = parent;
            }
        }

        static Vector3Int Neighbor6(int i)
        {
            switch (i)
            {
                case 0: return new Vector3Int(1, 0, 0);
                case 1: return new Vector3Int(-1, 0, 0);
                case 2: return new Vector3Int(0, 1, 0);
                case 3: return new Vector3Int(0, -1, 0);
                case 4: return new Vector3Int(0, 0, 1);
                default: return new Vector3Int(0, 0, -1);
            }
        }
    }
}
