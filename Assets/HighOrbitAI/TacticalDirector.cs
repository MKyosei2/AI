using UnityEngine;

namespace HighOrbitAI
{
    public class TacticalDirector
    {
        public enum AltitudeBand { Low, Mid, High }
        public enum Tactic
        {
            None,
            CruisePatrol,
            EngageMelee,
            StrafeOrbit,
            CoverApproach,
            ClimbReset,
            DiveAttack
        }

        public struct Result
        {
            public Tactic tactic;
            public AltitudeBand band;
            public Vector3 targetPos;        // XZ主導（YはHighOrbitAIが最終決定）
            public Vector3 extraSteer;
            public FlightController.FlightProfile profile;
            public float profileHold;
        }

        // ---- Tunables ----
        public float meleeRange = 35f;
        public float shootingRange = 140f;

        public float orbitRadius = 55f;
        public float orbitAngularDegPerSec = 70f;

        public float resetMinDist = 45f;
        public float obstacleDensityToReset = 0.55f;

        public float coverLateralOffset = 60f;
        public int coverTrySteps = 3;
        public float coverMaxOffset = 140f;

        public float diveSetupDist = 220f;
        public float diveBoostHold = 0.35f;

        [Header("LoS / Segment sampling")]
        [Tooltip("線分判定のサンプル間隔（m）。小さいほど精密だが重い。")]
        public float segmentSampleStep = 8f;

        [Tooltip("線分判定：両端から何mは必ず無視する（自分/敵の半径ぶん誤検知しやすい）。")]
        public float segmentEndIgnore = 2.0f;

        public Result Decide(
            Vector3 selfPos,
            Vector3 selfForward,
            Transform enemy,
            VolumeDatabase db,
            float agentRadius,
            bool inKeepOut,
            ICombatStateProvider combatState,
            float nowTime)
        {
            var r = new Result
            {
                tactic = Tactic.CruisePatrol,
                band = AltitudeBand.Mid,
                targetPos = selfPos + selfForward * 40f,
                extraSteer = Vector3.zero,
                profile = FlightController.FlightProfile.Cruise,
                profileHold = 0.15f
            };

            // 0) KeepOut 脱出最優先
            if (inKeepOut)
            {
                r.tactic = Tactic.ClimbReset;
                r.band = AltitudeBand.High;
                r.targetPos = selfPos + selfForward * 60f;
                r.profile = FlightController.FlightProfile.Evade;
                r.profileHold = 0.30f;
                return r;
            }

            // 敵なし
            if (enemy == null)
            {
                r.tactic = Tactic.CruisePatrol;
                r.band = AltitudeBand.Mid;
                r.profile = FlightController.FlightProfile.Cruise;
                r.profileHold = 0.2f;
                return r;
            }

            Vector3 enemyPos = enemy.position;
            Vector3 toEnemy = enemyPos - selfPos;
            float dist = toEnemy.magnitude;
            Vector3 dir = (dist > 1e-3f) ? (toEnemy / dist) : selfForward;

            bool melee = combatState != null && combatState.IsMeleeEngaging;
            bool shoot = combatState != null && combatState.IsShooting;
            bool boost = combatState != null && combatState.IsBoosting;
            bool evade = combatState != null && combatState.IsEvading;

            // 1) 近接
            if (dist <= meleeRange)
            {
                r.tactic = Tactic.EngageMelee;
                r.band = AltitudeBand.Low;
                r.targetPos = enemyPos;
                r.profile = FlightController.FlightProfile.EngageMelee;
                r.profileHold = 0.18f;
                return r;
            }

            // 2) 地表付近が混んでる→上昇リセット（仕切り直し）
            float density = EstimateObstacleDensityXZ(selfPos, enemyPos, db, agentRadius);
            if (density >= obstacleDensityToReset && dist >= resetMinDist && !melee)
            {
                r.tactic = Tactic.ClimbReset;
                r.band = AltitudeBand.High;
                r.targetPos = enemyPos;
                r.profile = FlightController.FlightProfile.Evade;
                r.profileHold = 0.25f;
                return r;
            }

            // 3) 射撃圏：射線が通るなら周回射撃、通らないなら遮蔽接近
            bool inShootRange = dist <= shootingRange;
            bool hasLOS = HasLineOfSight(selfPos, enemyPos, db, agentRadius);

            if (inShootRange && (shoot || !melee))
            {
                if (hasLOS)
                {
                    r.tactic = Tactic.StrafeOrbit;
                    r.band = AltitudeBand.Mid;

                    Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
                    float ang = nowTime * orbitAngularDegPerSec * Mathf.Deg2Rad;
                    Vector3 forwardOnPlane = Vector3.Cross(dir, right).normalized;

                    Vector3 orbitOffset = (Mathf.Cos(ang) * right + Mathf.Sin(ang) * forwardOnPlane).normalized * orbitRadius;
                    r.targetPos = enemyPos + orbitOffset;

                    r.profile = FlightController.FlightProfile.Shooting;
                    r.profileHold = 0.20f;
                    return r;
                }
                else
                {
                    r.tactic = Tactic.CoverApproach;
                    r.band = AltitudeBand.Low;

                    Vector3 lateral = Vector3.Cross(Vector3.up, dir).normalized;
                    Vector3 best;

                    if (TryFindSideApproach(selfPos, enemyPos, lateral, db, agentRadius, out best) ||
                        TryFindSideApproach(selfPos, enemyPos, -lateral, db, agentRadius, out best))
                    {
                        r.targetPos = best;
                    }
                    else
                    {
                        r.band = AltitudeBand.Mid;
                        r.targetPos = enemyPos;
                    }

                    r.profile = FlightController.FlightProfile.Shooting;
                    r.profileHold = 0.20f;
                    return r;
                }
            }

            // 4) 遠距離：上空でセットアップ→急降下（演出的にも賢く見える）
            if (dist >= diveSetupDist && !melee)
            {
                r.tactic = Tactic.DiveAttack;
                r.band = AltitudeBand.High;
                r.targetPos = enemyPos;
                r.profile = FlightController.FlightProfile.Boost;
                r.profileHold = diveBoostHold;
                return r;
            }

            // 5) その他：接近
            r.tactic = Tactic.CoverApproach;
            r.band = hasLOS ? AltitudeBand.Mid : AltitudeBand.Low;
            r.targetPos = enemyPos;

            if (evade) { r.profile = FlightController.FlightProfile.Evade; r.profileHold = 0.20f; }
            else if (boost) { r.profile = FlightController.FlightProfile.Boost; r.profileHold = 0.22f; }
            else { r.profile = FlightController.FlightProfile.Cruise; r.profileHold = 0.15f; }

            return r;
        }

        bool HasLineOfSight(Vector3 a, Vector3 b, VolumeDatabase db, float agentRadius)
        {
            if (db == null) return true;
            // 旧APIに依存しない：線分をサンプルして Hard(Blocked/KeepOut) があればLOS無し
            return !SegmentHitsHardBySampling(a, b, db, agentRadius);
        }

        bool SegmentHitsHardBySampling(Vector3 a, Vector3 b, VolumeDatabase db, float agentRadius)
        {
            Vector3 ab = b - a;
            float len = ab.magnitude;
            if (len <= 0.001f) return false;

            float step = Mathf.Max(1.0f, segmentSampleStep);
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
                if ((flags & NavFlags.Blocked) != 0) return true;
                if ((flags & NavFlags.KeepOut) != 0) return true;
            }
            return false;
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

                // 自分→候補 / 候補→敵 の両方でHardに当たらないなら採用
                bool ok1 = !SegmentHitsHardBySampling(selfPos, candidate, db, agentRadius);
                bool ok2 = !SegmentHitsHardBySampling(candidate, enemyPos, db, agentRadius);

                if (ok1 && ok2)
                {
                    outPos = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
