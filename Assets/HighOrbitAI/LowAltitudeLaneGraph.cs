using UnityEngine;
using System.Collections.Generic;

namespace HighOrbitAI
{
    public class LowAltitudeLaneGraph
    {
        public struct Node
        {
            public Vector3 pos;
            public int firstEdge;
            public int edgeCount;
        }

        public struct Edge
        {
            public int to;
            public float cost;
        }

        public readonly List<Node> nodes = new List<Node>(4096);
        public readonly List<Edge> edges = new List<Edge>(16384);
    }

    public class LowAltitudeLaneGraphBuilder
    {
        public float nodeSpacing = 35f;
        public float edgeMaxDistance = 120f;
        public int softCostSamples = 3;

        public LayerMask groundMask = ~0;
        public float groundCastHeight = 800f;
        public float groundMaxDistance = 2000f;

        public LowAltitudeLaneGraph Build(Bounds worldBounds, float lowHeight, float ceilingAboveGround, VolumeDatabase db, float agentRadius)
        {
            var g = new LowAltitudeLaneGraph();

            int nx = Mathf.CeilToInt(worldBounds.size.x / nodeSpacing);
            int nz = Mathf.CeilToInt(worldBounds.size.z / nodeSpacing);
            Vector3 origin = worldBounds.min;
            Vector3 start = origin + new Vector3(nodeSpacing * 0.5f, 0f, nodeSpacing * 0.5f);

            int[,] gridToId = new int[nx, nz];
            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
                gridToId[ix, iz] = -1;

            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                Vector3 p = start + new Vector3(ix * nodeSpacing, 0f, iz * nodeSpacing);

                float gy;
                if (!GroundSampler.TryGetGroundY(p, groundCastHeight, groundMaxDistance, groundMask, out gy))
                    gy = worldBounds.min.y;

                float desiredY = gy + lowHeight;
                float ceilingY = gy + ceilingAboveGround;
                p.y = Mathf.Min(desiredY, ceilingY);

                db.EvaluatePoint(p, agentRadius, out var flags, out _);
                if ((flags & NavFlags.KeepOut) != 0) continue;

                int id = g.nodes.Count;
                g.nodes.Add(new LowAltitudeLaneGraph.Node { pos = p, firstEdge = 0, edgeCount = 0 });
                gridToId[ix, iz] = id;
            }

            int r = Mathf.CeilToInt(edgeMaxDistance / nodeSpacing);

            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                int fromId = gridToId[ix, iz];
                if (fromId < 0) continue;

                int edgeStart = g.edges.Count;
                int edgeCount = 0;
                Vector3 from = g.nodes[fromId].pos;

                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int jx = ix + dx;
                    int jz = iz + dz;
                    if (jx < 0 || jx >= nx || jz < 0 || jz >= nz) continue;

                    int toId = gridToId[jx, jz];
                    if (toId < 0) continue;

                    Vector3 to = g.nodes[toId].pos;
                    float dist = Vector3.Distance(from, to);
                    if (dist > edgeMaxDistance) continue;

                    if (db.SegmentHitsHardAny(from, to)) continue;

                    float soft = db.EstimateSoftCostOnSegment(from, to, softCostSamples, agentRadius);

                    float dy = Mathf.Abs(to.y - from.y);
                    float altPenalty = dy * 0.35f;

                    g.edges.Add(new LowAltitudeLaneGraph.Edge { to = toId, cost = dist + soft + altPenalty });
                    edgeCount++;
                }

                var n = g.nodes[fromId];
                n.firstEdge = edgeStart;
                n.edgeCount = edgeCount;
                g.nodes[fromId] = n;
            }

            return g;
        }
    }
}
