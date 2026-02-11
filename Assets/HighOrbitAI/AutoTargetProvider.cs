using UnityEngine;

namespace HighOrbitAI
{
    public class AutoTargetProvider : MonoBehaviour, ITargetProvider
    {
        [Header("Search")]
        public float searchRadius = 800f;
        public LayerMask enemyMask = ~0;
        public string requiredTag = "";
        public int maxCandidates = 48;

        [Header("Base Weights")]
        public float wDistance = 1.0f;
        public float wLos = 0.8f;
        public float wBehind = 0.6f;
        public float wKeepOutPenalty = 1.2f;

        [Header("Threat Weights (Enemy)")]
        public float wThreatWeapon = 0.8f;
        public float wThreatHp = 0.35f;
        public float wThreatLockOn = 1.0f;

        [Header("Squad dispersion")]
        public float wCrowdPenalty = 0.55f;
        public float wSameRolePenalty = 0.35f;

        [Header("Role preference (A: role-based target split)")]
        public float wInterceptorIsolation = 0.9f;

        public float wGunnerPreferFar = 0.9f;
        public float gunnerMinPreferredDistance = 70f;
        public float wGunnerTooClosePenalty = 1.1f;

        public float wSupportPreferLockOn = 1.1f;
        public float wSupportPreferWeapon = 0.5f;

        public float isolationCheckRadius = 140f;
        public int isolationMaxNeighbors = 5;

        [Header("Role synergy (A++)")]
        [Tooltip("InterceptorがGunnerの主ターゲットを避ける強さ")]
        public float wInterceptorAvoidGunnerTarget = 0.9f;

        [Tooltip("GunnerがInterceptorの主ターゲットを避ける強さ")]
        public float wGunnerAvoidInterceptorTarget = 0.8f;

        [Tooltip("SupportがGunnerの主ターゲットを支援する（同じ敵を狙いやすい）強さ")]
        public float wSupportPreferGunnerTarget = 0.9f;

        [Tooltip("SupportがInterceptorの主ターゲットに3人目で群がるのを抑える軽い減点")]
        public float wSupportAvoidInterceptorPile = 0.25f;

        [Header("Role preference (existing multipliers)")]
        public float roleInterceptorDistanceMul = 1.15f;
        public float roleGunnerLosMul = 1.25f;
        public float roleSupportThreatMul = 1.15f;

        [Header("Bias when under fire")]
        [Range(0f, 1f)] public float underFireBias = 0.6f;

        [Header("Switching")]
        public float switchHysteresis = 0.18f;
        public float minSwitchInterval = 0.75f;

        Collider[] candidateBuf;
        Collider[] neighborBuf;
        float nextSwitchTime;

        void Awake()
        {
            candidateBuf = new Collider[Mathf.Max(8, maxCandidates)];
            neighborBuf = new Collider[Mathf.Clamp(maxCandidates, 16, 256)];
        }

        public Transform SelectTarget(in TargetQuery q)
        {
            if (q.self == null) return null;

            float now = q.nowTime;
            bool canSwitch = now >= nextSwitchTime;

            float currentScore = float.NegativeInfinity;
            if (q.currentTarget != null)
                currentScore = Score(q, q.currentTarget);

            int n = Physics.OverlapSphereNonAlloc(q.self.position, searchRadius, candidateBuf, enemyMask, QueryTriggerInteraction.Ignore);
            if (n <= 0) return q.currentTarget;

            Transform best = q.currentTarget;
            float bestScore = currentScore;

            for (int i = 0; i < n; i++)
            {
                var c = candidateBuf[i];
                if (c == null) continue;

                Transform t = c.transform;
                if (t == null || t == q.self) continue;

                if (!string.IsNullOrEmpty(requiredTag) && !t.CompareTag(requiredTag))
                    continue;

                float s = Score(q, t);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = t;
                }
            }

            if (best != q.currentTarget && canSwitch)
            {
                if (bestScore >= currentScore + switchHysteresis)
                {
                    nextSwitchTime = now + Mathf.Max(0.05f, minSwitchInterval);
                    return best;
                }
            }

            return q.currentTarget != null ? q.currentTarget : best;
        }

        float Score(in TargetQuery q, Transform enemy)
        {
            Vector3 selfPos = q.self.position;
            Vector3 enemyPos = enemy.position;

            Vector3 d = enemyPos - selfPos;
            float dist = d.magnitude;
            if (dist < 0.001f) dist = 0.001f;

            float dist01Near = Mathf.Clamp01(1f - (dist / searchRadius));
            float distScore = dist01Near * wDistance;

            float danger = Mathf.Clamp01(Mathf.Max(q.underFire01, q.targeted01));
            distScore *= Mathf.Lerp(1f, 1f - underFireBias, danger);

            float los01 = 1f;
            if (q.db != null)
                los01 = q.db.SegmentHitsHardAny(selfPos, enemyPos) ? 0f : 1f;
            float losScore = los01 * wLos;

            float behindScore = 0f;
            {
                Vector3 enemyFwd = enemy.forward;
                Vector3 enemyToSelf = (selfPos - enemyPos).normalized;
                float behind01 = Mathf.Clamp01(Vector3.Dot(enemyFwd, enemyToSelf));
                behindScore = behind01 * wBehind;
            }

            float penalty = 0f;
            if (q.db != null)
            {
                q.db.EvaluatePoint(enemyPos, q.agentRadius, out var flags, out _);
                if ((flags & NavFlags.KeepOut) != 0) penalty += 1.0f;
                if ((flags & NavFlags.Blocked) != 0) penalty += 1.0f;
            }
            penalty *= wKeepOutPenalty;

            float threat = 0f;
            float weaponT = 0f, hpT = 1f, lockT = 0f;
            var ti = enemy.GetComponent<IThreatInfoProvider>();
            if (ti != null)
            {
                weaponT = Mathf.Clamp01(ti.WeaponThreat01);
                hpT = Mathf.Clamp01(ti.Hp01);
                lockT = Mathf.Clamp01(ti.LockOnThreat01);

                threat =
                    weaponT * wThreatWeapon +
                    hpT * wThreatHp +
                    lockT * wThreatLockOn;
            }

            int crowd = SquadCoordinator.CountAssigned(q.squadId, enemy);
            int sameRole = SquadCoordinator.CountAssignedByRole(q.squadId, enemy, q.squadRole);
            float crowdPenalty = crowd * wCrowdPenalty + sameRole * wSameRolePenalty;

            float roleMulDist = 1f;
            float roleMulLos = 1f;
            float roleMulThreat = 1f;

            switch (q.squadRole)
            {
                case SquadRole.Interceptor:
                    roleMulDist = roleInterceptorDistanceMul;
                    break;
                case SquadRole.Gunner:
                    roleMulLos = roleGunnerLosMul;
                    break;
                case SquadRole.Support:
                    roleMulThreat = roleSupportThreatMul;
                    break;
            }

            float dangerMul = Mathf.Lerp(1f, 1.25f, danger);

            float baseScore =
                (distScore * roleMulDist) +
                ((losScore * roleMulLos) + behindScore) * dangerMul +
                (threat * roleMulThreat) * dangerMul;

            // =========================
            // A: Role-based target split
            // =========================
            float roleBonus = 0f;
            float roleExtraPenalty = 0f;

            if (q.squadRole == SquadRole.Gunner)
            {
                float dist01Far = Mathf.Clamp01(dist / searchRadius);
                roleBonus += dist01Far * wGunnerPreferFar;

                if (dist < gunnerMinPreferredDistance)
                {
                    float close01 = Mathf.Clamp01(1f - (dist / Mathf.Max(1f, gunnerMinPreferredDistance)));
                    roleExtraPenalty += close01 * wGunnerTooClosePenalty;
                }

                if (los01 <= 0.01f) roleExtraPenalty += 0.35f;
            }
            else if (q.squadRole == SquadRole.Interceptor)
            {
                int neighbors = CountEnemyNeighbors(enemy, enemyPos);
                neighbors = Mathf.Clamp(neighbors, 0, Mathf.Max(0, isolationMaxNeighbors));
                float isolation01 = 1f - (neighbors / (float)Mathf.Max(1, isolationMaxNeighbors));
                roleBonus += isolation01 * wInterceptorIsolation;
            }
            else if (q.squadRole == SquadRole.Support)
            {
                roleBonus += lockT * wSupportPreferLockOn;
                roleBonus += weaponT * wSupportPreferWeapon;

                if (dist < 55f) roleExtraPenalty += 0.25f;
            }

            // =========================
            // A++: Role synergy (team combos)
            // =========================
            Transform gunnerPrimary = null;
            Transform interceptorPrimary = null;

            SquadCoordinator.TryGetPrimaryTargetForRole(q.squadId, SquadRole.Gunner, out gunnerPrimary);
            SquadCoordinator.TryGetPrimaryTargetForRole(q.squadId, SquadRole.Interceptor, out interceptorPrimary);

            if (q.squadRole == SquadRole.Interceptor)
            {
                // Gunnerの射線確保：同じ敵に近接が貼り付くのを抑える
                if (gunnerPrimary != null && gunnerPrimary == enemy)
                    roleExtraPenalty += wInterceptorAvoidGunnerTarget;
            }
            else if (q.squadRole == SquadRole.Gunner)
            {
                // 近接が貼り付いてる敵は避ける（巻き込まれ/射線事故）
                if (interceptorPrimary != null && interceptorPrimary == enemy)
                    roleExtraPenalty += wGunnerAvoidInterceptorTarget;
            }
            else if (q.squadRole == SquadRole.Support)
            {
                // Gunnerを支援：同じ敵を見やすくする（ただし crowdPenalty は残る）
                if (gunnerPrimary != null && gunnerPrimary == enemy)
                    roleBonus += wSupportPreferGunnerTarget;

                // InterceptorにもSupportが乗って3人目になりやすいのを軽く抑える
                if (interceptorPrimary != null && interceptorPrimary == enemy)
                    roleExtraPenalty += wSupportAvoidInterceptorPile;
            }

            return (baseScore + roleBonus) - penalty - crowdPenalty - roleExtraPenalty;
        }

        int CountEnemyNeighbors(Transform enemy, Vector3 enemyPos)
        {
            int n = Physics.OverlapSphereNonAlloc(enemyPos, isolationCheckRadius, neighborBuf, enemyMask, QueryTriggerInteraction.Ignore);
            if (n <= 0) return 0;

            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var c = neighborBuf[i];
                if (c == null) continue;

                Transform t = c.transform;
                if (t == null) continue;
                if (t == enemy) continue;

                if (!string.IsNullOrEmpty(requiredTag) && !t.CompareTag(requiredTag))
                    continue;

                count++;
                if (count >= isolationMaxNeighbors) break;
            }
            return count;
        }

        void OnValidate()
        {
            maxCandidates = Mathf.Clamp(maxCandidates, 8, 256);
            isolationMaxNeighbors = Mathf.Clamp(isolationMaxNeighbors, 1, 32);

            if (candidateBuf == null || candidateBuf.Length != maxCandidates)
                candidateBuf = new Collider[maxCandidates];

            int nb = Mathf.Clamp(maxCandidates, 16, 256);
            if (neighborBuf == null || neighborBuf.Length != nb)
                neighborBuf = new Collider[nb];
        }
    }
}
