using UnityEngine;

namespace HighOrbitAI
{
    public interface ICombatStateProvider
    {
        bool IsMeleeEngaging { get; }
        bool IsShooting { get; }
        bool IsBoosting { get; }
        bool IsEvading { get; }
    }
}
