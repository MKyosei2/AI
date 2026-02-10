using UnityEngine;

namespace HighOrbitAI
{
    public class ThreatSignalRelay : MonoBehaviour, IThreatSignalProvider
    {
        [Range(0f, 1f)] public float underFire01 = 0f;
        [Range(0f, 1f)] public float targeted01 = 0f;

        public float UnderFire01 => underFire01;
        public float Targeted01 => targeted01;
    }
}
