using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 非戦闘時の巡回ルート定義（任意）。
    /// - points にTransformを入れる
    /// - あるいは、このコンポーネントを付けたGameObjectの子Transformを Reset() で自動収集
    /// </summary>
    public class RoutePath : MonoBehaviour
    {
        public List<Transform> points = new List<Transform>();

        void Reset()
        {
            points.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                var c = transform.GetChild(i);
                if (c != null) points.Add(c);
            }
        }
    }
}
