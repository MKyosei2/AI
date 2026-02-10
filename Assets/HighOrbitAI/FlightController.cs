using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class FlightController : MonoBehaviour
    {
        public enum FlightProfile
        {
            Cruise,
            Shooting,
            EngageMelee,
            Evade,
            Boost
        }

        [System.Serializable]
        public struct ProfileParams
        {
            [Header("Speed")]
            public float maxSpeed;
            public float minCruiseSpeed;

            [Header("Inertia / Turn")]
            public float maxAccel;
            public float maxYawDegPerSec;
            public float maxClimbRate;

            [Header("Responsiveness (0..1) 低いほど慣性が強い)")]
            [Range(0.05f, 1f)]
            public float responsiveness;
        }

        [Header("Base Kinematics (fallback / Cruise default)")]
        public float maxSpeed = 28f;
        public float minCruiseSpeed = 8f;
        public float maxAccel = 55f;
        public float maxYawDegPerSec = 240f;
        public float maxClimbRate = 18f;

        [Header("Profiles (override)")]
        public ProfileParams cruise = new ProfileParams
        {
            maxSpeed = 28f, minCruiseSpeed = 8f,
            maxAccel = 55f, maxYawDegPerSec = 240f, maxClimbRate = 18f,
            responsiveness = 0.55f
        };

        public ProfileParams shooting = new ProfileParams
        {
            maxSpeed = 24f, minCruiseSpeed = 7f,
            maxAccel = 45f, maxYawDegPerSec = 220f, maxClimbRate = 14f,
            responsiveness = 0.45f
        };

        public ProfileParams engageMelee = new ProfileParams
        {
            maxSpeed = 30f, minCruiseSpeed = 9f,
            maxAccel = 70f, maxYawDegPerSec = 320f, maxClimbRate = 22f,
            responsiveness = 0.75f
        };

        public ProfileParams evade = new ProfileParams
        {
            maxSpeed = 32f, minCruiseSpeed = 10f,
            maxAccel = 80f, maxYawDegPerSec = 360f, maxClimbRate = 26f,
            responsiveness = 0.90f
        };

        public ProfileParams boost = new ProfileParams
        {
            maxSpeed = 42f, minCruiseSpeed = 12f,
            maxAccel = 95f, maxYawDegPerSec = 300f, maxClimbRate = 26f,
            responsiveness = 0.85f
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

        // profile blending
        FlightProfile currentProfile = FlightProfile.Cruise;
        FlightProfile targetProfile = FlightProfile.Cruise;
        float profileBlend01 = 1f;
        float profileBlendSpeed = 6f;

        public IReadOnlyList<Vector3> CurrentPath => path;
        public Vector3 Velocity => velocity;
        public int CurrentWaypointIndex => index;

        public FlightProfile CurrentProfile => currentProfile;
        public FlightProfile TargetProfile => targetProfile;

        public void SetPath(List<Vector3> newPath)
        {
            path.Clear();
            if (newPath != null) path.AddRange(newPath);
            index = 0;
        }

        public void SetExtraSteer(Vector3 steer) => extraSteer = steer;

        public void ResetVelocity() => velocity = Vector3.zero;

        public void SetProfile(FlightProfile p, float holdSeconds = 0f)
        {
            // holdSeconds は「頻繁に切替えすぎない」ための意図だったりするが、
            // ここでは HighOrbitAI / TacticalDirector が管理する前提でOK。
            targetProfile = p;
            profileBlend01 = 0f;

            // 体感で滑らかにしたい時はここを上げる
            profileBlendSpeed = Mathf.Clamp(1f / Mathf.Max(0.05f, holdSeconds), 3f, 14f);
        }

        ProfileParams GetParams(FlightProfile p)
        {
            switch (p)
            {
                case FlightProfile.Shooting: return shooting;
                case FlightProfile.EngageMelee: return engageMelee;
                case FlightProfile.Evade: return evade;
                case FlightProfile.Boost: return boost;
                default: return cruise;
            }
        }

        static ProfileParams Lerp(ProfileParams a, ProfileParams b, float t)
        {
            return new ProfileParams
            {
                maxSpeed = Mathf.Lerp(a.maxSpeed, b.maxSpeed, t),
                minCruiseSpeed = Mathf.Lerp(a.minCruiseSpeed, b.minCruiseSpeed, t),
                maxAccel = Mathf.Lerp(a.maxAccel, b.maxAccel, t),
                maxYawDegPerSec = Mathf.Lerp(a.maxYawDegPerSec, b.maxYawDegPerSec, t),
                maxClimbRate = Mathf.Lerp(a.maxClimbRate, b.maxClimbRate, t),
                responsiveness = Mathf.Lerp(a.responsiveness, b.responsiveness, t)
            };
        }

        public void Tick(float dt)
        {
            if (path.Count == 0) return;

            // blend profile
            if (currentProfile != targetProfile)
            {
                profileBlend01 = Mathf.MoveTowards(profileBlend01, 1f, profileBlendSpeed * dt);
                if (profileBlend01 >= 0.999f) currentProfile = targetProfile;
            }
            else
            {
                profileBlend01 = 1f;
            }

            ProfileParams a = GetParams(currentProfile);
            ProfileParams b = GetParams(targetProfile);
            ProfileParams pr = Lerp(a, b, profileBlend01);

            // fallback: if someone never set profile params, use base
            if (pr.maxSpeed <= 0.01f) pr.maxSpeed = maxSpeed;
            if (pr.minCruiseSpeed <= 0.01f) pr.minCruiseSpeed = minCruiseSpeed;
            if (pr.maxAccel <= 0.01f) pr.maxAccel = maxAccel;
            if (pr.maxYawDegPerSec <= 0.01f) pr.maxYawDegPerSec = maxYawDegPerSec;
            if (pr.maxClimbRate <= 0.01f) pr.maxClimbRate = maxClimbRate;
            pr.responsiveness = Mathf.Clamp(pr.responsiveness, 0.05f, 1f);

            // target point
            int targetIndex = Mathf.Clamp(index + lookAhead, 0, path.Count - 1);
            Vector3 target = path[targetIndex];

            if (Vector3.Distance(transform.position, path[index]) <= waypointReachDist)
                index = Mathf.Min(index + 1, path.Count - 1);

            Vector3 to = (target - transform.position);
            if (to.sqrMagnitude < 1e-6f) return;

            // desired velocity
            Vector3 desiredDir = (to.normalized + extraSteer).normalized;
            float desiredSpeed = Mathf.Clamp(to.magnitude * 1.2f, pr.minCruiseSpeed, pr.maxSpeed);
            Vector3 desiredVel = desiredDir * desiredSpeed;
            desiredVel.y = Mathf.Clamp(desiredVel.y, -pr.maxClimbRate, pr.maxClimbRate);

            // inertia: responsiveness scales how quickly we chase desiredVel
            // (低いほど追従が遅い=慣性強め)
            float chase = Mathf.Lerp(0.35f, 1.0f, pr.responsiveness);

            Vector3 dv = (desiredVel - velocity) * chase;
            float maxDv = pr.maxAccel * dt;
            if (dv.magnitude > maxDv) dv = dv.normalized * maxDv;
            velocity += dv;

            // yaw: also affected by responsiveness (慣性で振り向きにくい)
            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Quaternion targetRot = Quaternion.LookRotation(planar.normalized, Vector3.up);
                float yawMul = Mathf.Lerp(0.55f, 1.0f, pr.responsiveness);
                float maxYaw = pr.maxYawDegPerSec * yawMul * dt;
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
