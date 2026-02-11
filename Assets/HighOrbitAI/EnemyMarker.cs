using UnityEngine;

namespace HighOrbitAI
{
    public class EnemyMarker : MonoBehaviour
    {
        void OnEnable()  => EnemyRegistry.Register(transform);
        void OnDisable() => EnemyRegistry.Unregister(transform);
        void OnDestroy() => EnemyRegistry.Unregister(transform);
    }
}
