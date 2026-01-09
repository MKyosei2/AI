using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// 非戦闘時（巡回/移動/探索）に使う “ルートAI”。
    /// - NavMesh不使用
    /// - 低高度ポリシー（空中戦に見えない）
    /// - 遠距離：低高度LaneGraph（2.5D的）で大局ルート
    /// - 近距離：ローカル3Dグリッドで詰め
    /// - ログで「何をどう考えたか」を出せる
    ///
    /// 追跡対象（プレイヤー）は不要：
    /// - Waypoints（Transform配列）を順番/ランダムで巡回
    /// - または RandomRoam（ワールド内ランダム目的地）
    /// </summary>
    public class HighOrbitAI : MonoBehaviour
    {
        // -----------------------
        // Logging
        // -----------------------
        public enum LogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Verbose = 4 }

        [Header("Logging (What AI is thinking)")]
        public bool logEnabled = true;
        public LogLevel logLevel = LogLevel.Info;
        [Tooltip("同じようなログを出し過ぎないための間引き（秒）")]
        public float logThrottleSeconds = 0.15f;
        [Tooltip("Decision tickごとに出すか（Info以上で有効）。falseなら重要イベントだけ。")]
        public bool logEachDecisionTick = true;
        public bool logGoalSelect = true;
        public bool logPlanner = true;
        public bool logModeChange = true;
        public bool logKeepOut = true;

        float lastLogTime = -999f;

        void Log(LogLevel lvl, string msg)
        {
            if (!logEnabled) return;
            if ((int)lvl > (int)logLevel) return;

            float now = Time.time;
            if (lvl >= LogLevel.Info && (now - lastLogTime) < Mathf.Max(0f, logThrottleSeconds))
                return;

            lastLogTime = now;
            string prefix = $"[RouteAI:{name}] ";

            if (lvl == LogLevel.Error) Debug.LogError(prefix + msg);
            else if (lvl == LogLevel.Warn) Debug.LogWarning(prefix + msg);
            else Debug.Log(prefix + msg);
        }

        static string V3(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";

        static float PathLength(IReadOnlyList<Vector3> pts)
        {
            if (pts == null || pts.Count < 2) return 0f;
            float sum = 0f;
            for (int i = 1; i < pts.Count; i++) sum += Vector3.Distance(pts[i - 1], pts[i]);
            return sum;
        }

        // -----------------------
        // Refs
        // -----------------------
        [Header("Refs")]
        public VolumeCollector volumeCollector;
        public FlightController controller;

        // -----------------------
        // Route / Goal Source (Non-Combat)
        // -----------------------
        public enum RouteMode { Waypoints, RandomRoam }

        [Header("Route Source (Non-Combat)")]
        public RouteMode routeMode = RouteMode.Waypoints;

        [Tooltip("巡回点。空なら RoutePath を探すか、自動でランダム移動にフォールバックします。")]
        public Transform[] waypoints;

        [Tooltip("Waypointsを持つ親。指定すると子Transformを順番にWaypointsとして使います。")]
        public Transform waypointRoot;

        public bool loopWaypoints = true;
        public bool shuffleWaypoints = false;

        [Tooltip("目的地に到達したとみなす2D距離（XZ）")]
        public float waypointReachDistXZ = 12f;

        [Tooltip("目的地を次に切り替える最小間隔（秒）。連続で切り替わるのを防ぐ。")]
        public float minSecondsBetweenGoalSwitch = 0.75f;

        [Header("Random Roam")]
        public Bounds roamBounds = new Bounds(Vector3.zero, new Vector3(1400, 200, 1400));
        public bool autoEstimateRoamBoundsFromColliders = true;
        public float roamMinSegment = 220f;
        public float roamMaxSegment = 520f;

        // -----------------------
        // Decision / Policy
        // -----------------------
        [Header("Decision")]
        public float decisionHz = 20f;

        [Header("Altitude Policy (anti-air-combat)")]
        public float lowHeight = 10f;
        public float ceilingAboveGround = 35f;
        public float minAboveGround = 3f;

        public LayerMask groundMask = ~0;
        public float groundCastHeight = 1200f;
        public float groundMaxDistance = 2500f;

        [Header("Route Planning")]
        public float agentRadius = 0.8f;

        [Tooltip("この距離より遠いならLaneGraph（大局）。")]
        public float laneRange = 260f;

        [Tooltip("この距離より近いならローカル3D（Terminal）。")]
        public float terminalRange = 160f;

        [Header("Lane Graph")]
        public float laneNodeSpacing = 35f;
        public float laneEdgeMaxDist = 120f;

        [Header("Local Grid")]
        public float localCellSize = 6f;
        public float localRadius = 140f;

        [Header("Variety (same shortest cost)")]
        public float tieEpsilon = 0.02f;
        public float varietyPeriod = 2.0f;

        // -----------------------
        // Debug exposed
        // -----------------------
        public enum AIMode { Lane, Terminal }
        public AIMode CurrentMode => mode;

        public Vector3 DebugTarget { get; private set; }
        public Vector3 DebugGoal { get; private set; }
        public bool DebugLastPlanOk { get; private set; }
        public string DebugLastPlanMessage { get; private set; }
        public float DebugFlatDistance { get; private set; }
        public float DebugDesiredY { get; private set; }
        public float DebugCeilingY { get; private set; }
        public bool DebugInKeepOut { get; private set; }

        public int DebugWaypointIndex => waypointIndex;
        public int DebugWaypointCount => cachedWaypoints.Count;

        AIMode mode = AIMode.Lane;
        AIMode lastMode;

        // planners
        LowAltitudeLaneGraph laneGraph;
        LowAltitudeLaneGraphBuilder laneBuilder;
        AStarLaneVariety laneAstar;
        LocalGridPlannerVariety localPlanner;

        readonly List<Vector3> lanePath = new List<Vector3>(256);
        readonly List<Vector3> localPath = new List<Vector3>(256);

        // runtime state
        readonly List<Transform> cachedWaypoints = new List<Transform>(128);
        int waypointIndex = 0;

        float decisionTimer;
        bool graphReady;
        bool lastInKeepOut;
        int lastDbRevision;
        Bounds cachedWorldBounds;

        float lastGoalSwitchTime = -999f;
        Vector3 currentTarget;
        Vector3 currentGoal;

        void Reset()
        {
            controller = GetComponent<FlightController>();
        }

        void Start()
        {
            if (controller == null) controller = GetComponent<FlightController>();

            laneBuilder = new LowAltitudeLaneGraphBuilder
            {
                nodeSpacing = laneNodeSpacing,
                edgeMaxDistance = laneEdgeMaxDist,
                softCostSamples = 3,
                groundMask = groundMask,
                groundCastHeight = groundCastHeight,
                groundMaxDistance = groundMaxDistance,
            };

            laneAstar = new AStarLaneVariety { tieEpsilon = tieEpsilon, validateEdgesWithDb = true };
            if (volumeCollector != null) laneAstar.database = volumeCollector.database;
            localPlanner = new LocalGridPlannerVariety { cellSize = localCellSize, localRadius = localRadius, tieEpsilon = tieEpsilon };

            DebugLastPlanOk = false;
            DebugLastPlanMessage = "Init";

            lastMode = mode;
            lastInKeepOut = false;

            BuildWaypointCache();
            EnsureInitialTarget();

            lastDbRevision = (volumeCollector != null && volumeCollector.database != null) ? volumeCollector.database.revision : 0;

            Log(LogLevel.Info, "Started (Non-Combat Route AI).");
        }

        void Update()
        {
            if (volumeCollector == null || volumeCollector.database == null || controller == null)
                return;
// ensure validator sees latest DB (after domain reload etc.)
if (laneAstar != null && laneAstar.database == null)
    laneAstar.database = volumeCollector.database;

// if conditional/dynamic state changed, accelerate next decision tick
int revNow = volumeCollector.database.revision;
if (revNow != lastDbRevision)
{
    if (logEnabled && logLevel >= LogLevel.Info)
        Log(LogLevel.Info, $"DB revision changed: {lastDbRevision} -> {revNow} (conditional/dynamic update)");
    lastDbRevision = revNow;

    // force a quick re-plan on next frame
    decisionTimer = 1f / Mathf.Max(1f, decisionHz);
}


            DebugFlatDistance = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(currentTarget.x, currentTarget.z)
            );

            // mode selection by distance to currentTarget
            if (DebugFlatDistance < terminalRange) mode = AIMode.Terminal;
            else if (DebugFlatDistance > laneRange) mode = AIMode.Lane;

            if (logModeChange && mode != lastMode)
            {
                Log(LogLevel.Info, $"ModeChange: {lastMode} -> {mode} (flatDist={DebugFlatDistance:0.0}, terminal<{terminalRange}, lane>{laneRange})");
                lastMode = mode;
            }

            decisionTimer += Time.deltaTime;
            if (decisionTimer >= 1f / Mathf.Max(1f, decisionHz))
            {
                decisionTimer = 0f;

                volumeCollector.UpdateDynamicVolumes();
                EnsureLaneGraph();

                // select/advance target if needed
                MaybeAdvanceTarget();

                // compute goal with altitude constraints
                currentGoal = BuildGoalFromTarget(currentTarget);
                DebugTarget = currentTarget;
                DebugGoal = currentGoal;

                if (logEachDecisionTick && logLevel >= LogLevel.Info)
                {
                    Log(LogLevel.Info, $"Tick: mode={mode}, distXZ={DebugFlatDistance:0.0}, target={V3(currentTarget)}, goal={V3(currentGoal)}, wpi={waypointIndex}/{cachedWaypoints.Count}, routeMode={routeMode}");
                }

                if (mode == AIMode.Lane) PlanLane(currentGoal);
                else PlanLocal(currentGoal);
            }

            controller.Tick(Time.deltaTime);
            EnforceKeepOut();
        }

        // -----------------------
        // Waypoints / Roam
        // -----------------------
        void BuildWaypointCache()
        {
            cachedWaypoints.Clear();

            // 1) explicit array
            if (waypoints != null && waypoints.Length > 0)
            {
                for (int i = 0; i < waypoints.Length; i++)
                    if (waypoints[i] != null) cachedWaypoints.Add(waypoints[i]);
            }

            // 2) root children
            if (cachedWaypoints.Count == 0 && waypointRoot != null)
            {
                for (int i = 0; i < waypointRoot.childCount; i++)
                {
                    var t = waypointRoot.GetChild(i);
                    if (t != null) cachedWaypoints.Add(t);
                }
            }

            // 3) RoutePath component in scene
            if (cachedWaypoints.Count == 0)
            {
                var rp = FindFirstObjectByType<RoutePath>();
                if (rp != null && rp.points != null && rp.points.Count > 0)
                {
                    for (int i = 0; i < rp.points.Count; i++)
                        if (rp.points[i] != null) cachedWaypoints.Add(rp.points[i]);
                }
            }

            if (shuffleWaypoints && cachedWaypoints.Count > 1)
                Shuffle(cachedWaypoints);

            if (cachedWaypoints.Count == 0 && routeMode == RouteMode.Waypoints)
            {
                Log(LogLevel.Warn, "No waypoints found -> switching routeMode to RandomRoam.");
                routeMode = RouteMode.RandomRoam;
            }

            if (autoEstimateRoamBoundsFromColliders)
                roamBounds = EstimateRoamBoundsFallback(roamBounds);

            if (logGoalSelect && logLevel >= LogLevel.Info)
            {
                Log(LogLevel.Info, $"Waypoints cached: {cachedWaypoints.Count}, roamBounds={V3(roamBounds.center)} size={V3(roamBounds.size)}");
            }
        }

        void EnsureInitialTarget()
        {
            if (routeMode == RouteMode.Waypoints && cachedWaypoints.Count > 0)
            {
                waypointIndex = Mathf.Clamp(waypointIndex, 0, cachedWaypoints.Count - 1);
                currentTarget = cachedWaypoints[waypointIndex].position;
                lastGoalSwitchTime = Time.time;
                return;
            }

            currentTarget = PickRoamTarget(transform.position);
            lastGoalSwitchTime = Time.time;
        }

        void MaybeAdvanceTarget()
        {
            if (routeMode == RouteMode.Waypoints && cachedWaypoints.Count > 0)
            {
                Vector2 a = new Vector2(transform.position.x, transform.position.z);
                Vector2 b = new Vector2(currentTarget.x, currentTarget.z);
                float dist = Vector2.Distance(a, b);

                bool close = dist <= waypointReachDistXZ;
                bool timeOk = (Time.time - lastGoalSwitchTime) >= minSecondsBetweenGoalSwitch;

                if (close && timeOk)
                {
                    int old = waypointIndex;
                    waypointIndex++;

                    if (waypointIndex >= cachedWaypoints.Count)
                    {
                        if (loopWaypoints) waypointIndex = 0;
                        else waypointIndex = cachedWaypoints.Count - 1;
                    }

                    currentTarget = cachedWaypoints[waypointIndex].position;
                    lastGoalSwitchTime = Time.time;

                    if (logGoalSelect)
                        Log(LogLevel.Info, $"TargetSwitch: waypoint {old} -> {waypointIndex}, target={V3(currentTarget)} (distXZ={dist:0.0})");
                }
                else
                {
                    var t = cachedWaypoints[Mathf.Clamp(waypointIndex, 0, cachedWaypoints.Count - 1)];
                    if (t != null) currentTarget = t.position;
                }

                return;
            }

            {
                Vector2 a = new Vector2(transform.position.x, transform.position.z);
                Vector2 b = new Vector2(currentTarget.x, currentTarget.z);
                float dist = Vector2.Distance(a, b);

                bool close = dist <= waypointReachDistXZ;
                bool timeOk = (Time.time - lastGoalSwitchTime) >= minSecondsBetweenGoalSwitch;

                if (close && timeOk)
                {
                    Vector3 oldT = currentTarget;
                    currentTarget = PickRoamTarget(transform.position);
                    lastGoalSwitchTime = Time.time;

                    if (logGoalSelect)
                        Log(LogLevel.Info, $"TargetSwitch: roam {V3(oldT)} -> {V3(currentTarget)} (distXZ={dist:0.0})");
                }
            }
        }

        Vector3 PickRoamTarget(Vector3 from)
        {
            for (int i = 0; i < 24; i++)
            {
                float rx = Random.Range(roamBounds.min.x, roamBounds.max.x);
                float rz = Random.Range(roamBounds.min.z, roamBounds.max.z);

                Vector2 a = new Vector2(from.x, from.z);
                Vector2 b = new Vector2(rx, rz);
                float d = Vector2.Distance(a, b);

                if (d < roamMinSegment) continue;
                if (d > roamMaxSegment) continue;

                return new Vector3(rx, from.y, rz);
            }

            float x = Random.Range(roamBounds.min.x, roamBounds.max.x);
            float z = Random.Range(roamBounds.min.z, roamBounds.max.z);
            return new Vector3(x, from.y, z);
        }

        static void Shuffle<T>(List<T> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                int j = Random.Range(i, list.Count);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        Bounds EstimateRoamBoundsFallback(Bounds fallback)
        {
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            bool init = false;
            Bounds b = fallback;

            foreach (var c in cols)
            {
                if (!c.enabled) continue;
                if (!init) { b = c.bounds; init = true; }
                else b.Encapsulate(c.bounds);
            }

            if (!init) return fallback;

            b.Expand(new Vector3(800, 0, 800));
            b.center = new Vector3(b.center.x, fallback.center.y, b.center.z);
            b.size = new Vector3(b.size.x, fallback.size.y, b.size.z);
            return b;
        }

        // -----------------------
        // Altitude policy
        // -----------------------
        Vector3 BuildGoalFromTarget(Vector3 target)
        {
            float groundY;
            bool hasGround = GroundSampler.TryGetGroundY(target, groundCastHeight, groundMaxDistance, groundMask, out groundY);
            if (!hasGround) groundY = target.y;

            float desiredY = Mathf.Clamp(groundY + lowHeight, groundY + minAboveGround, groundY + ceilingAboveGround);
            float ceilingY = groundY + ceilingAboveGround;
            DebugDesiredY = desiredY;
            DebugCeilingY = ceilingY;

            return new Vector3(target.x, desiredY, target.z);
        }

        // -----------------------
        // Planning
        // -----------------------
        void EnsureLaneGraph()
        {
            if (graphReady) return;

            cachedWorldBounds = EstimateWorldBounds();
            cachedWorldBounds.Expand(new Vector3(1200, 800, 1200));

            laneBuilder.nodeSpacing = laneNodeSpacing;
            laneBuilder.edgeMaxDistance = laneEdgeMaxDist;

            float t0 = Time.realtimeSinceStartup;
            laneGraph = laneBuilder.Build(cachedWorldBounds, lowHeight, ceilingAboveGround, volumeCollector.database, agentRadius);
            float dt = (Time.realtimeSinceStartup - t0) * 1000f;

            graphReady = (laneGraph != null && laneGraph.nodes.Count > 0);

            if (graphReady) Log(LogLevel.Info, $"LaneGraph built: nodes={laneGraph.nodes.Count}, edges={laneGraph.edges.Count}, spacing={laneNodeSpacing}, buildTime={dt:0.0}ms");
            else Log(LogLevel.Warn, "LaneGraph build failed: no nodes (check groundMask / KeepOut coverage / world bounds).");
        }

        Bounds EstimateWorldBounds()
        {
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            bool init = false;
            Bounds b = new Bounds(transform.position, Vector3.one * 200f);
            foreach (var c in cols)
            {
                if (!c.enabled) continue;
                if (!init) { b = c.bounds; init = true; }
                else b.Encapsulate(c.bounds);
            }
            if (!init) b = new Bounds(transform.position, Vector3.one * 600f);
            return b;
        }

        int ComputeVarietySeed()
        {
            int t = Mathf.FloorToInt(Time.time / Mathf.Max(0.25f, varietyPeriod));
            int b = transform.GetInstanceID();
            unchecked { return b * 19349663 ^ t * 83492791; }
        }

        void PlanLane(Vector3 goal)
        {
            if (!graphReady)
            {
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Lane: graph not ready";
                controller.SetPath(new List<Vector3> { transform.position, goal });
                if (logPlanner) Log(LogLevel.Warn, "PlanLane: graph not ready -> direct");
                return;
            }

            int start = FindNearestLaneNode(transform.position);
            int end = FindNearestLaneNode(goal);
            if (start < 0 || end < 0)
            {
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Lane: no nodes";
                controller.SetPath(new List<Vector3> { transform.position, goal });
                if (logPlanner) Log(LogLevel.Warn, $"PlanLane: no nodes (start={start}, end={end}) -> direct");
                return;
            }

            int seed = ComputeVarietySeed();
            laneAstar.tieEpsilon = tieEpsilon;

            float t0 = Time.realtimeSinceStartup;
            bool ok = laneAstar.FindPath(laneGraph, start, end, seed, lanePath);
            float dt = (Time.realtimeSinceStartup - t0) * 1000f;

            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(lanePath.Count + 2);
                path.Add(transform.position);
                path.AddRange(lanePath);
                path.Add(goal);
                controller.SetPath(path);

                float len = PathLength(path);
                DebugLastPlanMessage = $"Lane: path={path.Count}";

                if (logPlanner && logLevel >= LogLevel.Info)
                    Log(LogLevel.Info, $"PlanLane: OK pts={path.Count}, len={len:0.0}, tieEps={tieEpsilon:0.000}, seed={seed}, compute={dt:0.0}ms");
            }
            else
            {
                controller.SetPath(new List<Vector3> { transform.position, goal });
                DebugLastPlanMessage = "Lane: A* failed -> direct";

                if (logPlanner) Log(LogLevel.Warn, $"PlanLane: FAIL -> direct (compute={dt:0.0}ms, start={start}, end={end})");
            }
        }

        void PlanLocal(Vector3 goal)
        {
            int seed = ComputeVarietySeed();

            localPlanner.cellSize = localCellSize;
            localPlanner.localRadius = localRadius;
            localPlanner.tieEpsilon = tieEpsilon;

            float t0 = Time.realtimeSinceStartup;
            bool ok = localPlanner.FindPath(
                transform.position, goal,
                volumeCollector.database,
                agentRadius,
                controller.maxClimbRate,
                controller.maxSpeed,
                DebugDesiredY,
                DebugCeilingY,
                seed,
                localPath
            );
            float dt = (Time.realtimeSinceStartup - t0) * 1000f;

            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(localPath.Count + 1);
                path.Add(transform.position);
                path.AddRange(localPath);
                controller.SetPath(path);

                float len = PathLength(path);
                DebugLastPlanMessage = $"Terminal: path={path.Count}";

                if (logPlanner && logLevel >= LogLevel.Info)
                    Log(LogLevel.Info, $"PlanLocal: OK pts={path.Count}, len={len:0.0}, cell={localCellSize}, rad={localRadius}, ceilY={DebugCeilingY:0.0}, seed={seed}, compute={dt:0.0}ms");
            }
            else
            {
                controller.SetPath(new List<Vector3> { transform.position, goal });
                DebugLastPlanMessage = "Terminal: A* failed -> direct";

                if (logPlanner) Log(LogLevel.Warn, $"PlanLocal: FAIL -> direct (compute={dt:0.0}ms, cell={localCellSize}, rad={localRadius})");
            }
        }

        int FindNearestLaneNode(Vector3 p)
        {
            if (laneGraph == null || laneGraph.nodes.Count == 0) return -1;

            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < laneGraph.nodes.Count; i++)
            {
                Vector3 np = laneGraph.nodes[i].pos;
                float dx = np.x - p.x;
                float dz = np.z - p.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        // -----------------------
        // KeepOut push
        // -----------------------
        void EnforceKeepOut()
        {
            var db = volumeCollector.database;
            db.EvaluatePoint(transform.position, agentRadius, out var flags, out _);

            DebugInKeepOut = (flags & NavFlags.KeepOut) != 0;

            if (logKeepOut && DebugInKeepOut != lastInKeepOut)
            {
                Log(LogLevel.Warn, DebugInKeepOut ? "KeepOut: ENTER" : "KeepOut: EXIT");
                lastInKeepOut = DebugInKeepOut;
            }

            if (!DebugInKeepOut) return;

            Vector3 fwd = transform.forward;
            Vector3 right = transform.right;
            Vector3 push = (right * 0.8f) + (fwd * 0.2f) + (Vector3.up * 0.25f);

            controller.ApplyKeepOutPush(push, Time.deltaTime);
        }
    }
}
