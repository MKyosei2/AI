using UnityEngine;

namespace HighOrbitAI
{
    public class KeepOutZone : MonoBehaviour
    {
        public float margin = 10f;
        public bool isDynamic = false;

        public Bounds GetInflatedBounds(float agentRadius = 0f)
        {
            var b = GetBaseBounds();
            return GeometryUtil.Inflate(b, margin + agentRadius);
        }

        Bounds GetBaseBounds()
        {
            var col = GetComponent<Collider>();
            if (col != null) return col.bounds;

            var r = GetComponent<Renderer>();
            if (r != null) return r.bounds;

            return new Bounds(transform.position, Vector3.one * 0.1f);
        }
    }
}
