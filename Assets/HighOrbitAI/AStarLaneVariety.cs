using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// LaneGraph用 A*（最短コストは維持しつつ、同コスト解をseedで分岐）。
    ///
    /// 追加：
    /// - database を渡すと、探索中にエッジを “動的/条件付き” も含めて再検証できる
    ///   （例：ドアが閉まった瞬間、次の探索でそのエッジを自然に回避）
    /// </summary>
    public class AStarLaneVariety
    {
        public float tieEpsilon = 0.01f;

        [Header("Optional: dynamic edge validation")]
        public bool validateEdgesWithDb = true;

        /// <summary>動的/条件付きチェック用（未設定なら検証しない）</summary>
        public VolumeDatabase database;

        struct Rec
        {
            public float g;
            public float f;
            public List<int> parents;
        }

        readonly Dictionary<int, Rec> recs = new Dictionary<int, Rec>(4096);
        readonly HashSet<int> closed = new HashSet<int>();
        readonly List<int> open = new List<int>(1024);

        public bool FindPath(LowAltitudeLaneGraph g, int start, int goal, int seed, List<Vector3> outPath)
        {
            outPath.Clear();
            recs.Clear();
            closed.Clear();
            open.Clear();

            if (start == goal)
            {
                outPath.Add(g.nodes[start].pos);
                return true;
            }

            recs[start] = new Rec { g = 0f, f = Heu(g, start, goal), parents = new List<int>(1) { -1 } };
            open.Add(start);

            while (open.Count > 0)
            {
                int cur = PopBest(open);
                if (cur == goal)
                {
                    Reconstruct(goal, seed, g, outPath);
                    outPath.Reverse();
                    return true;
                }

                open.Remove(cur);
                closed.Add(cur);

                var n = g.nodes[cur];
                Vector3 curPos = n.pos;

                for (int i = 0; i < n.edgeCount; i++)
                {
                    var e = g.edges[n.firstEdge + i];
                    int nxt = e.to;
                    if (closed.Contains(nxt)) continue;

                    // ★ 動的/条件付きのエッジ再検証（必要なときだけ）
                    if (validateEdgesWithDb && database != null)
                    {
                        Vector3 nxtPos = g.nodes[nxt].pos;
                        if (database.SegmentHitsHardAny(curPos, nxtPos))
                            continue;
                    }

                    float ng = recs[cur].g + e.cost;

                    if (!recs.TryGetValue(nxt, out var r))
                    {
                        r = new Rec
                        {
                            g = ng,
                            f = ng + Heu(g, nxt, goal),
                            parents = new List<int>(1) { cur }
                        };
                        recs[nxt] = r;
                        open.Add(nxt);
                    }
                    else
                    {
                        if (ng + tieEpsilon < r.g)
                        {
                            r.g = ng;
                            r.f = ng + Heu(g, nxt, goal);
                            r.parents.Clear();
                            r.parents.Add(cur);
                            recs[nxt] = r;
                        }
                        else if (Mathf.Abs(ng - r.g) <= tieEpsilon)
                        {
                            if (r.parents.Count < 8) r.parents.Add(cur);
                            recs[nxt] = r;
                        }
                    }
                }
            }

            return false;
        }

        float Heu(LowAltitudeLaneGraph g, int a, int b)
        {
            Vector3 pa = g.nodes[a].pos;
            Vector3 pb = g.nodes[b].pos;
            float dx = pa.x - pb.x;
            float dz = pa.z - pb.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        int PopBest(List<int> open)
        {
            int best = open[0];
            float bestF = recs[best].f;
            for (int i = 1; i < open.Count; i++)
            {
                int id = open[i];
                float f = recs[id].f;
                if (f < bestF)
                {
                    best = id;
                    bestF = f;
                }
            }
            return best;
        }

        void Reconstruct(int goal, int seed, LowAltitudeLaneGraph g, List<Vector3> outPath)
        {
            int cur = goal;
            int guard = 0;

            while (cur >= 0 && guard++ < 8192)
            {
                outPath.Add(g.nodes[cur].pos);
                var r = recs[cur];
                if (r.parents == null || r.parents.Count == 0) break;

                int p = PickParent(r.parents, seed, cur);
                cur = p;
            }
        }

        int PickParent(List<int> parents, int seed, int nodeId)
        {
            if (parents.Count == 1) return parents[0];
            unchecked
            {
                int h = seed ^ (nodeId * 73856093);
                int idx = Mathf.Abs(h) % parents.Count;
                return parents[idx];
            }
        }
    }
}
