using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class StaticBVH
    {
        public struct Node
        {
            public Bounds bounds;
            public int left;
            public int right;
            public int start;
            public int count;
            public bool IsLeaf => left < 0 && right < 0;
        }

        readonly List<Node> nodes = new List<Node>(2048);
        int[] indices;

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
            nodes.Add(new Node { bounds = b, left = -1, right = -1, start = start, count = count });

            if (count <= leafSize) return nodeIndex;

            Vector3 size = b.size;
            int axis = (size.x > size.y && size.x > size.z) ? 0 : (size.y > size.z ? 1 : 2);

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

        public void QuerySegment(Vector3 p0, Vector3 p1, List<int> outVolumeIds)
        {
            outVolumeIds.Clear();
            if (nodes.Count == 0) return;

            Vector3 d = (p1 - p0);
            Bounds segAabb = new Bounds((p0 + p1) * 0.5f, new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z)));
            segAabb.Expand(Vector3.one * 0.01f);

            var stack = new Stack<int>(64);
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                var n = nodes[ni];
                if (!n.bounds.Intersects(segAabb)) continue;
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
