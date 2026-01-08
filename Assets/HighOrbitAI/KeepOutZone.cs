using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 近づいてはいけない領域（硬い禁止）。
    /// Collider/RendererのBoundsをベースに、marginで周辺範囲を広げて扱う。
    /// </summary>
    public class KeepOutZone : MonoBehaviour
    {
        [Tooltip("禁止領域の周辺範囲（m）")]
        public float margin = 10f;

        [Tooltip("このゾーンが動くならtrue（例：移動する危険物）")]
        public bool isDynamic = false;

        public Bounds GetInflatedBounds(float agentRadius = 0f)
        {
            var b = GetBaseBounds();
            return GeometryUtil.Inflate(b, margin + agentRadius);
        }

        Bounds GetBaseBounds()
        {
            // Collider優先、なければRenderer
            var col = GetComponent<Collider>();
            if (col != null) return col.bounds;

            var r = GetComponent<Renderer>();
            if (r != null) return r.bounds;

            // 何もない場合はTransform位置を小さく
            return new Bounds(transform.position, Vector3.one * 0.1f);
        }
    }
}
