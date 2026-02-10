using UnityEngine;

namespace HighOrbitAI
{
    public struct TargetQuery
    {
        public Transform self;
        public Vector3 selfForward;

        public VolumeDatabase db;
        public float agentRadius;

        public float underFire01;
        public float targeted01;

        public Transform currentTarget;
        public float nowTime;
    }

    public interface ITargetProvider
    {
        Transform SelectTarget(in TargetQuery q);
    }
}
