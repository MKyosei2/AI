using UnityEngine;

namespace HighOrbitAI
{
    public static class GroundSampler
    {
        public static bool TryGetGroundY(Vector3 position, float castHeight, float maxDistance, LayerMask groundMask, out float groundY)
        {
            Vector3 origin = position + Vector3.up * castHeight;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, castHeight + maxDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y;
                return true;
            }
            groundY = position.y;
            return false;
        }
    }
}
