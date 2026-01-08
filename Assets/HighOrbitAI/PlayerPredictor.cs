using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// プレイヤー運動を軽量に推定して未来位置を作る。
    /// “学習”の最小コスパ版：ゴール生成を賢くする。
    /// </summary>
    public class PlayerPredictor
    {
        Vector3 lastPos;
        Vector3 vel;
        Vector3 acc;
        Vector3 lastVel;
        bool inited;

        public void Reset(Vector3 pos)
        {
            lastPos = pos;
            vel = Vector3.zero;
            acc = Vector3.zero;
            lastVel = Vector3.zero;
            inited = true;
        }

        public void Tick(Vector3 currentPos, float dt, float velLerp = 0.25f, float accLerp = 0.25f)
        {
            if (!inited) Reset(currentPos);
            dt = Mathf.Max(1e-6f, dt);

            var v = (currentPos - lastPos) / dt;
            vel = Vector3.Lerp(vel, v, velLerp);
            acc = Vector3.Lerp(acc, (vel - lastVel) / dt, accLerp);

            lastVel = vel;
            lastPos = currentPos;
        }

        public Vector3 Predict(float T)
        {
            return lastPos + vel * T + 0.5f * acc * (T * T);
        }
    }
}
