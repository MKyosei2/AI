using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class SpatialHash3D
    {
        readonly float cellSize;
        readonly Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>(4096);

        public SpatialHash3D(float cellSize)
        {
            this.cellSize = Mathf.Max(0.01f, cellSize);
        }

        public void Clear() => buckets.Clear();

        public Vector3Int WorldToCell(Vector3 p)
        {
            return new Vector3Int(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize)
            );
        }

        long Key(Vector3Int c)
        {
            unchecked
            {
                long x = (long)c.x;
                long y = (long)c.y;
                long z = (long)c.z;
                return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
            }
        }

        public void Insert(int id, Bounds aabb)
        {
            Vector3Int min = WorldToCell(aabb.min);
            Vector3Int max = WorldToCell(aabb.max);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                var k = Key(new Vector3Int(x,y,z));
                if (!buckets.TryGetValue(k, out var list))
                {
                    list = new List<int>(8);
                    buckets[k] = list;
                }
                list.Add(id);
            }
        }

        public void Query(Bounds area, List<int> outIds)
        {
            Vector3Int min = WorldToCell(area.min);
            Vector3Int max = WorldToCell(area.max);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                var k = Key(new Vector3Int(x,y,z));
                if (buckets.TryGetValue(k, out var list))
                    outIds.AddRange(list);
            }
        }
    }
}
