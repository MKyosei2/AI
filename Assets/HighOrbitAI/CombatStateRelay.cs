using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 最短で戦闘状態をAIに渡すためのリレー。
    /// あなたの武器/格闘スクリプトから、このコンポーネントのフラグを更新してください。
    /// </summary>
    public class CombatStateRelay : MonoBehaviour, ICombatStateProvider
    {
        [Header("Combat Flags (set these from your combat scripts)")]
        public bool meleeEngaging;
        public bool shooting;
        public bool boosting;
        public bool evading;

        public bool IsMeleeEngaging => meleeEngaging;
        public bool IsShooting => shooting;
        public bool IsBoosting => boosting;
        public bool IsEvading => evading;

        public void ClearAll()
        {
            meleeEngaging = false;
            shooting = false;
            boosting = false;
            evading = false;
        }
    }
}
