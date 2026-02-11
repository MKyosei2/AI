using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class HighOrbitAI : MonoBehaviour
    {
        [Header("Refs")]
        public VolumeCollector volumeCollector;
        public FlightController controller;

        public enum RouteMode { Waypoints, RandomRoam }

        [Header("Route Source")]
        public RouteMode routeMode = RouteMode.Waypoints;
        public Transform[] waypoints;
        public Transform waypointRoot;

        public bool loopWaypoints = true;
        public bool shuffleWaypoints = false;

        public float waypointReachDistXZ = 12f;
        public float minSecondsBetweenGoalSwitch = 0.75f;

        [Header("Random Roam")]
        public Bounds roamBounds = new Bounds(Vector3.zero, new Vector3(1400, 200, 1400));
        public float roamMinSegment = 220f;
        public float roamMaxSegment = 520f;

        [Header("Decision (think rate)")]
        public float decisionHz = 6f;

        [Header("Planning (heavy)")]
        public float planHz = 2f;
        public float replanGoalDeltaXZ = 14f;

        [Header("Altitude Policy (Terminal)")]
        public float lowHeight = 10f;
        public float ceilingAboveGround = 35f;
        public float minAboveGround = 3f;

        public LayerMask groundMask = ~0;
        public float groundCastHeight = 1200f;
        public float groundMaxDistance = 2500f;

        [Header("3D Cruise Graph (Sparse)")]
        public float cruiseNodeSpacingXZ = 45f;
        public float cruiseLayerSpacingY = 45f;
        public float cruiseMinY = 10f;
        public float cruiseMaxY = 320f;
        public float cruiseEdgeMaxDistance = 110f;
        public int cruiseSoftSamples = 0;

        public float cruiseLowAltPenalty = 0.0008f;
        public float cruisePenaltyBaseY = 70f;

        [Header("Cruise Build Budget")]
        [Tooltip("Cruiseグラフ生成の1フレーム予算(ms)。小さいほど滑らか。")]
        public float cruiseBuildBudgetMs = 2.0f;

        [Header("Cruise Altitude (3D movement)")]
        [Tooltip("巡航高度の変化スピード(大きいほど上下が素早い)")]
        public float cruiseAltitudeResponsiveness = 0.6f;

        [Tooltip("巡航高度を時々変更する周期(秒)。小さいほど上下しやすい")]
        public float cruiseAltitudeChangePeriod = 5.0f;

        [Tooltip("巡航高度のランダム幅(0..1)。0=固定、1=最大で上下")]
        [Range(0f, 1f)]
        public float cruiseAltitudeVariety = 0.75f;

        [Header("Terminal Local 3D Grid")]
        public float terminalRange = 120f;
        public float localCellSize = 10f;
        public float localRadius = 95f;

        [Header("Variety")]
        public float tieEpsilon = 0.02f;
        public float varietyPeriod = 2.0f;

        // -----------------------
        // DebugView互換
        // -----------------------
        public enum AIMode { Lane, Terminal }
        AIMode mode = AIMode.Lane;
        public AIMode CurrentMode => mode;

        public Vector3 DebugTarget { get; private set; }
        public Vector3 DebugGoal { get; private set; }
        public bool DebugLastPlanOk { get; private set; }
        public string DebugLastPlanMessage { get; private set; }
        public float DebugFlatDistance { get; private set; }
        public float DebugDesiredY { get; private set; }
        public float DebugCeilingY { get; private set; }
        public bool DebugInKeepOut { get; private set; }

        public bool DebugMelee { get; private set; }
        public bool DebugShooting { get; private set; }
        public bool DebugBoost { get; private set; }
        public bool DebugEvade { get; private set; }
        public string DebugPhase { get; private set; }
        public string DebugTactic { get; private set; }
        public TacticalDirector.AltitudeBand DebugBand { get; private set; }

        void SetDebugCombatDefaults()
        {
            DebugMelee = false;
            DebugShooting = false;
            DebugBoost = false;
            DebugEvade = false;

            DebugPhase = (mode == AIMode.Terminal) ? "Terminal" : "Cruise";
            DebugTactic = "-";
            DebugBand = TacticalDirector.AltitudeBand.Mid;
        }

        // -----------------------
        // internal
        // -----------------------
        int waypointIndex;
        float goalSwitchCooldown;

        Vector3 currentTarget;
        Vector3 currentGoal;
        Vector3 lastPlannedGoal;

        float decisionTimer;
        float planTimer;

        // planner
        AirLaneGraph3DBuilder cruiseBuilder;
        AStarAirLane cruiseAstar;
        LocalGridPlannerVariety localPlanner;

        // paths
        readonly List<Vector3> cruisePath = new List<Vector3>(512);
        readonly List<Vector3> localPath = new List<Vector3>(256);
        readonly List<Vector3> lastSentPath = new List<Vector3>(128);

        // dynamic tick global 1x per frame
        static int s_lastDynTickFrame = -1;
        static VolumeCollector s_lastDynTickCollector;

        // ★重いプランを「全AI同時」に走らせない（フリーズ対策）
        static int s_lastHeavyPlanFrame = -1;

        // ★Cruiseグラフは collectorごとに共有＆段階生成
        class CruiseSharedState
        {
            public int staticRev;
            public int paramHash;
            public Bounds bounds;
            public AirLaneGraph graph;
            public bool building;
        }

        static readonly Dictionary<int, CruiseSharedState> s_cruiseByCollector = new Dictionary<int, CruiseSharedState>(16);

        // ★巡航高度（立体移動用）
        float currentCruiseY;
        float targetCruiseY;
        float nextCruiseYChangeTime;

        int ComputeCruiseParamHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + cruiseNodeSpacingXZ.GetHashCode();
                h = h * 31 + cruiseLayerSpacingY.GetHashCode();
                h = h * 31 + cruiseMinY.GetHashCode();
                h = h * 31 + cruiseMaxY.GetHashCode();
                h = h * 31 + cruiseEdgeMaxDistance.GetHashCode();
                h = h * 31 + cruiseSoftSamples.GetHashCode();
                h = h * 31 + cruiseLowAltPenalty.GetHashCode();
                h = h * 31 + cruisePenaltyBaseY.GetHashCode();
                return h;
            }
        }

        void Reset()
        {
            controller = GetComponent<FlightController>();
        }

        void Start()
        {
            if (controller == null) controller = GetComponent<FlightController>();

            cruiseBuilder = new AirLaneGraph3DBuilder();
            cruiseAstar = new AStarAirLane();

            localPlanner = new LocalGridPlannerVariety
            {
                cellSize = localCellSize,
                localRadius = localRadius,
                maxNodes = 12000,
                tieEpsilon = tieEpsilon
            };

            BuildWaypointCache();
            EnsureInitialTarget();

            DebugLastPlanOk = false;
            DebugLastPlanMessage = "Init";
            SetDebugCombatDefaults();

            // 初期巡航高度
            currentCruiseY = Mathf.Clamp(transform.position.y, cruiseMinY, cruiseMaxY);
            targetCruiseY = currentCruiseY;
            nextCruiseYChangeTime = Time.time + 0.5f;
        }

        void Update()
        {
            if (volumeCollector == null || volumeCollector.database == null || controller == null)
                return;

            // 動的更新を1フレーム1回だけ
            if (s_lastDynTickFrame != Time.frameCount || s_lastDynTickCollector != volumeCollector)
            {
                s_lastDynTickFrame = Time.frameCount;
                s_lastDynTickCollector = volumeCollector;
                volumeCollector.TickDynamic(Time.deltaTime);
            }

            // Think
            decisionTimer += Time.deltaTime;
            if (decisionTimer >= 1f / Mathf.Max(1f, decisionHz))
            {
                decisionTimer = 0f;

                EnsureCruiseGraphShared();

                MaybeAdvanceTarget();

                // mode判定（XZ距離）
                float flatDist = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentTarget.x, currentTarget.z)
                );
                DebugFlatDistance = flatDist;
                mode = (flatDist <= terminalRange) ? AIMode.Terminal : AIMode.Lane;

                // ★ここが「立体移動」の要：ゴールYをモードで切り替える
                currentGoal = BuildGoalFromTarget(currentTarget, mode);

                DebugTarget = currentTarget;
                DebugGoal = currentGoal;

                volumeCollector.database.EvaluatePoint(transform.position, 0.2f, out var fHere, out _);
                DebugInKeepOut = (fHere & NavFlags.KeepOut) != 0;

                SetDebugCombatDefaults();
            }

            // Plan (heavy) - 全AI同時実行を抑制
            planTimer += Time.deltaTime;
            float interval = 1f / Mathf.Max(0.25f, planHz);

            bool goalMoved = Vector2.Distance(
                new Vector2(currentGoal.x, currentGoal.z),
                new Vector2(lastPlannedGoal.x, lastPlannedGoal.z)
            ) >= replanGoalDeltaXZ;

            bool timeToPlan = planTimer >= interval;

            if ((timeToPlan || goalMoved) && s_lastHeavyPlanFrame != Time.frameCount)
            {
                s_lastHeavyPlanFrame = Time.frameCount;
                planTimer = 0f;

                RunPlanner();
                lastPlannedGoal = currentGoal;

                SetDebugCombatDefaults();
            }

            controller.Tick(Time.deltaTime);
        }

        void RunPlanner()
        {
            var cruiseGraph = GetSharedCruiseGraph();

            // Cruise未準備(生成中)なら直線で止めない
            if (mode == AIMode.Lane)
            {
                if (cruiseGraph == null)
                {
                    SendPathIfChanged(new List<Vector3> { transform.position, currentGoal });
                    DebugLastPlanOk = false;
                    DebugLastPlanMessage = "Cruise building -> direct";
                    return;
                }

                PlanCruise(cruiseGraph, currentGoal);
                return;
            }

            PlanTerminal(currentGoal, fallbackCruiseGraph: cruiseGraph);
        }

        AirLaneGraph GetSharedCruiseGraph()
        {
            int cid = volumeCollector.GetInstanceID();
            if (!s_cruiseByCollector.TryGetValue(cid, out var st)) return null;
            return st.graph;
        }

        void EnsureCruiseGraphShared()
        {
            int cid = volumeCollector.GetInstanceID();
            int staticRev = volumeCollector.database.staticRevision;
            int ph = ComputeCruiseParamHash();

            if (!s_cruiseByCollector.TryGetValue(cid, out var st))
            {
                st = new CruiseSharedState();
                s_cruiseByCollector[cid] = st;
            }

            if (st.graph != null && st.staticRev == staticRev && st.paramHash == ph)
                return;

            if (st.building)
                return;

            st.building = true;
            st.staticRev = staticRev;
            st.paramHash = ph;
            st.bounds = volumeCollector.GetWorldBoundsForCruise();

            cruiseBuilder.nodeSpacingXZ = cruiseNodeSpacingXZ;
            cruiseBuilder.layerSpacingY = cruiseLayerSpacingY;
            cruiseBuilder.minY = cruiseMinY;
            cruiseBuilder.maxY = cruiseMaxY;
            cruiseBuilder.edgeMaxDistance = cruiseEdgeMaxDistance;
            cruiseBuilder.softCostSamples = cruiseSoftSamples;
            cruiseBuilder.lowAltitudePenalty = cruiseLowAltPenalty;
            cruiseBuilder.penaltyBaseY = cruisePenaltyBaseY;

            StartCoroutine(BuildCruiseCoroutine(st));
        }

        IEnumerator BuildCruiseCoroutine(CruiseSharedState st)
        {
            AirLaneGraph built = null;

            yield return cruiseBuilder.BuildIncremental(
                st.bounds,
                volumeCollector.database,
                0.7f,
                g => built = g,
                msBudget: Mathf.Clamp(cruiseBuildBudgetMs, 0.5f, 6f)
            );

            st.graph = built;
            st.building = false;
        }

        void PlanCruise(AirLaneGraph graph, Vector3 goal)
        {
            int s = FindNearestNode(graph, transform.position);
            int g = FindNearestNode(graph, goal);

            if (s < 0 || g < 0)
            {
                SendPathIfChanged(new List<Vector3> { transform.position, goal });
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Cruise nearest missing -> direct";
                return;
            }

            cruisePath.Clear();
            bool ok = cruiseAstar.FindPath(graph, s, g, cruisePath);

            DebugLastPlanOk = ok;
            DebugLastPlanMessage = ok ? "Cruise OK" : "Cruise FAIL";

            if (!ok)
            {
                SendPathIfChanged(new List<Vector3> { transform.position, goal });
                return;
            }

            var path = new List<Vector3>(cruisePath.Count + 2);
            path.Add(transform.position);
            path.AddRange(cruisePath);
            path.Add(goal);
            SendPathIfChanged(path);
        }

        void PlanTerminal(Vector3 goal, AirLaneGraph fallbackCruiseGraph)
        {
            float desiredY = goal.y;
            float ceilingY = goal.y;

            if (GroundSampler.TryGetGroundY(transform.position, groundCastHeight, groundMaxDistance, groundMask, out float groundY))
            {
                desiredY = Mathf.Clamp(goal.y, groundY + minAboveGround, groundY + ceilingAboveGround);
                ceilingY = groundY + ceilingAboveGround;
            }
            DebugDesiredY = desiredY;
            DebugCeilingY = ceilingY;

            localPlanner.cellSize = localCellSize;
            localPlanner.localRadius = localRadius;
            localPlanner.tieEpsilon = tieEpsilon;

            int seed = ComputeVarietySeed();
            float agentRadius = Mathf.Max(0.01f, volumeCollector.agentRadius);

            localPath.Clear();
            bool ok = localPlanner.FindPath(
                transform.position, goal,
                volumeCollector.database,
                agentRadius,
                controller.maxClimbRate,
                controller.maxSpeed,
                desiredY, ceilingY,
                seed,
                localPath
            );

            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(localPath.Count + 1);
                path.Add(transform.position);
                path.AddRange(localPath);
                SendPathIfChanged(path);
                DebugLastPlanMessage = "Terminal OK";
                return;
            }

            if (fallbackCruiseGraph != null)
            {
                PlanCruise(fallbackCruiseGraph, goal);
                DebugLastPlanMessage = "Terminal FAIL -> Cruise";
            }
            else
            {
                SendPathIfChanged(new List<Vector3> { transform.position, goal });
                DebugLastPlanMessage = "Terminal FAIL -> direct";
            }
        }

        static int FindNearestNode(AirLaneGraph graph, Vector3 p)
        {
            if (graph == null || graph.nodes == null || graph.nodes.Count == 0) return -1;

            int best = 0;
            float bestD = float.PositiveInfinity;

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Vector3 np = graph.nodes[i].pos;
                float dx = np.x - p.x;
                float dy = np.y - p.y;
                float dz = np.z - p.z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        int ComputeVarietySeed()
        {
            int t = Mathf.FloorToInt(Time.time / Mathf.Max(0.25f, varietyPeriod));
            int b = transform.GetInstanceID();
            unchecked { return b * 19349663 ^ t * 83492791; }
        }

        // ★Lane(Cruise)用：巡航高度を自動選択（WaypointsのYが0でも立体移動になる）
        float ComputeCruiseAltitude(Vector3 targetXZ)
        {
            // たまにターゲット巡航高度を更新（上下の動きを作る）
            if (Time.time >= nextCruiseYChangeTime)
            {
                nextCruiseYChangeTime = Time.time + Mathf.Max(0.8f, cruiseAltitudeChangePeriod);

                // 安定した乱数（個体差＋時間）
                int id = GetInstanceID();
                float t = Mathf.Floor(Time.time / Mathf.Max(0.5f, cruiseAltitudeChangePeriod));
                float n = Mathf.Abs(Mathf.Sin((id * 0.00017f + 1.23f) * 10000f + t * 2.11f));
                n = Mathf.Lerp(0.5f, n, cruiseAltitudeVariety);

                float y = Mathf.Lerp(cruiseMinY, cruiseMaxY, n);

                // 地面より下に行かない
                if (GroundSampler.TryGetGroundY(new Vector3(targetXZ.x, transform.position.y, targetXZ.z),
                        groundCastHeight, groundMaxDistance, groundMask, out float gy))
                {
                    y = Mathf.Max(y, gy + minAboveGround + 2f);
                }

                targetCruiseY = Mathf.Clamp(y, cruiseMinY, cruiseMaxY);
            }

            // 追従を滑らかに
            currentCruiseY = Mathf.Lerp(currentCruiseY, targetCruiseY,
                Mathf.Clamp01(cruiseAltitudeResponsiveness * Time.deltaTime * 10f));

            return currentCruiseY;
        }

        // ★モードに応じてゴールYを決める
        Vector3 BuildGoalFromTarget(Vector3 target, AIMode m)
        {
            // XZはターゲットに合わせる
            float x = target.x;
            float z = target.z;

            if (m == AIMode.Lane)
            {
                // WaypointのYが0でも巡航高度で立体移動
                float y = ComputeCruiseAltitude(new Vector3(x, 0f, z));
                DebugDesiredY = y;
                DebugCeilingY = y;
                return new Vector3(x, y, z);
            }

            // Terminal: 地面＋天井内に収める
            float yT = target.y;

            if (GroundSampler.TryGetGroundY(transform.position, groundCastHeight, groundMaxDistance, groundMask, out float groundY))
            {
                float minY = groundY + minAboveGround;
                float maxY = groundY + ceilingAboveGround;

                // waypointがy=0なら低高度固定になりがちなので、少しだけ余裕を持たせる
                float desired = (Mathf.Abs(yT) < 0.01f) ? (groundY + lowHeight) : yT;
                yT = Mathf.Clamp(desired, minY, maxY);

                DebugDesiredY = yT;
                DebugCeilingY = maxY;
            }
            else
            {
                yT = Mathf.Clamp(yT, cruiseMinY, cruiseMaxY);
                DebugDesiredY = yT;
                DebugCeilingY = yT;
            }

            return new Vector3(x, yT, z);
        }

        void SendPathIfChanged(List<Vector3> newPath)
        {
            if (newPath == null || newPath.Count == 0) return;

            if (lastSentPath.Count == newPath.Count)
            {
                float e2 = 1.5f * 1.5f;
                bool same = true;
                for (int i = 0; i < newPath.Count; i++)
                {
                    Vector3 d = newPath[i] - lastSentPath[i];
                    if (d.sqrMagnitude > e2) { same = false; break; }
                }
                if (same) return;
            }

            controller.SetPath(newPath);

            lastSentPath.Clear();
            lastSentPath.AddRange(newPath);
        }

        // -----------------------
        // Waypoints / Roam
        // -----------------------
        void BuildWaypointCache()
        {
            if (waypointRoot != null)
            {
                var list = new List<Transform>();
                for (int i = 0; i < waypointRoot.childCount; i++)
                    list.Add(waypointRoot.GetChild(i));
                waypoints = list.ToArray();
            }

            if (waypoints == null) waypoints = new Transform[0];

            if (shuffleWaypoints && waypoints.Length > 1)
            {
                for (int i = 0; i < waypoints.Length; i++)
                {
                    int j = Random.Range(i, waypoints.Length);
                    var tmp = waypoints[i];
                    waypoints[i] = waypoints[j];
                    waypoints[j] = tmp;
                }
            }

            waypointIndex = 0;
        }

        void EnsureInitialTarget()
        {
            if (routeMode == RouteMode.Waypoints)
            {
                if (waypoints != null && waypoints.Length > 0 && waypoints[0] != null)
                    currentTarget = waypoints[0].position;
                else
                    currentTarget = transform.position + transform.forward * 200f;
            }
            else
            {
                currentTarget = PickRoamTarget(transform.position);
            }
        }

        void MaybeAdvanceTarget()
        {
            goalSwitchCooldown -= 1f / Mathf.Max(1f, decisionHz);
            if (goalSwitchCooldown > 0f) return;

            Vector3 pos = transform.position;

            if (routeMode == RouteMode.Waypoints)
            {
                if (waypoints == null || waypoints.Length == 0) return;

                var wp = waypoints[waypointIndex];
                if (wp == null) return;

                float distXZ = Vector2.Distance(new Vector2(wp.position.x, wp.position.z), new Vector2(pos.x, pos.z));

                if (distXZ <= waypointReachDistXZ)
                {
                    int next = waypointIndex + 1;
                    if (next >= waypoints.Length)
                    {
                        if (!loopWaypoints) return;
                        next = 0;
                    }

                    waypointIndex = next;
                    var nextWp = waypoints[waypointIndex];
                    if (nextWp != null)
                    {
                        currentTarget = nextWp.position;
                        goalSwitchCooldown = minSecondsBetweenGoalSwitch;
                    }
                }
            }
            else
            {
                float distXZ = Vector2.Distance(new Vector2(currentTarget.x, currentTarget.z), new Vector2(pos.x, pos.z));

                if (distXZ <= waypointReachDistXZ)
                {
                    currentTarget = PickRoamTarget(pos);
                    goalSwitchCooldown = minSecondsBetweenGoalSwitch;
                }
            }
        }

        Vector3 PickRoamTarget(Vector3 from)
        {
            Vector3 dir = Random.insideUnitSphere;
            dir.y = 0f;
            dir.Normalize();

            float seg = Random.Range(roamMinSegment, roamMaxSegment);
            Vector3 p = from + dir * seg;

            p.x = Mathf.Clamp(p.x, roamBounds.min.x, roamBounds.max.x);
            p.z = Mathf.Clamp(p.z, roamBounds.min.z, roamBounds.max.z);

            // roamでも立体移動：目標Yは巡航高度へ
            float y = ComputeCruiseAltitude(new Vector3(p.x, 0f, p.z));
            p.y = y;

            return p;
        }
    }
}
