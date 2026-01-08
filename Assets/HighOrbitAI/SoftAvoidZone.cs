using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// できれば避けたい領域（通っても良いがコスト増）。
    /// </summary>
    public class SoftAvoidZone : MonoBehaviour
    {
        [Tooltip("避けたい周辺範囲（m）")]
        public float margin = 10f;

        [Tooltip("この領域に入ったときの追加コスト")]
        public float costAdd = 20f;

        [Tooltip("このゾーンが動くならtrue")]
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
