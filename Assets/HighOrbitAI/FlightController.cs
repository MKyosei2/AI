using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class FlightController : MonoBehaviour
    {
        [Header("Kinematics")]
        public float maxSpeed = 28f;
        public float minCruiseSpeed = 8f;     // ★ホバー感を潰す
        public float maxAccel = 55f;
        public float maxYawDegPerSec = 240f;
        public float maxClimbRate = 18f;

        [Header("Path Follow")]
        public float waypointReachDist = 3.0f;
        public int lookAhead = 1;

        [Header("Safety")]
        public float keepOutPushStrength = 35f;

        readonly List<Vector3> path = new List<Vector3>(256);
        int index;
        Vector3 velocity;
        Vector3 extraSteer;

        public IReadOnlyList<Vector3> CurrentPath => path;
        public Vector3 Velocity => velocity;
        public int CurrentWaypointIndex => index;

        public void SetPath(List<Vector3> newPath)
        {
            path.Clear();
            if (newPath != null) path.AddRange(newPath);
            index = 0;
        }

        public void SetExtraSteer(Vector3 steer) => extraSteer = steer;

        public void Tick(float dt)
        {
            if (path.Count == 0) return;

            int targetIndex = Mathf.Clamp(index + lookAhead, 0, path.Count - 1);
            Vector3 target = path[targetIndex];

            if (Vector3.Distance(transform.position, path[index]) <= waypointReachDist)
                index = Mathf.Min(index + 1, path.Count - 1);

            Vector3 to = (target - transform.position);
            if (to.sqrMagnitude < 1e-6f) return;

            Vector3 desiredDir = (to.normalized + extraSteer).normalized;
            float desiredSpeed = Mathf.Clamp(to.magnitude * 1.2f, minCruiseSpeed, maxSpeed);
            Vector3 desiredVel = desiredDir * desiredSpeed;

            desiredVel.y = Mathf.Clamp(desiredVel.y, -maxClimbRate, maxClimbRate);

            Vector3 dv = desiredVel - velocity;
            float maxDv = maxAccel * dt;
            if (dv.magnitude > maxDv) dv = dv.normalized * maxDv;
            velocity += dv;

            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Quaternion targetRot = Quaternion.LookRotation(planar.normalized, Vector3.up);
                float maxYaw = maxYawDegPerSec * dt;
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, maxYaw);
            }

            transform.position += velocity * dt;
            extraSteer = Vector3.zero;
        }

        public void ApplyKeepOutPush(Vector3 pushDir, float dt)
        {
            velocity += pushDir.normalized * keepOutPushStrength * dt;
        }
    }
}
