using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class FlightController : MonoBehaviour
    {
        [Header("Kinematics (Base)")]
        public float maxSpeed = 28f;
        public float minCruiseSpeed = 8f;     // ★ホバー感を潰す
        public float maxAccel = 55f;
        public float maxYawDegPerSec = 240f;
        public float maxClimbRate = 18f;

        [Header("Handling (Inertia Tuning)")]
        [Range(0.1f, 2.0f)]
        public float handling = 1.0f;

        [Range(0f, 10f)]
        public float linearDamping = 0.0f;

        [Header("Profile System (Next-Gen)")]
        [Min(0f)]
        public float profileBlendTime = 0.18f;

        public enum FlightProfile
        {
            Cruise,
            EngageMelee,
            Shooting,
            Evade,
            Boost
        }

        [System.Serializable]
        public struct ProfileTuning
        {
            [Range(0.1f, 2.5f)] public float handlingMul;
            [Range(0f, 10f)] public float dampingAdd;
            [Range(0.1f, 2.0f)] public float speedMul;
            [Range(0.1f, 2.5f)] public float accelMul;
            [Range(0.1f, 2.5f)] public float yawMul;
            [Range(0.1f, 2.5f)] public float climbMul;
        }

        [Header("Profile Tunings")]
        public ProfileTuning cruise = new ProfileTuning
        {
            handlingMul = 1.0f, dampingAdd = 0.0f, speedMul = 1.0f, accelMul = 1.0f, yawMul = 1.0f, climbMul = 1.0f
        };

        public ProfileTuning engageMelee = new ProfileTuning
        {
            handlingMul = 1.35f, dampingAdd = 0.8f, speedMul = 0.95f, accelMul = 1.15f, yawMul = 1.35f, climbMul = 1.20f
        };

        public ProfileTuning shooting = new ProfileTuning
        {
            handlingMul = 1.15f, dampingAdd = 1.2f, speedMul = 0.90f, accelMul = 1.00f, yawMul = 1.10f, climbMul = 1.00f
        };

        public ProfileTuning evade = new ProfileTuning
        {
            handlingMul = 1.55f, dampingAdd = 0.4f, speedMul = 1.05f, accelMul = 1.45f, yawMul = 1.55f, climbMul = 1.35f
        };

        public ProfileTuning boost = new ProfileTuning
        {
            handlingMul = 1.10f, dampingAdd = 0.0f, speedMul = 1.30f, accelMul = 1.25f, yawMul = 1.00f, climbMul = 1.10f
        };

        [Header("Path Follow")]
        public float waypointReachDist = 3.0f;
        public int lookAhead = 1;

        [Header("Safety")]
        public float keepOutPushStrength = 35f;

        readonly List<Vector3> path = new List<Vector3>(256);
        int index;
        Vector3 velocity;
        Vector3 extraSteer;

        FlightProfile currentProfile = FlightProfile.Cruise;
        FlightProfile targetProfile = FlightProfile.Cruise;

        float profileBlendT = 1f;          // 0..1
        float profileHoldUntil = -1f;      // time until which we stick to targetProfile

        public IReadOnlyList<Vector3> CurrentPath => path;
        public Vector3 Velocity => velocity;
        public int CurrentWaypointIndex => index;
        public FlightProfile CurrentProfile => currentProfile;
        public FlightProfile TargetProfile => targetProfile;

        public void SetProfile(FlightProfile profile, float holdSeconds = 0f)
        {
            targetProfile = profile;
            if (holdSeconds > 0f) profileHoldUntil = Time.time + holdSeconds;

            if (currentProfile != targetProfile || profileBlendT >= 1f)
                profileBlendT = 0f;
        }

        public void SetPath(List<Vector3> newPath)
        {
            path.Clear();
            if (newPath != null) path.AddRange(newPath);
            index = 0;
        }

        public void SetExtraSteer(Vector3 steer) => extraSteer = steer;

        public void Tick(float dt)
        {
            if (profileHoldUntil > 0f && Time.time >= profileHoldUntil && targetProfile != FlightProfile.Cruise)
            {
                targetProfile = FlightProfile.Cruise;
                profileBlendT = 0f;
                profileHoldUntil = -1f;
            }

            if (currentProfile != targetProfile || profileBlendT < 1f)
            {
                float blendDur = Mathf.Max(0.0001f, profileBlendTime);
                profileBlendT = Mathf.Clamp01(profileBlendT + dt / blendDur);
                if (profileBlendT >= 1f) currentProfile = targetProfile;
            }

            if (path.Count == 0) return;

            int targetIndex = Mathf.Clamp(index + lookAhead, 0, path.Count - 1);
            Vector3 target = path[targetIndex];

            if (Vector3.Distance(transform.position, path[index]) <= waypointReachDist)
                index = Mathf.Min(index + 1, path.Count - 1);

            Vector3 to = (target - transform.position);
            if (to.sqrMagnitude < 1e-6f) return;

            ProfileTuning a = GetTuning(currentProfile);
            ProfileTuning b = GetTuning(targetProfile);
            float t = profileBlendT;

            float handlingMul = Mathf.Lerp(a.handlingMul, b.handlingMul, t);
            float dampingAdd  = Mathf.Lerp(a.dampingAdd,  b.dampingAdd,  t);
            float speedMul    = Mathf.Lerp(a.speedMul,    b.speedMul,    t);
            float accelMul    = Mathf.Lerp(a.accelMul,    b.accelMul,    t);
            float yawMul      = Mathf.Lerp(a.yawMul,      b.yawMul,      t);
            float climbMul    = Mathf.Lerp(a.climbMul,    b.climbMul,    t);

            float effHandling = handling * handlingMul;
            float effDamping  = Mathf.Max(0f, linearDamping + dampingAdd);

            float effMaxSpeed = Mathf.Max(0.01f, maxSpeed * speedMul);
            float effAccel    = Mathf.Max(0.01f, maxAccel * accelMul * effHandling);
            float effYaw      = Mathf.Max(0.01f, maxYawDegPerSec * yawMul * effHandling);
            float effClimb    = Mathf.Max(0.01f, maxClimbRate * climbMul * effHandling);

            Vector3 desiredDir = (to.normalized + extraSteer).normalized;
            float desiredSpeed = Mathf.Clamp(to.magnitude * 1.2f, minCruiseSpeed, effMaxSpeed);

            Vector3 desiredVel = desiredDir * desiredSpeed;
            desiredVel.y = Mathf.Clamp(desiredVel.y, -effClimb, effClimb);

            Vector3 dv = desiredVel - velocity;
            float maxDv = effAccel * dt;
            if (dv.magnitude > maxDv) dv = dv.normalized * maxDv;
            velocity += dv;

            if (effDamping > 0f)
            {
                float k = Mathf.Clamp01(1f - (effDamping * dt));
                velocity *= k;
            }

            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Quaternion targetRot = Quaternion.LookRotation(planar.normalized, Vector3.up);
                float maxYaw = effYaw * dt;
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, maxYaw);
            }

            transform.position += velocity * dt;
            extraSteer = Vector3.zero;
        }

        public void ApplyKeepOutPush(Vector3 pushDir, float dt)
        {
            velocity += pushDir.normalized * keepOutPushStrength * dt;
        }

        ProfileTuning GetTuning(FlightProfile p)
        {
            switch (p)
            {
                case FlightProfile.EngageMelee: return engageMelee;
                case FlightProfile.Shooting:    return shooting;
                case FlightProfile.Evade:       return evade;
                case FlightProfile.Boost:       return boost;
                default:                        return cruise;
            }
        }
    }
}
