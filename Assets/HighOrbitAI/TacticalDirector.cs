using UnityEngine;

namespace HighOrbitAI
{
    public class TacticalDirector
    {
        public enum AltitudeBand { Low, Mid, High }
        public enum Tactic { None, CruisePatrol, EngageMelee, StrafeOrbit, CoverApproach, ClimbReset, DiveAttack }
        public enum Phase { None, DiveSetup, DiveCommit, EngageMelee, EscapeReset, OrbitShoot, CoverApproach, FlankSetup }

        public struct Result
        {
            public Tactic tactic;
            public Phase phase;
            public AltitudeBand band;
            public Vector3 targetPos;
            public Vector3 extraSteer;
            public FlightController.FlightProfile profile;
            public float profileHold;
        }

        [Header("Ranges")]
        public float meleeRange = 35f;
        public float meleeExitRange = 55f;
        public float shootingRange = 140f;

        [Header("Orbit")]
        public float orbitRadius = 55f;
        public float orbitAngularDegPerSec = 70f;

        [Header("Reset / Density")]
        public float resetMinDist = 45f;
        public float obstacleDensityToReset = 0.55f;

        [Header("Threat bias")]
        [Range(0f, 1f)] public float underFireToReset = 0.55f;
        [Range(0f, 1f)] public float targetedToHigh = 0.45f;

        [Header("Cover Approach")]
        public float coverLateralOffset = 60f;
        public int coverTrySteps = 3;
        public float coverMaxOffset = 140f;

        [Header("Dive Attack")]
        public float diveSetupDist = 220f;
        public float diveCommitDist = 160f;
        public float diveBoostHold = 0.35f;

        [Header("Interceptor Flank")]
        public float flankBehindDist = 85f;
        public float flankLateralDist = 60f;
        public float flankHold = 0.9f;

        [Header("Gunner")]
        public float gunnerAvoidMeleeDist = 55f;
        public float gunnerPreferHigh = 0.8f;

        [Header("Support")]
        public float supportPreferredMin = 90f;
        public float supportPreferredMax = 160f;
        public float supportOrbitRadius = 75f;
        public float supportEscapeBias = 0.75f;

        [Header("Phase Timing (seconds)")]
        public float phaseMinHold = 0.45f;
        public float meleeHold = 0.65f;
        public float orbitHold = 1.00f;
        public float coverHold = 0.85f;
        public float diveSetupHold = 0.90f;
        public float diveCommitHold = 0.55f;
        public float escapeHold = 0.90f;

        [Header("LOS thickness sampling (optional)")]
        public bool useThicknessSampling = true;
        public float segmentSampleStep = 10f;
        public float segmentEndIgnore = 2.0f;

        Phase phase = Phase.None;
        float phaseUntil = -1f;
        Vector3 lockedOrbitSide;
        float lastPhaseSetTime = -999f;

        public Phase CurrentPhase => phase;

        void SetPhase(Phase p, float now, float holdSec)
        {
            phase = p;
            phaseUntil = now + Mathf.Max(phaseMinHold, holdSec);
            lastPhaseSetTime = now;
        }

        bool PhaseLocked(float now) => now < phaseUntil;

        public Result Decide(
            Vector3 selfPos,
            Vector3 selfForward,
            Transform enemy,
            VolumeDatabase db,
            float agentRadius,
            bool inKeepOut,
            ICombatStateProvider combatState,
            float underFire01,
            float targeted01,
            SquadRole role,
            float nowTime)
        {
            var r = new Result
            {
                tactic = Tactic.CruisePatrol,
                phase = phase,
                band = AltitudeBand.Mid,
                targetPos = selfPos + selfForward * 40f,
                extraSteer = Vector3.zero,
                profile = FlightController.FlightProfile.Cruise,
                profileHold = 0.18f
            };

            if (inKeepOut)
            {
                if (phase != Phase.EscapeReset) SetPhase(Phase.EscapeReset, nowTime, escapeHold);
                r.phase = phase;
                r.tactic = Tactic.ClimbReset;
                r.band = AltitudeBand.High;
                r.targetPos = selfPos + selfForward * 80f;
                r.profile = FlightController.FlightProfile.Evade;
                r.profileHold = 0.30f;
                return r;
            }

            if (enemy == null)
            {
                if (phase != Phase.None) SetPhase(Phase.None, nowTime, 0f);
                r.phase = phase;
                r.tactic = Tactic.CruisePatrol;
                r.band = AltitudeBand.Mid;
                r.profile = FlightController.FlightProfile.Cruise;
                r.profileHold = 0.20f;
                return r;
            }

            Vector3 enemyPos = enemy.position;
            Vector3 toEnemy = enemyPos - selfPos;
            float dist = toEnemy.magnitude;
            Vector3 dir = (dist > 1e-3f) ? (toEnemy / dist) : selfForward;

            bool melee = combatState != null && combatState.IsMeleeEngaging;

            bool inShootRange = dist <= shootingRange;
            bool hasLOS = HasLineOfSight(selfPos, enemyPos, db, agentRadius);

            float enemyThreat = 0f;
            float enemyWeapon = 0f, enemyHp = 1f, enemyLock = 0f;
            var ti = enemy.GetComponent<IThreatInfoProvider>();
            if (ti != null)
            {
                enemyWeapon = Mathf.Clamp01(ti.WeaponThreat01);
                enemyHp = Mathf.Clamp01(ti.Hp01);
                enemyLock = Mathf.Clamp01(ti.LockOnThreat01);
                enemyThreat = Mathf.Clamp01(enemyWeapon * 0.55f + enemyHp * 0.20f + enemyLock * 0.65f);
            }

            float dangerSelf = Mathf.Clamp01(Mathf.Max(underFire01, targeted01));
            bool preferHigh = targeted01 >= targetedToHigh;

            float escapeBias = (role == SquadRole.Support) ? supportEscapeBias : 1f;
            float underFireResetTh = underFireToReset * Mathf.Lerp(1f, 0.85f, (role == SquadRole.Support || role == SquadRole.Gunner) ? 1f : 0f);

            if (PhaseLocked(nowTime))
                return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);

            if (dangerSelf >= underFireResetTh * escapeBias && dist >= resetMinDist)
            {
                SetPhase(Phase.EscapeReset, nowTime, escapeHold);
                return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
            }

            if (role == SquadRole.Interceptor)
            {
                if (dist > meleeRange && dist < diveSetupDist && TryBuildFlankPoint(selfPos, enemy, enemyPos, db, agentRadius, out _))
                {
                    SetPhase(Phase.FlankSetup, nowTime, flankHold);
                    return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
                }

                if (dist <= meleeRange || melee)
                {
                    SetPhase(Phase.EngageMelee, nowTime, meleeHold);
                    return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
                }
            }
            else
            {
                if (dist <= gunnerAvoidMeleeDist)
                {
                    SetPhase(Phase.EscapeReset, nowTime, escapeHold);
                    return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
                }
            }

            float density = EstimateObstacleDensityXZ(selfPos, enemyPos, db, agentRadius);
            if (density >= obstacleDensityToReset && dist >= resetMinDist)
            {
                SetPhase(Phase.EscapeReset, nowTime, escapeHold);
                return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
            }

            if (dist >= diveSetupDist && role == SquadRole.Interceptor)
            {
                SetPhase(Phase.DiveSetup, nowTime, diveSetupHold);
                return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
            }

            if (inShootRange)
            {
                if (hasLOS)
                {
                    lockedOrbitSide = (lockedOrbitSide.sqrMagnitude < 0.01f)
                        ? Vector3.Cross(Vector3.up, dir).normalized * (Random.value < 0.5f ? 1f : -1f)
                        : lockedOrbitSide.normalized;

                    SetPhase(Phase.OrbitShoot, nowTime, orbitHold);
                    return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
                }
                else
                {
                    SetPhase(Phase.CoverApproach, nowTime, coverHold);
                    return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
                }
            }

            if (role == SquadRole.Support)
            {
                SetPhase(Phase.OrbitShoot, nowTime, orbitHold);
                return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
            }

            SetPhase(Phase.CoverApproach, nowTime, coverHold);
            return BuildByPhase(phase, selfPos, selfForward, enemy, enemyPos, dir, dist, db, agentRadius, hasLOS, underFire01, targeted01, role, enemyThreat, nowTime);
        }

        Result BuildByPhase(
            Phase ph,
            Vector3 selfPos,
            Vector3 selfForward,
            Transform enemy,
            Vector3 enemyPos,
            Vector3 dirToEnemy,
            float dist,
            VolumeDatabase db,
            float agentRadius,
            bool hasLOS,
            float underFire01,
            float targeted01,
            SquadRole role,
            float enemyThreat01,
            float nowTime)
        {
            var r = new Result
            {
                phase = ph,
                tactic = Tactic.CoverApproach,
                band = AltitudeBand.Mid,
                targetPos = enemyPos,
                extraSteer = Vector3.zero,
                profile = FlightController.FlightProfile.Cruise,
                profileHold = 0.18f
            };

            bool preferHigh = targeted01 >= targetedToHigh;
            float dangerSelf = Mathf.Clamp01(Mathf.Max(underFire01, targeted01));

            if (role == SquadRole.Gunner) preferHigh = true;
            if (role == SquadRole.Support && dangerSelf > 0.25f) preferHigh = true;

            switch (ph)
            {
                case Phase.FlankSetup:
                    r.tactic = Tactic.CoverApproach;
                    r.band = AltitudeBand.Mid;
                    r.profile = FlightController.FlightProfile.Cruise;
                    r.profileHold = 0.18f;

                    if (TryBuildFlankPoint(selfPos, enemy, enemyPos, db, agentRadius, out var flankPoint))
                        r.targetPos = flankPoint;
                    else
                        r.targetPos = enemyPos;

                    {
                        float dx = r.targetPos.x - selfPos.x;
                        float dz = r.targetPos.z - selfPos.z;
                        float dXZ = Mathf.Sqrt(dx * dx + dz * dz);
                        if (dXZ <= 35f)
                            SetPhase(Phase.DiveCommit, nowTime, diveCommitHold);
                    }

                    if (dist <= meleeRange)
                        SetPhase(Phase.EngageMelee, nowTime, meleeHold);

                    return r;

                case Phase.DiveSetup:
                    r.tactic = Tactic.DiveAttack;
                    r.band = AltitudeBand.High;
                    r.targetPos = enemyPos;
                    r.profile = FlightController.FlightProfile.Boost;
                    r.profileHold = 0.25f;

                    if (dist <= diveCommitDist)
                        SetPhase(Phase.DiveCommit, nowTime, diveCommitHold);

                    return r;

                case Phase.DiveCommit:
                    r.tactic = Tactic.DiveAttack;
                    r.band = AltitudeBand.Mid;
                    r.targetPos = enemyPos;
                    r.profile = FlightController.FlightProfile.Boost;
                    r.profileHold = diveBoostHold;

                    if (dist <= meleeRange && role == SquadRole.Interceptor)
                        SetPhase(Phase.EngageMelee, nowTime, meleeHold);

                    if (role != SquadRole.Interceptor && dist <= gunnerAvoidMeleeDist)
                        SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                    return r;

                case Phase.EngageMelee:
                    r.tactic = Tactic.EngageMelee;
                    r.band = AltitudeBand.Low;
                    r.targetPos = enemyPos;
                    r.profile = FlightController.FlightProfile.EngageMelee;
                    r.profileHold = 0.22f;

                    if (dangerSelf >= underFireToReset || dist >= meleeExitRange)
                        SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                    return r;

                case Phase.EscapeReset:
                    r.tactic = Tactic.ClimbReset;
                    r.band = AltitudeBand.High;
                    r.targetPos = enemyPos;
                    r.profile = FlightController.FlightProfile.Evade;
                    r.profileHold = 0.25f;

                    if (role == SquadRole.Interceptor && dist >= diveSetupDist * 0.85f)
                        SetPhase(Phase.DiveSetup, nowTime, diveSetupHold);

                    if (role != SquadRole.Interceptor && dist >= shootingRange * 0.9f)
                        SetPhase(Phase.OrbitShoot, nowTime, orbitHold);

                    return r;

                case Phase.OrbitShoot:
                    r.tactic = Tactic.StrafeOrbit;

                    if (role == SquadRole.Gunner) r.band = AltitudeBand.High;
                    else if (role == SquadRole.Support) r.band = preferHigh ? AltitudeBand.High : AltitudeBand.Mid;
                    else r.band = preferHigh ? AltitudeBand.High : AltitudeBand.Mid;

                    float rad = orbitRadius;
                    if (role == SquadRole.Support) rad = supportOrbitRadius;

                    r.profile = FlightController.FlightProfile.Shooting;
                    r.profileHold = 0.20f;

                    {
                        Vector3 right = (lockedOrbitSide.sqrMagnitude > 0.01f)
                            ? lockedOrbitSide.normalized
                            : Vector3.Cross(Vector3.up, dirToEnemy).normalized;

                        float ang = (nowTime - lastPhaseSetTime) * orbitAngularDegPerSec * Mathf.Deg2Rad;
                        Vector3 forwardOnPlane = Vector3.Cross(dirToEnemy, right).normalized;
                        Vector3 orbitOffset = (Mathf.Cos(ang) * right + Mathf.Sin(ang) * forwardOnPlane).normalized * rad;
                        r.targetPos = enemyPos + orbitOffset;
                    }

                    if (role == SquadRole.Support)
                    {
                        if (dist < supportPreferredMin)
                        {
                            r.extraSteer += (-dirToEnemy) * 0.6f;
                            r.profile = FlightController.FlightProfile.Evade;
                            r.profileHold = 0.18f;
                        }
                        else if (dist > supportPreferredMax)
                        {
                            r.extraSteer += (dirToEnemy) * 0.35f;
                        }
                    }

                    if (!hasLOS)
                        SetPhase(Phase.CoverApproach, nowTime, coverHold);

                    float threatEscape = Mathf.Lerp(underFireToReset, underFireToReset * 0.85f, enemyThreat01);
                    if ((role == SquadRole.Gunner || role == SquadRole.Support) && dangerSelf >= threatEscape)
                        SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                    if (role == SquadRole.Interceptor && dist <= meleeRange)
                        SetPhase(Phase.EngageMelee, nowTime, meleeHold);

                    if (role != SquadRole.Interceptor && dist <= gunnerAvoidMeleeDist)
                        SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                    return r;

                case Phase.CoverApproach:
                default:
                    r.tactic = Tactic.CoverApproach;

                    if (role == SquadRole.Interceptor)
                    {
                        if (TryBuildFlankPoint(selfPos, enemy, enemyPos, db, agentRadius, out var flankPoint2))
                            r.targetPos = flankPoint2;
                        else
                            r.targetPos = enemyPos;

                        r.band = AltitudeBand.Mid;
                        r.profile = FlightController.FlightProfile.Cruise;
                        r.profileHold = 0.16f;

                        if (dist <= diveCommitDist)
                            SetPhase(Phase.DiveCommit, nowTime, diveCommitHold);

                        return r;
                    }

                    if (role == SquadRole.Gunner)
                    {
                        r.band = AltitudeBand.High;
                        r.profile = hasLOS ? FlightController.FlightProfile.Shooting : FlightController.FlightProfile.Cruise;
                        r.profileHold = 0.16f;

                        if (dist <= gunnerAvoidMeleeDist)
                            SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                        if (hasLOS)
                            SetPhase(Phase.OrbitShoot, nowTime, orbitHold);

                        if (!hasLOS && db != null)
                        {
                            Vector3 lateral = Vector3.Cross(Vector3.up, dirToEnemy).normalized;
                            if (TryFindSideApproach(selfPos, enemyPos, lateral, db, agentRadius, out var best) ||
                                TryFindSideApproach(selfPos, enemyPos, -lateral, db, agentRadius, out best))
                                r.targetPos = best;
                        }

                        return r;
                    }

                    if (role == SquadRole.Support)
                    {
                        r.band = preferHigh ? AltitudeBand.High : AltitudeBand.Mid;
                        r.profile = hasLOS ? FlightController.FlightProfile.Shooting : FlightController.FlightProfile.Cruise;
                        r.profileHold = 0.16f;

                        if (dist < supportPreferredMin)
                        {
                            r.extraSteer += (-dirToEnemy) * 0.7f;
                            r.profile = FlightController.FlightProfile.Evade;
                            r.profileHold = 0.18f;
                        }
                        else if (dist > supportPreferredMax)
                        {
                            r.extraSteer += (dirToEnemy) * 0.4f;
                        }

                        if (!hasLOS && db != null)
                        {
                            Vector3 lateral = Vector3.Cross(Vector3.up, dirToEnemy).normalized;
                            if (TryFindSideApproach(selfPos, enemyPos, lateral, db, agentRadius, out var best) ||
                                TryFindSideApproach(selfPos, enemyPos, -lateral, db, agentRadius, out best))
                                r.targetPos = best;
                        }

                        if (dangerSelf >= underFireToReset * supportEscapeBias)
                            SetPhase(Phase.EscapeReset, nowTime, escapeHold);

                        if (hasLOS)
                            SetPhase(Phase.OrbitShoot, nowTime, orbitHold);

                        return r;
                    }

                    r.band = hasLOS ? AltitudeBand.Mid : AltitudeBand.Low;
                    r.targetPos = enemyPos;
                    r.profile = (dist <= shootingRange && hasLOS) ? FlightController.FlightProfile.Shooting : FlightController.FlightProfile.Cruise;
                    r.profileHold = 0.15f;
                    return r;
            }
        }

        bool TryBuildFlankPoint(Vector3 selfPos, Transform enemy, Vector3 enemyPos, VolumeDatabase db, float agentRadius, out Vector3 flankPos)
        {
            flankPos = enemyPos;
            if (enemy == null) return false;

            Vector3 enemyFwd = enemy.forward;
            Vector3 enemyRight = enemy.right;

            Vector3 behind = enemyPos - enemyFwd.normalized * flankBehindDist;

            Vector3 toSelf = (selfPos - enemyPos);
            float side = Mathf.Sign(Vector3.Dot(toSelf, enemyRight));
            if (Mathf.Abs(side) < 0.01f) side = 1f;

            Vector3 lateral = enemyRight.normalized * (flankLateralDist * side);

            Vector3 c1 = behind + lateral;
            Vector3 c2 = behind - lateral;

            if (IsPointOk(selfPos, c1, enemyPos, db, agentRadius))
            {
                flankPos = c1;
                return true;
            }
            if (IsPointOk(selfPos, c2, enemyPos, db, agentRadius))
            {
                flankPos = c2;
                return true;
            }

            return false;
        }

        bool IsPointOk(Vector3 selfPos, Vector3 candidate, Vector3 enemyPos, VolumeDatabase db, float agentRadius)
        {
            if (db == null) return true;

            if (db.SegmentHitsHardAny(selfPos, candidate)) return false;
            if (db.SegmentHitsHardAny(candidate, enemyPos)) return false;

            if (!useThicknessSampling) return true;

            if (!HasLineOfSight(selfPos, candidate, db, agentRadius)) return false;
            if (!HasLineOfSight(candidate, enemyPos, db, agentRadius)) return false;

            return true;
        }

        bool HasLineOfSight(Vector3 a, Vector3 b, VolumeDatabase db, float agentRadius)
        {
            if (db == null) return true;

            if (db.SegmentHitsHardAny(a, b)) return false;
            if (!useThicknessSampling) return true;

            Vector3 ab = b - a;
            float len = ab.magnitude;
            if (len <= 0.001f) return true;

            float step = Mathf.Max(2.0f, segmentSampleStep);
            int n = Mathf.Max(2, Mathf.CeilToInt(len / step) + 1);

            float ignore = Mathf.Max(0f, segmentEndIgnore);
            float tStart = Mathf.Clamp01(ignore / len);
            float tEnd = Mathf.Clamp01(1f - (ignore / len));
            if (tEnd <= tStart) { tStart = 0f; tEnd = 1f; }

            for (int i = 0; i < n; i++)
            {
                float t = Mathf.Lerp(tStart, tEnd, i / (float)(n - 1));
                Vector3 p = a + ab * t;

                db.EvaluatePoint(p, agentRadius, out var flags, out _);
                if ((flags & (NavFlags.Blocked | NavFlags.KeepOut)) != 0) return false;
            }

            return true;
        }

        float EstimateObstacleDensityXZ(Vector3 selfPos, Vector3 enemyPos, VolumeDatabase db, float agentRadius)
        {
            if (db == null) return 0f;

            Vector3 mid = (selfPos + enemyPos) * 0.5f;
            float r = Mathf.Clamp(Vector3.Distance(selfPos, enemyPos) * 0.25f, 25f, 110f);

            int samples = 8;
            int hit = 0;
            for (int i = 0; i < samples; i++)
            {
                float ang = (i / (float)samples) * Mathf.PI * 2f;
                Vector3 p = mid + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;

                db.EvaluatePoint(p, agentRadius, out var flags, out _);
                if ((flags & NavFlags.Blocked) != 0 || (flags & NavFlags.KeepOut) != 0) hit++;
            }

            return hit / (float)samples;
        }

        bool TryFindSideApproach(
            Vector3 selfPos,
            Vector3 enemyPos,
            Vector3 lateralDir,
            VolumeDatabase db,
            float agentRadius,
            out Vector3 outPos)
        {
            outPos = enemyPos;
            if (db == null) return false;

            for (int i = 1; i <= Mathf.Max(1, coverTrySteps); i++)
            {
                float k = i / (float)coverTrySteps;
                float offset = Mathf.Lerp(coverLateralOffset, coverMaxOffset, k);
                Vector3 candidate = enemyPos + lateralDir * offset;

                bool ok1 = !db.SegmentHitsHardAny(selfPos, candidate);
                bool ok2 = !db.SegmentHitsHardAny(candidate, enemyPos);
                if (!ok1 || !ok2) continue;

                if (useThicknessSampling)
                {
                    if (!HasLineOfSight(selfPos, candidate, db, agentRadius)) continue;
                    if (!HasLineOfSight(candidate, enemyPos, db, agentRadius)) continue;
                }

                outPos = candidate;
                return true;
            }

            return false;
        }
    }
}
