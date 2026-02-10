using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 自動ターゲット選択：
    /// - 距離（近いほど高得点）
    /// - LoS（射線が通ると加点）
    /// - 背後（背後を取れるなら加点）
    /// - 上空逃げモード（UnderFire/Targetedが強い時は “遠め/上空へ逃げやすい” を優先）
    /// - KeepOut/Blocked に近い候補は減点
    /// </summary>
    public class AutoTargetProvider : MonoBehaviour, ITargetProvider
    {
        [Header("Search")]
        public float searchRadius = 800f;
        public LayerMask enemyMask = ~0;
        [Tooltip("空ならタグ無視。設定したら一致するTagだけ候補にする。")]
        public string requiredTag = "";

        [Tooltip("候補数（NonAllocバッファ）")]
        public int maxCandidates = 48;

        [Header("Weights")]
        public float wDistance = 1.0f;
        public float wLos = 0.8f;
        public float wBehind = 0.6f;
        public float wKeepOutPenalty = 1.2f;

        [Header("Bias when under fire")]
        [Tooltip("被弾中は近距離固執を弱める（上へ逃げやすいターゲット選択に寄せる）")]
        [Range(0f, 1f)] public float underFireBias = 0.6f;

        [Header("Switching")]
        [Tooltip("このスコア差が無いとターゲットを切り替えない（フラつき防止）")]
        public float switchHysteresis = 0.18f;

        [Tooltip("最短での切替間隔（秒）")]
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

            // 既存ターゲットのスコアを先に計算（ヒステリシス用）
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

            // 切替判定（ヒステリシス + インターバル）
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

            // 距離スコア：近いほど高得点（0..1-ish）
            float dist01 = Mathf.Clamp01(1f - (dist / searchRadius));
            float distScore = dist01 * wDistance;

            // 被弾/狙われ中は「近距離固執」を弱める
            float danger = Mathf.Clamp01(Mathf.Max(q.underFire01, q.targeted01));
            distScore *= Mathf.Lerp(1f, 1f - underFireBias, danger);

            // LoS
            float losScore = 0f;
            if (q.db != null)
            {
                bool blocked = q.db.SegmentHitsHardAny(selfPos, enemyPos);
                losScore = blocked ? 0f : 1f;
            }
            else
            {
                losScore = 1f;
            }
            losScore *= wLos;

            // 背後（敵の forward と「敵→自分」の向きで判定）
            float behindScore = 0f;
            {
                Vector3 enemyFwd = enemy.forward;
                Vector3 enemyToSelf = (selfPos - enemyPos).normalized;
                // 敵が向いてる方向と逆向き（=背後側）ほど大きい
                float behind01 = Mathf.Clamp01(Vector3.Dot(enemyFwd, enemyToSelf)); // 1=完全に背後
                behindScore = behind01 * wBehind;
            }

            // KeepOut/Blocked 近傍ペナルティ（候補点が危険なら減点）
            float penalty = 0f;
            if (q.db != null)
            {
                q.db.EvaluatePoint(enemyPos, q.agentRadius, out var flags, out _);
                if ((flags & NavFlags.KeepOut) != 0) penalty += 1.0f;
                if ((flags & NavFlags.Blocked) != 0) penalty += 1.0f;
            }
            penalty *= wKeepOutPenalty;

            // 危険時は「LoS/背後」をより評価（上空逃げしつつ“通る相手/背後取れる相手”）
            float dangerMul = Mathf.Lerp(1f, 1.25f, danger);

            return (distScore + (losScore + behindScore) * dangerMul) - penalty;
        }

        void OnValidate()
        {
            maxCandidates = Mathf.Clamp(maxCandidates, 8, 256);
            if (buf == null || buf.Length != maxCandidates)
                buf = new Collider[maxCandidates];
        }
    }
}
