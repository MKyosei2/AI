using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 2階層AI：
    /// - Cruise（超高軌道）: 疎グラフ(AirLaneGraph) A*
    /// - Terminal（近距離）: 局所3DグリッドA*
    /// タグ/禁止/障害物は VolumeDatabase（AABB+flags+cost）で評価する。
    /// </summary>
    public class HighOrbitAI : MonoBehaviour
    {
        [Header("References")]
        public Transform player;
        public VolumeCollector volumeCollector; // NavWorldなどに付けたものを指定
        public FlightController controller;

        [Header("Cruise (High Orbit)")]
        public float Hcruise = 200f;
        public float cruiseBand = 20f;
        public float cruiseGoalPredictT = 0.8f;

        [Header("Mode Switch")]
        public float descendRange = 80f;  // 近づいたらTerminalへ
        public float ascendRange = 140f;  // 離れたらCruiseへ（ヒステリシス）

        [Header("Decision Loop")]
        [Tooltip("計画・更新の頻度（Hz）。毎フレーム再計画しないための要】")]
        public float decisionHz = 15f;

        [Header("Local Planner")]
        public float localGoalPredictT = 0.4f;
        public float localRadius = 60f;
        public float localCellSize = 5f;

        [Header("Agent")]
        public float agentRadius = 0.5f;

        enum Mode { Cruise, Terminal }
        Mode mode = Mode.Cruise;

        AirLaneGraph cruiseGraph;
        AStarAirLane cruiseAstar;
        LocalGridPlanner localPlanner;

        readonly List<Vector3> cruisePath = new List<Vector3>(256);
        readonly List<Vector3> localPath = new List<Vector3>(256);

        readonly PlayerPredictor predictor = new PlayerPredictor();

        float decisionTimer;

        void Reset()
        {
            controller = GetComponent<FlightController>();
        }

        void Start()
        {
            if (controller == null) controller = GetComponent<FlightController>();
            cruiseAstar = new AStarAirLane();

            localPlanner = new LocalGridPlanner { localRadius = localRadius, cellSize = localCellSize };
            if (player != null) predictor.Reset(player.position);

            BuildCruiseGraphIfNeeded();
        }

        void Update()
        {
            if (player == null || volumeCollector == null || volumeCollector.database == null || controller == null)
                return;

            // 1) 毎フレーム：プレイヤー予測更新
            predictor.Tick(player.position, Time.deltaTime);

            // 2) 低レート：判断 + 再計画
            decisionTimer += Time.deltaTime;
            if (decisionTimer >= 1f / Mathf.Max(1f, decisionHz))
            {
                decisionTimer = 0f;

                // 動的ボリューム更新（最小実装：全再構築）
                volumeCollector.UpdateDynamicVolumes();

                // モード切替（XZ距離で判定）
                float flatDist = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(player.position.x, player.position.z)
                );

                if (mode == Mode.Cruise && flatDist < descendRange) mode = Mode.Terminal;
                else if (mode == Mode.Terminal && flatDist > ascendRange) mode = Mode.Cruise;

                if (mode == Mode.Cruise)
                    PlanCruise();
                else
                    PlanLocal();
            }

            // 3) 毎フレーム：経路追従（制約付き）
            controller.Tick(Time.deltaTime);

            // 4) KeepOut最後の砦（侵入してしまったら押し戻す）
            EnforceKeepOut();
        }

        void BuildCruiseGraphIfNeeded()
        {
            if (volumeCollector == null || volumeCollector.database == null) return;

            // ワールド範囲は VolumeCollector のRenderer/Colliderから推定したいが、
            // 最小実装として「シーン全体のCollider bounds」を集約して使う。
            Bounds world = EstimateWorldBounds();
            var builder = new AirLaneGraphBuilder
            {
                nodeSpacing = 20f,
                edgeMaxDistance = 80f,
                softCostSamples = 3
            };

            cruiseGraph = builder.Build(world, Hcruise, cruiseBand, volumeCollector.database, agentRadius);
        }

        Bounds EstimateWorldBounds()
        {
            // シーンのColliderをざっくり包む。必要なら手動指定に変更してOK。
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            bool init = false;
            Bounds b = new Bounds(transform.position, Vector3.one * 100f);
            foreach (var c in cols)
            {
                if (!c.enabled) continue;
                if (!init) { b = c.bounds; init = true; }
                else b.Encapsulate(c.bounds);
            }
            // 余白
            b.Expand(new Vector3(100, 200, 100));
            return b;
        }

        void PlanCruise()
        {
            if (cruiseGraph == null || cruiseGraph.nodes.Count == 0)
                BuildCruiseGraphIfNeeded();

            Vector3 pf = predictor.Predict(cruiseGoalPredictT);
            Vector3 goal = new Vector3(pf.x, Hcruise, pf.z);

            int start = FindNearestCruiseNode(transform.position);
            int end = FindNearestCruiseNode(goal);
            if (start < 0 || end < 0) return;

            if (cruiseAstar.FindPath(cruiseGraph, start, end, cruisePath))
            {
                // 先頭に現在位置、末尾にgoalを入れるとより滑らか
                var path = new List<Vector3>(cruisePath.Count + 2);
                path.Add(transform.position);
                path.AddRange(cruisePath);
                path.Add(goal);
                controller.SetPath(path);
            }
        }

        void PlanLocal()
        {
            localPlanner.localRadius = localRadius;
            localPlanner.cellSize = localCellSize;

            Vector3 pf = predictor.Predict(localGoalPredictT);
            Vector3 goal = pf;

            // Terminalでは高度をプレイヤー近傍へ寄せたいならここで調整
            // goal.y = Mathf.Lerp(transform.position.y, player.position.y, 0.7f);

            bool ok = localPlanner.FindPath(
                transform.position, goal,
                volumeCollector.database,
                agentRadius,
                controller.maxClimbRate,
                controller.maxSpeed,
                localPath
            );

            if (ok)
            {
                var path = new List<Vector3>(localPath.Count + 1);
                path.Add(transform.position);
                path.AddRange(localPath);
                controller.SetPath(path);
            }
            else
            {
                // 局所探索が失敗したら、直進追従だけ（回避は制御/KeepOutで守る）
                controller.SetPath(new List<Vector3> { transform.position, goal });
            }
        }

        int FindNearestCruiseNode(Vector3 p)
        {
            if (cruiseGraph == null || cruiseGraph.nodes.Count == 0) return -1;

            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < cruiseGraph.nodes.Count; i++)
            {
                float d = (cruiseGraph.nodes[i].pos - p).sqrMagnitude;
                if (d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        void EnforceKeepOut()
        {
            // 現在位置がKeepOut内なら押し戻す
            var db = volumeCollector.database;
            db.EvaluatePoint(transform.position, agentRadius, out var flags, out _);
            if ((flags & NavFlags.KeepOut) == 0) return;

            // どのKeepOutか特定して押し戻すのが理想だが、最小実装として
            // 少し上に逃がす（安全策）＋ランダムな横方向
            Vector3 push = Vector3.up + (transform.right * 0.5f) + (transform.forward * 0.2f);
            controller.ApplyKeepOutPush(push, Time.deltaTime);
        }
    }
}
