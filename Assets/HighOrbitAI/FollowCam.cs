using UnityEngine;

namespace HighOrbitAI
{
    public class FollowCam : MonoBehaviour
    {
        [Header("Targets")]
        public Transform target;
        public Transform[] targets;

        [Header("Follow")]
        public Vector3 offset = new Vector3(0f, 18f, -38f);
        public Vector3 lookOffset = new Vector3(0f, 6f, 0f);

        [Tooltip("位置の追従時間(小さいほど素早い)")]
        public float positionSmoothTime = 0.10f;

        [Tooltip("回転の追従時間(小さいほど素早い)")]
        public float rotationSmoothTime = 0.08f;

        [Header("Cycle (No Input System required)")]
        [Tooltip("入力を使わず一定間隔で追従対象を切り替える")]
        public bool autoCycle = true;

        [Tooltip("自動切替の間隔(秒)")]
        public float cycleSeconds = 6f;

        int idx;
        Vector3 posVel;
        float yawVel, pitchVel;
        float nextCycleTime;

        void Awake()
        {
            if (targets != null && targets.Length > 0 && target == null)
            {
                idx = 0;
                target = targets[idx];
            }

            nextCycleTime = Time.time + Mathf.Max(0.5f, cycleSeconds);
        }

        void Update()
        {
            if (!autoCycle) return;
            if (targets == null || targets.Length <= 1) return;

            if (Time.time >= nextCycleTime)
            {
                idx = (idx + 1) % targets.Length;
                target = targets[idx];

                posVel = Vector3.zero;
                yawVel = pitchVel = 0f;

                nextCycleTime = Time.time + Mathf.Max(0.5f, cycleSeconds);
            }
        }

        void LateUpdate()
        {
            if (target == null) return;

            Vector3 desiredPos = target.position + (target.rotation * offset);

            transform.position = Vector3.SmoothDamp(
                transform.position,
                desiredPos,
                ref posVel,
                Mathf.Max(0.01f, positionSmoothTime),
                Mathf.Infinity,
                Time.deltaTime);

            Vector3 lookPoint = target.position + lookOffset;
            Vector3 dir = (lookPoint - transform.position);
            if (dir.sqrMagnitude < 0.0001f) dir = target.forward;

            Quaternion desiredRot = Quaternion.LookRotation(dir.normalized, Vector3.up);

            Vector3 e0 = transform.rotation.eulerAngles;
            Vector3 e1 = desiredRot.eulerAngles;

            float yaw = Mathf.SmoothDampAngle(e0.y, e1.y, ref yawVel, Mathf.Max(0.01f, rotationSmoothTime));
            float pitch = Mathf.SmoothDampAngle(e0.x, e1.x, ref pitchVel, Mathf.Max(0.01f, rotationSmoothTime));

            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
    }
}
