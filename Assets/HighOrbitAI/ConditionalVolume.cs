using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 条件付きVolume（ドア/液体/イベントなど）。
    ///
    /// 重要：
    /// - ColliderのEnable/Disableで“存在”を変えるのではなく、
    ///   このコンポーネントの状態（open/close, enabled, cost）を切り替えるのが合理的。
    /// - DBには常に登録され、状態が変わるたびに flags/cost が更新される。
    /// </summary>
    [DisallowMultipleComponent]
    public class ConditionalVolume : MonoBehaviour
    {
        public enum VolumeType { Door, Hazard, Custom }

        [Header("Type")]
        public VolumeType type = VolumeType.Door;

        [Header("Bounds Source")]
        [Tooltip("未指定なら同一GameObjectのColliderを探します。")]
        public Collider sourceCollider;

        [Tooltip("Collider.bounds を使う（推奨）。")]
        public bool useColliderBounds = true;

        [Tooltip("Colliderが無い場合の手動AABB（ローカル中心）")]
        public Vector3 manualCenter = Vector3.zero;

        [Tooltip("Colliderが無い場合の手動AABB（ローカルサイズ）")]
        public Vector3 manualSize = new Vector3(3f, 4f, 3f);

        [Tooltip("agentRadius分だけAABBを膨らませる（衝突余裕）")]
        public bool inflateByAgentRadius = true;

        // ---- Door ----
        [Header("Door (type=Door)")]
        [Tooltip("true=開いている（通れる） / false=閉じている（Blocked）")]
        public bool doorOpen = true;

        [Tooltip("開いていてもDoorフラグ自体は残す（デバッグ用）。Blockedは付かない。")]
        public bool keepDoorFlagWhenOpen = true;

        // ---- Hazard ----
        [Header("Hazard (type=Hazard)")]
        [Tooltip("true=危険が有効 / false=無効")]
        public bool hazardEnabled = true;

        [Tooltip("危険が“通行不可”か")]
        public bool hazardBlocks = false;

        [Tooltip("危険を“避けたい”として扱う（SoftAvoid + cost）")]
        public bool hazardSoftAvoid = true;

        public float hazardCostAdd = 15f;

        // ---- Custom ----
        [Header("Custom (type=Custom)")]
        public bool customActive = true;
        public bool customBlocked = false;
        public bool customKeepOut = false;
        public bool customSoftAvoid = false;
        public float customCostAdd = 10f;

        [Tooltip("Custom時に追加する意味フラグ（Door/Hazard/Conditionalなど）")]
        public NavFlags customExtraFlags = NavFlags.Conditional;

        void Reset()
        {
            if (sourceCollider == null) sourceCollider = GetComponent<Collider>();
        }

        void OnValidate()
        {
            if (sourceCollider == null) sourceCollider = GetComponent<Collider>();
        }

        /// <summary>
        /// 現在状態の VolumeLite 情報を返す（DB登録/更新用）。
        /// </summary>
        public void GetCurrent(float agentRadius, out Bounds bounds, out NavFlags flags, out float costAdd)
        {
            bounds = ResolveBounds(agentRadius);

            flags = NavFlags.None;
            costAdd = 0f;

            switch (type)
            {
                case VolumeType.Door:
                {
                    bool active = true; // ドアは存在するが、閉まってる時だけBlocked
                    if (!active)
                    {
                        flags = NavFlags.None;
                        costAdd = 0f;
                        return;
                    }

                    // semantic
                    flags = NavFlags.Conditional | NavFlags.Door;
                    if (!doorOpen) flags |= NavFlags.Blocked;
                    else if (!keepDoorFlagWhenOpen) flags &= ~NavFlags.Door;

                    return;
                }

                case VolumeType.Hazard:
                {
                    if (!hazardEnabled)
                    {
                        flags = NavFlags.None;
                        costAdd = 0f;
                        return;
                    }

                    flags = NavFlags.Conditional | NavFlags.Hazard;
                    if (hazardBlocks) flags |= NavFlags.Blocked;
                    if (hazardSoftAvoid)
                    {
                        flags |= NavFlags.SoftAvoid;
                        costAdd = Mathf.Max(0f, hazardCostAdd);
                    }
                    return;
                }

                default:
                {
                    if (!customActive)
                    {
                        flags = NavFlags.None;
                        costAdd = 0f;
                        return;
                    }

                    flags = customExtraFlags;
                    if (customBlocked) flags |= NavFlags.Blocked;
                    if (customKeepOut) flags |= NavFlags.KeepOut;
                    if (customSoftAvoid)
                    {
                        flags |= NavFlags.SoftAvoid;
                        costAdd = Mathf.Max(0f, customCostAdd);
                    }
                    return;
                }
            }
        }

        Bounds ResolveBounds(float agentRadius)
        {
            Bounds b;

            if (useColliderBounds && sourceCollider != null)
            {
                b = sourceCollider.bounds;
            }
            else
            {
                // manual local -> world AABB (approx)
                Vector3 c = transform.TransformPoint(manualCenter);
                Vector3 ls = transform.lossyScale;
                Vector3 s = new Vector3(
                    Mathf.Abs(manualSize.x * ls.x),
                    Mathf.Abs(manualSize.y * ls.y),
                    Mathf.Abs(manualSize.z * ls.z)
                );

                b = new Bounds(c, s);
            }

            if (inflateByAgentRadius && agentRadius > 0f)
            {
                b.Expand(Vector3.one * (agentRadius * 2f));
            }

            return b;
        }

        // Convenience API
        public void SetDoorOpen(bool open) => doorOpen = open;
        public void SetHazardEnabled(bool enabled) => hazardEnabled = enabled;
    }
}
