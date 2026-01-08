using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 静的ボリューム向けのAABBツリー(BVH)。
    /// - ロード時に一回構築
    /// - QueryAabb / QuerySegment を高速化
    /// </summary>
    public class StaticBVH
    {
        public struct Node
        {
            public Bounds bounds;
            public int left;   // child index, -1 if leaf
            public int right;  // child index, -1 if leaf
            public int start;  // leaf: range start in indices[]
            public int count;  // leaf: range count
            public bool IsLeaf => left < 0 && right < 0;
        }

        readonly List<Node> nodes = new List<Node>(2048);
        int[] indices; // volume indices

        readonly IList<VolumeLite> volumes;
        readonly int leafSize;

        public StaticBVH(IList<VolumeLite> volumes, int leafSize = 8)
        {
            this.volumes = volumes;
            this.leafSize = Mathf.Clamp(leafSize, 2, 32);
        }

        public void Build(List<int> volumeIds)
        {
            indices = volumeIds.ToArray();
            nodes.Clear();
            if (indices.Length == 0) return;

            BuildRecursive(0, indices.Length);
        }

        int BuildRecursive(int start, int count)
        {
            Bounds b = new Bounds();
            bool init = false;
            for (int i = start; i < start + count; i++)
            {
                var vb = volumes[indices[i]].aabb;
                if (!init) { b = vb; init = true; }
                else b.Encapsulate(vb);
            }

            int nodeIndex = nodes.Count;
            nodes.Add(new Node
            {
                bounds = b,
                left = -1,
                right = -1,
                start = start,
                count = count
            });

            if (count <= leafSize)
                return nodeIndex;

            // split on longest axis
            Vector3 size = b.size;
            int axis = (size.x > size.y && size.x > size.z) ? 0 : (size.y > size.z ? 1 : 2);

            // sort by center along axis
            System.Array.Sort(indices, start, count, new CenterComparer(volumes, axis));

            int mid = start + count / 2;
            int left = BuildRecursive(start, mid - start);
            int right = BuildRecursive(mid, start + count - mid);

            var n = nodes[nodeIndex];
            n.left = left;
            n.right = right;
            nodes[nodeIndex] = n;

            return nodeIndex;
        }

        class CenterComparer : IComparer<int>
        {
            readonly IList<VolumeLite> volumes;
            readonly int axis;
            public CenterComparer(IList<VolumeLite> volumes, int axis) { this.volumes = volumes; this.axis = axis; }
            public int Compare(int a, int b)
            {
                float ca = volumes[a].aabb.center[axis];
                float cb = volumes[b].aabb.center[axis];
                return ca.CompareTo(cb);
            }
        }

        public void QueryAabb(Bounds area, List<int> outVolumeIds)
        {
            outVolumeIds.Clear();
            if (nodes.Count == 0) return;

            var stack = new Stack<int>(64);
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                var n = nodes[ni];
                if (!n.bounds.Intersects(area)) continue;

                if (n.IsLeaf)
                {
                    for (int i = n.start; i < n.start + n.count; i++)
                        outVolumeIds.Add(indices[i]);
                }
                else
                {
                    stack.Push(n.left);
                    stack.Push(n.right);
                }
            }
        }

        /// <summary>
        /// 線分に当たりそうな候補を返す（本当の衝突は各VolumeのAABBで判定）
        /// </summary>
        public void QuerySegment(Vector3 p0, Vector3 p1, List<int> outVolumeIds)
        {
            outVolumeIds.Clear();
            if (nodes.Count == 0) return;

            // 線分のAABB（粗い枝刈り）
            Bounds segAabb = new Bounds((p0 + p1) * 0.5f, new Vector3(Mathf.Abs((p1 - p0).x), Mathf.Abs((p1 - p0).y), Mathf.Abs((p1 - p0).z)));
            segAabb.Expand(Vector3.one * 0.01f);

            var stack = new Stack<int>(64);
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                var n = nodes[ni];
                if (!n.bounds.Intersects(segAabb)) continue;

                // さらに線分 vs nodeAABB
                if (!GeometryUtil.SegmentIntersectsAabb(p0, p1, n.bounds)) continue;

                if (n.IsLeaf)
                {
                    for (int i = n.start; i < n.start + n.count; i++)
                        outVolumeIds.Add(indices[i]);
                }
                else
                {
                    stack.Push(n.left);
                    stack.Push(n.right);
                }
            }
        }
    }
}
