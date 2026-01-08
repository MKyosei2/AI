using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// Cruise用：AirLaneGraph上のA*探索（最小・高速）。
    /// </summary>
    public class AStarAirLane
    {
        struct Record
        {
            public int node;
            public int parent;
            public float g;
            public float f;
        }

        // 使い回しバッファ（GC削減）
        readonly Dictionary<int, Record> records = new Dictionary<int, Record>(4096);
        readonly List<int> open = new List<int>(4096);
        readonly HashSet<int> closed = new HashSet<int>();

        public bool FindPath(AirLaneGraph g, int start, int goal, List<Vector3> outPath)
        {
            outPath.Clear();
            records.Clear();
            open.Clear();
            closed.Clear();

            Record r0 = new Record { node = start, parent = -1, g = 0f, f = Heuristic(g, start, goal) };
            records[start] = r0;
            open.Add(start);

            while (open.Count > 0)
            {
                int current = PopBest(open, records);
                if (current == goal)
                {
                    Reconstruct(goal, records, g, outPath);
                    outPath.Reverse();
                    return true;
                }

                open.Remove(current);
                closed.Add(current);

                var n = g.nodes[current];
                for (int i = 0; i < n.edgeCount; i++)
                {
                    var e = g.edges[n.firstEdge + i];
                    int next = e.to;
                    if (closed.Contains(next)) continue;

                    float ng = records[current].g + e.cost;

                    if (!records.TryGetValue(next, out var rec))
                    {
                        rec = new Record { node = next, parent = current, g = ng, f = ng + Heuristic(g, next, goal) };
                        records[next] = rec;
                        open.Add(next);
                    }
                    else if (ng < rec.g)
                    {
                        rec.parent = current;
                        rec.g = ng;
                        rec.f = ng + Heuristic(g, next, goal);
                        records[next] = rec;
                        if (!open.Contains(next)) open.Add(next);
                    }
                }
            }

            return false;
        }

        static float Heuristic(AirLaneGraph g, int a, int b)
        {
            return Vector3.Distance(g.nodes[a].pos, g.nodes[b].pos);
        }

        static int PopBest(List<int> open, Dictionary<int, Record> records)
        {
            int best = open[0];
            float bestF = records[best].f;
            for (int i = 1; i < open.Count; i++)
            {
                int n = open[i];
                float f = records[n].f;
                if (f < bestF) { best = n; bestF = f; }
            }
            return best;
        }

        static void Reconstruct(int goal, Dictionary<int, Record> records, AirLaneGraph g, List<Vector3> outPath)
        {
            int cur = goal;
            while (cur >= 0)
            {
                outPath.Add(g.nodes[cur].pos);
                cur = records[cur].parent;
            }
        }
    }
}
