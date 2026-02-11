using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// AirLaneGraph を 3D化するビルダー。
    /// - 複数の高度レイヤー(y)にノードを配置
    /// - 同一レイヤ内の近傍接続 + 上下レイヤへの接続
    /// - SegmentHitsHardAny で硬衝突を弾く
    /// - SoftAvoid をサンプルしてコストへ反映
    /// </summary>
    public class AirLaneGraph3DBuilder
    {
        [Header("Grid")]
        public float nodeSpacingXZ = 28f;
        public float layerSpacingY = 25f;

        [Tooltip("最低高度(ワールドY)")]
        public float minY = 10f;

        [Tooltip("最高高度(ワールドY)")]
        public float maxY = 320f;

        [Header("Edges")]
        public float edgeMaxDistance = 95f;

        [Tooltip("SoftAvoidのサンプル回数(少ないほど軽い)")]
        public int softCostSamples = 2;

        [Header("Preference")]
        [Tooltip("低高度を避ける(高いほど上を好む)。0で無効。")]
        public float lowAltitudePenalty = 0.0f;

        [Tooltip("ペナルティ計算の基準高度(この高さ未満でペナルティ)")]
        public float penaltyBaseY = 60f;

        public AirLaneGraph Build(Bounds worldBounds, VolumeDatabase db, float queryRadius = 0.6f)
        {
            var g = new AirLaneGraph();
            g.Clear();

            // --- レイヤ数 ---
            int ny = Mathf.Clamp(Mathf.CeilToInt((maxY - minY) / Mathf.Max(1f, layerSpacingY)) + 1, 2, 64);

            // --- xzグリッド ---
            int nx = Mathf.Clamp(Mathf.CeilToInt(worldBounds.size.x / Mathf.Max(1f, nodeSpacingXZ)), 8, 240);
            int nz = Mathf.Clamp(Mathf.CeilToInt(worldBounds.size.z / Mathf.Max(1f, nodeSpacingXZ)), 8, 240);

            Vector3 start = worldBounds.min + new Vector3(nodeSpacingXZ * 0.5f, 0f, nodeSpacingXZ * 0.5f);

            // grid(x,y,z) -> nodeId
            int[,,] gridToId = new int[nx, ny, nz];
            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
                gridToId[ix, iy, iz] = -1;

            // --- ノード生成 ---
            for (int iy = 0; iy < ny; iy++)
            {
                float y = minY + iy * layerSpacingY;

                for (int ix = 0; ix < nx; ix++)
                for (int iz = 0; iz < nz; iz++)
                {
                    Vector3 p = start + new Vector3(ix * nodeSpacingXZ, 0f, iz * nodeSpacingXZ);
                    p.y = y;

                    db.EvaluatePoint(p, queryRadius, out var flags, out _);

                    // ノードが障害物/KeepOut内なら作らない（3Dでも基本）
                    if ((flags & NavFlags.KeepOut) != 0) continue;
                    if ((flags & NavFlags.Blocked) != 0) continue;

                    int id = g.nodes.Count;
                    g.nodes.Add(new AirLaneGraph.Node { pos = p, firstEdge = 0, edgeCount = 0 });
                    gridToId[ix, iy, iz] = id;
                }
            }

            // --- エッジ接続 ---
            // r: xz方向の探索半径
            int rx = Mathf.CeilToInt(edgeMaxDistance / Mathf.Max(1f, nodeSpacingXZ));
            rx = Mathf.Clamp(rx, 1, 6);

            // 3D近傍（同一レイヤ: 8近傍まで、上下レイヤ: 同一xz + 斜めも少し）
            // ただし軽さ優先で候補を絞る
            var neighbor = new List<Vector3Int>(32);

            // 同一レイヤ（8近傍の範囲をrxで拡張）
            for (int dx = -rx; dx <= rx; dx++)
            for (int dz = -rx; dz <= rx; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                neighbor.Add(new Vector3Int(dx, 0, dz));
            }

            // 上下レイヤ（同一xz + 近い斜め）
            neighbor.Add(new Vector3Int(0, 1, 0));
            neighbor.Add(new Vector3Int(0, -1, 0));
            neighbor.Add(new Vector3Int(1, 1, 0));
            neighbor.Add(new Vector3Int(-1, 1, 0));
            neighbor.Add(new Vector3Int(0, 1, 1));
            neighbor.Add(new Vector3Int(0, 1, -1));
            neighbor.Add(new Vector3Int(1, -1, 0));
            neighbor.Add(new Vector3Int(-1, -1, 0));
            neighbor.Add(new Vector3Int(0, -1, 1));
            neighbor.Add(new Vector3Int(0, -1, -1));

            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
            {
                int fromId = gridToId[ix, iy, iz];
                if (fromId < 0) continue;

                int edgeStart = g.edges.Count;
                int edgeCount = 0;

                Vector3 from = g.nodes[fromId].pos;

                for (int k = 0; k < neighbor.Count; k++)
                {
                    var d = neighbor[k];
                    int jx = ix + d.x;
                    int jy = iy + d.y;
                    int jz = iz + d.z;

                    if ((uint)jx >= (uint)nx || (uint)jy >= (uint)ny || (uint)jz >= (uint)nz) continue;

                    int toId = gridToId[jx, jy, jz];
                    if (toId < 0) continue;

                    Vector3 to = g.nodes[toId].pos;
                    float dist = Vector3.Distance(from, to);
                    if (dist > edgeMaxDistance) continue;

                    // 硬衝突を弾く（静的＋動的）
                    if (db.SegmentHitsHardAny(from, to)) continue;

                    float soft = (softCostSamples > 0)
                        ? db.EstimateSoftCostOnSegment(from, to, softCostSamples, queryRadius)
                        : 0f;

                    // 低高度を嫌う（上に行けば行くほど障害物が減る世界観の補助）
                    float lowPen = 0f;
                    if (lowAltitudePenalty > 0f)
                    {
                        float midY = (from.y + to.y) * 0.5f;
                        float under = Mathf.Max(0f, penaltyBaseY - midY);
                        lowPen = under * under * lowAltitudePenalty;
                    }

                    g.edges.Add(new AirLaneGraph.Edge { to = toId, cost = dist + soft + lowPen });
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
