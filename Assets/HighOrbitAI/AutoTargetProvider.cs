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
        [Tooltip("同じ敵に味方が集まるほど減点")]
        public float wCrowdPenalty = 0.55f;

        [Tooltip("同じ“役割”が同じ敵に集まるほど強めに減点")]
        public float wSameRolePenalty = 0.35f;

        [Header("Role preference")]
        public float roleInterceptorDistanceMul = 1.15f; // 近距離寄り
        public float roleGunnerLosMul = 1.25f;          // LoS寄り
        public float roleSupportThreatMul = 1.15f;      // 高脅威狙い（分散でサポートが引き受ける等）

        [Header("Bias when under fire")]
        [Range(0f, 1f)] public float underFireBias = 0.6f;

        [Header("Switching")]
        public float switchHysteresis = 0.18f;
        public float minSwitchInterval = 0.75f;

        Collider[] buf;
        float nextSwitchTime;

        void Awake()
        {
            buf = new Collider[Mathf.Max(8, maxCandidates)];
        }

        public Transform SelectTarget(in TargetQuery q)
        {
            if (q.self == null) return null;

            float now = q.nowTime;
            bool canSwitch = now >= nextSwitchTime;

            float currentScore = float.NegativeInfinity;
            if (q.currentTarget != null)
                currentScore = Score(q, q.currentTarget);

            int n = Physics.OverlapSphereNonAlloc(q.self.position, searchRadius, buf, enemyMask, QueryTriggerInteraction.Ignore);
            if (n <= 0) return q.currentTarget;

            Transform best = q.currentTarget;
            float bestScore = currentScore;

            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
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

            float dist01 = Mathf.Clamp01(1f - (dist / searchRadius));
            float distScore = dist01 * wDistance;

            // 危険時は“近さ”への固執を落とす
            float dangerSelf = Mathf.Clamp01(Mathf.Max(q.underFire01, q.targeted01));
            distScore *= Mathf.Lerp(1f, 1f - underFireBias, dangerSelf);

            // LoS
            float los01 = 1f;
            if (q.db != null)
                los01 = q.db.SegmentHitsHardAny(selfPos, enemyPos) ? 0f : 1f;
            float losScore = los01 * wLos;

            // 背後
            float behindScore = 0f;
            {
                Vector3 enemyFwd = enemy.forward;
                Vector3 enemyToSelf = (selfPos - enemyPos).normalized;
                float behind01 = Mathf.Clamp01(Vector3.Dot(enemyFwd, enemyToSelf)); // 1=背後
                behindScore = behind01 * wBehind;
            }

            // KeepOut/Blocked ペナルティ
            float penalty = 0f;
            if (q.db != null)
            {
                q.db.EvaluatePoint(enemyPos, q.agentRadius, out var flags, out _);
                if ((flags & NavFlags.KeepOut) != 0) penalty += 1.0f;
                if ((flags & NavFlags.Blocked) != 0) penalty += 1.0f;
            }
            penalty *= wKeepOutPenalty;

            // ---- Enemy Threat（武器/HP/ロックオン）----
            float threat = 0f;
            var ti = enemy.GetComponent<IThreatInfoProvider>();
            if (ti != null)
            {
                threat =
                    ti.WeaponThreat01 * wThreatWeapon +
                    ti.Hp01 * wThreatHp +
                    ti.LockOnThreat01 * wThreatLockOn;
            }

            // ---- Squad dispersion（群がり抑制）----
            int crowd = SquadCoordinator.CountAssigned(q.squadId, enemy);
            int sameRole = SquadCoordinator.CountAssignedByRole(q.squadId, enemy, q.squadRole);

            float crowdPenalty = crowd * wCrowdPenalty + sameRole * wSameRolePenalty;

            // ---- Role preference ----
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

            // 危険時ほど“脅威”と“射線”を重視（Support/Gunnerが働きやすい）
            float dangerMul = Mathf.Lerp(1f, 1.25f, dangerSelf);

            float baseScore =
                (distScore * roleMulDist) +
                ((losScore * roleMulLos) + behindScore) * dangerMul +
                (threat * roleMulThreat) * dangerMul;

            return baseScore - penalty - crowdPenalty;
        }

        void OnValidate()
        {
            maxCandidates = Mathf.Clamp(maxCandidates, 8, 256);
            if (buf == null || buf.Length != maxCandidates)
                buf = new Collider[maxCandidates];
        }
    }
}
