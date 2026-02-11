using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class AirLaneGraph3DBuilder
    {
        [Header("Grid")]
        public float nodeSpacingXZ = 28f;
        public float layerSpacingY = 25f;
        public float minY = 10f;
        public float maxY = 320f;

        [Header("Edges")]
        public float edgeMaxDistance = 95f;

        [Header("Soft cost sampling")]
        public int softCostSamples = 2;

        [Header("Low-altitude bias")]
        public float lowAltitudePenalty = 0.0015f;
        public float penaltyBaseY = 70f;

        [Header("Perf Caps (Freeze guard)")]
        public int maxGridXZ = 80;
        public int maxLayersY = 24;

        public AirLaneGraph Build(Bounds worldBounds, VolumeDatabase db, float queryRadius = 0.7f)
        {
            AirLaneGraph result = null;
            var e = BuildIncremental(worldBounds, db, queryRadius, g => result = g, msBudget: 99999f);
            while (e.MoveNext()) { }
            return result;
        }

        public IEnumerator BuildIncremental(
            Bounds worldBounds,
            VolumeDatabase db,
            float queryRadius,
            System.Action<AirLaneGraph> onDone,
            float msBudget = 2.0f
        )
        {
            var g = new AirLaneGraph();
            g.Clear();

            // --- レイヤ数 ---
            int ny = Mathf.CeilToInt((maxY - minY) / Mathf.Max(1f, layerSpacingY)) + 1;
            ny = Mathf.Clamp(ny, 2, Mathf.Max(2, maxLayersY));

            // --- xzグリッド ---
            int nx = Mathf.CeilToInt(worldBounds.size.x / Mathf.Max(1f, nodeSpacingXZ));
            int nz = Mathf.CeilToInt(worldBounds.size.z / Mathf.Max(1f, nodeSpacingXZ));
            nx = Mathf.Clamp(nx, 8, Mathf.Max(8, maxGridXZ));
            nz = Mathf.Clamp(nz, 8, Mathf.Max(8, maxGridXZ));

            Vector3 start = worldBounds.min + new Vector3(nodeSpacingXZ * 0.5f, 0f, nodeSpacingXZ * 0.5f);

            int[,,] gridToId = new int[nx, ny, nz];
            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
                gridToId[ix, iy, iz] = -1;

            float t0 = Time.realtimeSinceStartup;

            // --- ノード生成（段階）---
            for (int iy = 0; iy < ny; iy++)
            {
                float y = minY + iy * layerSpacingY;

                for (int ix = 0; ix < nx; ix++)
                for (int iz = 0; iz < nz; iz++)
                {
                    Vector3 p = start + new Vector3(ix * nodeSpacingXZ, 0f, iz * nodeSpacingXZ);
                    p.y = y;

                    db.EvaluatePoint(p, queryRadius, out var flags, out _);

                    if ((flags & NavFlags.KeepOut) != 0) continue;
                    if ((flags & NavFlags.Blocked) != 0) continue;

                    int id = g.nodes.Count;
                    g.nodes.Add(new AirLaneGraph.Node { pos = p, firstEdge = 0, edgeCount = 0 });
                    gridToId[ix, iy, iz] = id;

                    if ((Time.realtimeSinceStartup - t0) * 1000f >= msBudget)
                    {
                        t0 = Time.realtimeSinceStartup;
                        yield return null;
                    }
                }
            }

            // --- エッジ接続（段階）---
            int rx = Mathf.CeilToInt(edgeMaxDistance / Mathf.Max(1f, nodeSpacingXZ));
            rx = Mathf.Clamp(rx, 1, 4);

            // 近傍候補を固定（軽量）
            var neighbor = new List<Vector3Int>(32);
            for (int dx = -rx; dx <= rx; dx++)
            for (int dz = -rx; dz <= rx; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                neighbor.Add(new Vector3Int(dx, 0, dz));
            }
            neighbor.Add(new Vector3Int(0, 1, 0));
            neighbor.Add(new Vector3Int(0, -1, 0));

            t0 = Time.realtimeSinceStartup;

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

                    if (db.SegmentHitsHardAny(from, to)) continue;

                    float soft = (softCostSamples > 0)
                        ? db.EstimateSoftCostOnSegment(from, to, softCostSamples, queryRadius)
                        : 0f;

                    float lowPen = 0f;
                    if (lowAltitudePenalty > 0f)
                    {
                        float midY = (from.y + to.y) * 0.5f;
                        float under = Mathf.Max(0f, penaltyBaseY - midY);
                        lowPen = under * under * lowAltitudePenalty;
                    }

                    g.edges.Add(new AirLaneGraph.Edge { to = toId, cost = dist + soft + lowPen });
                    edgeCount++;

                    if ((Time.realtimeSinceStartup - t0) * 1000f >= msBudget)
                    {
                        t0 = Time.realtimeSinceStartup;
                        yield return null;
                    }
                }

                var n = g.nodes[fromId];
                n.firstEdge = edgeStart;
                n.edgeCount = edgeCount;
                g.nodes[fromId] = n;

                if ((Time.realtimeSinceStartup - t0) * 1000f >= msBudget)
                {
                    t0 = Time.realtimeSinceStartup;
                    yield return null;
                }
            }

            onDone?.Invoke(g);
        }
    }
}
