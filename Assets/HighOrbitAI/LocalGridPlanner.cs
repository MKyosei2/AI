using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// Terminal用：局所窓内の3DグリッドA*（探索範囲が小さいので現実的）。
    /// 障害物が多くても「局所窓の中」だけを見るのでスケールしやすい。
    /// </summary>
    public class LocalGridPlanner
    {
        public float cellSize = 5f;
        public float localRadius = 60f; // 半径
        public int maxNodes = 12000;     // 探索打ち切り（リアルタイム保証）

        readonly List<Vector3> neighbors = new List<Vector3>(6);

        struct NodeRec
        {
            public int parent;
            public float g;
            public float f;
        }

        readonly Dictionary<int, NodeRec> recs = new Dictionary<int, NodeRec>(16384);
        readonly List<int> open = new List<int>(16384);
        readonly HashSet<int> closed = new HashSet<int>();

        Vector3Int dims;
        Vector3 origin;
        float yMin, yMax;

        int ToIndex(Vector3Int c) => (c.x) + (c.y * dims.x) + (c.z * dims.x * dims.y);

        bool InBounds(Vector3Int c) => c.x >= 0 && c.x < dims.x && c.y >= 0 && c.y < dims.y && c.z >= 0 && c.z < dims.z;

        Vector3 CellCenter(Vector3Int c) => origin + new Vector3((c.x + 0.5f) * cellSize, (c.y + 0.5f) * cellSize, (c.z + 0.5f) * cellSize);

        public bool FindPath(Vector3 startPos, Vector3 goalPos, VolumeDatabase db, float agentRadius, float maxClimbRate, float maxSpeed, List<Vector3> outPath)
        {
            outPath.Clear();
            recs.Clear();
            open.Clear();
            closed.Clear();

            // 局所窓：startを中心に立方体
            float d = localRadius * 2f;
            dims = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 6, 120),
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 6, 120),
                Mathf.Clamp(Mathf.CeilToInt(d / cellSize), 6, 120)
            );

            origin = startPos - new Vector3(dims.x * cellSize, dims.y * cellSize, dims.z * cellSize) * 0.5f;

            // 高度制限：局所窓内（必要なら外部パラメータ化）
            yMin = origin.y;
            yMax = origin.y + dims.y * cellSize;

            Vector3Int sCell = WorldToCell(startPos);
            Vector3Int gCell = WorldToCell(goalPos);
            if (!InBounds(sCell) || !InBounds(gCell)) return false;

            int sIdx = ToIndex(sCell);
            int gIdx = ToIndex(gCell);

            recs[sIdx] = new NodeRec { parent = -1, g = 0f, f = Heuristic(startPos, goalPos) };
            open.Add(sIdx);

            int expansions = 0;

            while (open.Count > 0)
            {
                int cur = PopBest(open);
                if (cur == gIdx)
                {
                    Reconstruct(cur, gIdx, outPath);
                    outPath.Reverse();
                    return true;
                }

                open.Remove(cur);
                closed.Add(cur);

                var curCell = IndexToCell(cur);
                Vector3 curPos = CellCenter(curCell);

                // 展開数制限（リアルタイム）
                expansions++;
                if (expansions > maxNodes) break;

                // 6近傍（軽量）
                for (int ni = 0; ni < 6; ni++)
                {
                    var nCell = curCell + Neighbor6(ni);
                    if (!InBounds(nCell)) continue;

                    Vector3 nPos = CellCenter(nCell);

                    // 上昇率制限（簡易：dt=距離/速度）
                    float dy = nPos.y - curPos.y;
                    float dist = Vector3.Distance(curPos, nPos);
                    float dt = Mathf.Max(0.001f, dist / Mathf.Max(0.1f, maxSpeed));
                    if (dy / dt > maxClimbRate) continue;

                    // 高度範囲（念のため）
                    if (nPos.y < yMin || nPos.y > yMax) continue;

                    // 通行判定（KeepOut/Blocked）
                    db.EvaluatePoint(nPos, agentRadius, out var flags, out float costAdd);
                    if ((flags & NavFlags.KeepOut) != 0) continue;
                    if ((flags & NavFlags.Blocked) != 0) continue;

                    int nIdx = ToIndex(nCell);
                    if (closed.Contains(nIdx)) continue;

                    float ng = recs[cur].g + dist + costAdd;

                    if (!recs.TryGetValue(nIdx, out var rec))
                    {
                        rec = new NodeRec
                        {
                            parent = cur,
                            g = ng,
                            f = ng + Heuristic(nPos, goalPos)
                        };
                        recs[nIdx] = rec;
                        open.Add(nIdx);
                    }
                    else if (ng < rec.g)
                    {
                        rec.parent = cur;
                        rec.g = ng;
                        rec.f = ng + Heuristic(nPos, goalPos);
                        recs[nIdx] = rec;
                        if (!open.Contains(nIdx)) open.Add(nIdx);
                    }
                }
            }

            return false;
        }

        Vector3Int WorldToCell(Vector3 p)
        {
            Vector3 lp = p - origin;
            return new Vector3Int(
                Mathf.FloorToInt(lp.x / cellSize),
                Mathf.FloorToInt(lp.y / cellSize),
                Mathf.FloorToInt(lp.z / cellSize)
            );
        }

        Vector3Int IndexToCell(int idx)
        {
            int xy = dims.x * dims.y;
            int z = idx / xy;
            int rem = idx - z * xy;
            int y = rem / dims.x;
            int x = rem - y * dims.x;
            return new Vector3Int(x, y, z);
        }

        static float Heuristic(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

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

        void Reconstruct(int goalIdx, int startGoalIdx, List<Vector3> outPath)
        {
            int cur = goalIdx;
            while (cur >= 0 && recs.TryGetValue(cur, out var rec))
            {
                outPath.Add(CellCenter(IndexToCell(cur)));
                cur = rec.parent;
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
