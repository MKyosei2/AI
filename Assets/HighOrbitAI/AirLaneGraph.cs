using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 超高軌道(Cruise)用の疎グラフ。
    /// ノード数が少ないのでA*が超高速。
    /// </summary>
    public class AirLaneGraph
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

        public void Clear()
        {
            nodes.Clear();
            edges.Clear();
        }
    }

    /// <summary>
    /// 高度Hcruiseの面にノードを生成し、エッジを接続してグラフ化する。
    /// </summary>
    public class AirLaneGraphBuilder
    {
        public float nodeSpacing = 20f;
        public float edgeMaxDistance = 80f;
        public int softCostSamples = 4;

        public AirLaneGraph Build(Bounds worldBounds, float Hcruise, float band, VolumeDatabase db, float queryRadius = 0.5f)
        {
            var g = new AirLaneGraph();

            // ノード生成
            int nx = Mathf.CeilToInt(worldBounds.size.x / nodeSpacing);
            int nz = Mathf.CeilToInt(worldBounds.size.z / nodeSpacing);
            Vector3 origin = worldBounds.min;

            // bandの中心高度を採用（必要なら複数レーンに拡張可）
            float y = Hcruise;

            // マップ端に寄りすぎないよう半分オフセット
            Vector3 start = origin + new Vector3(nodeSpacing * 0.5f, 0f, nodeSpacing * 0.5f);

            // グリッド→ノードID
            int[,] gridToId = new int[nx, nz];
            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
                gridToId[ix, iz] = -1;

            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                Vector3 p = start + new Vector3(ix * nodeSpacing, 0f, iz * nodeSpacing);
                p.y = y;

                db.EvaluatePoint(p, queryRadius, out var flags, out _);
                if ((flags & NavFlags.KeepOut) != 0) continue;

                var node = new AirLaneGraph.Node { pos = p, firstEdge = 0, edgeCount = 0 };
                int id = g.nodes.Count;
                g.nodes.Add(node);
                gridToId[ix, iz] = id;
            }

            // エッジ接続（近傍グリッド＋距離制限）
            // ざっくり: 周囲 rセルを見て接続候補にする
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

                    // hard衝突（KeepOut/Blocked）に当たるなら接続しない
                    if (db.SegmentHitsHard(from, to)) continue;

                    // soft cost（あれば）
                    float soft = db.EstimateSoftCostOnSegment(from, to, softCostSamples, queryRadius);

                    g.edges.Add(new AirLaneGraph.Edge { to = toId, cost = dist + soft });
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
