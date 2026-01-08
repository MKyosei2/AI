using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 経路（点列）を追従しつつ、速度/加速度/旋回/上昇率制限を守る簡易フライト制御。
    /// Rigidbody無しのTransform移動（必要ならRigidbody版へ拡張可能）
    /// </summary>
    public class FlightController : MonoBehaviour
    {
        [Header("Kinematics")]
        public float maxSpeed = 18f;
        public float maxAccel = 35f;
        public float maxYawDegPerSec = 220f;
        public float maxClimbRate = 18f; // m/s

        [Header("Path Follow")]
        public float waypointReachDist = 2.0f;
        public int lookAhead = 1; // 先読みウェイポイント

        [Header("KeepOut Safety")]
        public float keepOutPushStrength = 30f;

        readonly List<Vector3> path = new List<Vector3>(256);
        int index;
        Vector3 velocity;

        public IReadOnlyList<Vector3> CurrentPath => path;

        public void SetPath(List<Vector3> newPath)
        {
            path.Clear();
            if (newPath != null) path.AddRange(newPath);
            index = 0;
        }

        public void Tick(float dt)
        {
            if (path.Count == 0) return;

            int targetIndex = Mathf.Clamp(index + lookAhead, 0, path.Count - 1);
            Vector3 target = path[targetIndex];

            // 到達判定（現在index）
            if (Vector3.Distance(transform.position, path[index]) <= waypointReachDist)
                index = Mathf.Min(index + 1, path.Count - 1);

            // 目標方向
            Vector3 to = (target - transform.position);
            if (to.sqrMagnitude < 1e-6f) return;

            // 上昇率制限：y方向速度上限を作る
            float desiredSpeed = maxSpeed;
            Vector3 desiredVel = to.normalized * desiredSpeed;

            // climb clamp
            desiredVel.y = Mathf.Clamp(desiredVel.y, -maxClimbRate, maxClimbRate);

            // 加速度制限
            Vector3 dv = desiredVel - velocity;
            float maxDv = maxAccel * dt;
            if (dv.magnitude > maxDv) dv = dv.normalized * maxDv;
            velocity += dv;

            // 旋回制限（yawのみ簡易）
            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Quaternion targetRot = Quaternion.LookRotation(planar.normalized, Vector3.up);
                float maxYaw = maxYawDegPerSec * dt;
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, maxYaw);
            }

            // 位置更新
            transform.position += velocity * dt;
        }

        public void ApplyKeepOutPush(Vector3 pushDir, float dt)
        {
            // KeepOut内部に入ってしまったときの押し戻し（最後の砦）
            velocity += pushDir.normalized * keepOutPushStrength * dt;
        }
    }
}
