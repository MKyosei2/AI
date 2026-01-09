using UnityEngine;

namespace HighOrbitAI
{
    public class SoftAvoidZone : MonoBehaviour
    {
        public float margin = 10f;
        public float costAdd = 20f;
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
