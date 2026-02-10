using UnityEngine;

namespace HighOrbitAI
{
    public class ThreatInfoRelay : MonoBehaviour, IThreatInfoProvider
    {
        [Range(0f, 1f)] public float hp01 = 1f;
        [Range(0f, 1f)] public float weaponThreat01 = 0.5f;
        [Range(0f, 1f)] public float lockOnThreat01 = 0f;

        public float Hp01 => hp01;
        public float WeaponThreat01 => weaponThreat01;
        public float LockOnThreat01 => lockOnThreat01;
    }
}
