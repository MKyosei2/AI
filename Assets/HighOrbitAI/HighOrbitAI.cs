using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class HighOrbitAI : MonoBehaviour
    {
        public enum LogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Verbose = 4 }

        [Header("Logging (Perf tip: turn off in stress test)")]
        public bool logEnabled = true;
        public LogLevel logLevel = LogLevel.Info;
        public float logThrottleSeconds = 0.15f;
        public bool logEachDecisionTick = false;
        public bool logModeChange = false;

        float lastLogTime = -999f;
        void Log(LogLevel lvl, string msg)
        {
            if (!logEnabled) return;
            if ((int)lvl > (int)logLevel) return;

            float now = Time.time;
            if (lvl >= LogLevel.Info && (now - lastLogTime) < Mathf.Max(0f, logThrottleSeconds))
                return;

            lastLogTime = now;
            string prefix = $"[HighOrbitAI:{name}] ";
            if (lvl == LogLevel.Error) Debug.LogError(prefix + msg);
            else if (lvl == LogLevel.Warn) Debug.LogWarning(prefix + msg);
            else Debug.Log(prefix + msg);
        }

        // -----------------------
        // Refs
        // -----------------------
        [Header("Refs")]
        public VolumeCollector volumeCollector;
        public FlightController controller;

        // -----------------------
        // Route / Goal Source
        // -----------------------
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
        public bool autoEstimateRoamBoundsFromColliders = true;
        public float roamMinSegment = 220f;
        public float roamMaxSegment = 520f;

        // -----------------------
        // Update rates
        // -----------------------
        [Header("Decision (think rate)")]
        public float decisionHz = 10f;

        [Header("Planning (heavy)")]
        public float planHz = 4f;
        public float replanGoalDeltaXZ = 12f;

        [Header("Smoothing / Anti-stutter")]
        public bool staggerHeavyWork = true;
        public float pathChangeEpsilon = 1.5f;

        // -----------------------
        // Altitude policy
        // -----------------------
        [Header("Altitude Policy")]
        public float lowHeight = 10f;
        public float ceilingAboveGround = 35f;
        public float minAboveGround = 3f;

        public LayerMask groundMask = ~0;
        public float groundCastHeight = 1200f;
        public float groundMaxDistance = 2500f;

        // -----------------------
        // 3D Cruise Graph (Sparse)
        // -----------------------
        [Header("3D Cruise Graph (Sparse, Ultra-light)")]
        public float cruiseNodeSpacingXZ = 28f;
        public float cruiseLayerSpacingY = 25f;
        public float cruiseMinY = 10f;
        public float cruiseMaxY = 320f;
        public float cruiseEdgeMaxDistance = 95f;
        public int cruiseSoftSamples = 2;

        [Tooltip("低高度を嫌う係数。0で無効。")]
        public float cruiseLowAltPenalty = 0.0015f;

        [Tooltip("この高さ未満にいるほどペナルティ(上へ行きやすくなる)")]
        public float cruisePenaltyBaseY = 70f;

        // -----------------------
        // Terminal Local 3D Grid
        // -----------------------
        [Header("Terminal 3D Grid (Refine)")]
        public float terminalRange = 160f;
        public float localCellSize = 6f;
        public float localRadius = 140f;

        [Header("Variety")]
        public float tieEpsilon = 0.02f;
        public float varietyPeriod = 2.0f;

        // -----------------------
        // DebugView compatibility (IMPORTANT)
        // DebugView は AIMode.Lane / AIMode.Terminal を期待している
        // ここでは Lane = Cruise3D(3D疎グラフ), Terminal = Local3D(精密)
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

        // DebugView互換（戦闘なしの固定）
        public bool DebugMelee { get; private set; }
        public bool DebugShooting { get; private set; }
        public bool DebugBoost { get; private set; }
        public bool DebugEvade { get; private set; }
        public string DebugPhase { get; private set; }
        public string DebugTactic { get; private set; }
        public TacticalDirector.AltitudeBand DebugBand { get; private set; }

        public int DebugWaypointIndex => waypointIndex;
        public int DebugWaypointCount => cachedWaypoints.Count;

        // -----------------------
        // Internals
        // -----------------------
        AIMode mode = AIMode.Lane;
        AIMode lastMode = AIMode.Lane;

        readonly List<Transform> cachedWaypoints = new List<Transform>(128);
        int waypointIndex = 0;
        float lastGoalSwitchTime = -999f;

        float decisionTimer;
        float planTimer;

        Vector3 currentTarget;
        Vector3 currentGoal;
        Vector3 lastPlannedGoal;

        // 共有ガード（動的更新を1フレーム1回）
        static int s_lastDynTickFrame = -1;
        static VolumeCollector s_lastDynTickCollector = null;

        // setPath微小更新抑制
        readonly List<Vector3> lastSentPath = new List<Vector3>(256);

        // プランずらし
        float heavyPhaseOffset;

        // --- 3D cruise graph ---
        AirLaneGraph cruiseGraph;
        AirLaneGraph3DBuilder cruiseBuilder;
        AStarAirLane cruiseAstar;
        bool cruiseGraphReady;
        Bounds cachedWorldBounds;
        int lastDbRevision;

        // --- terminal refine ---
        LocalGridPlannerVariety localPlanner;
        readonly List<Vector3> cruisePath = new List<Vector3>(512);
        readonly List<Vector3> localPath = new List<Vector3>(256);

        void Reset()
        {
            controller = GetComponent<FlightController>();
        }

        void Start()
        {
            if (controller == null) controller = GetComponent<FlightController>();

            cruiseBuilder = new AirLaneGraph3DBuilder
            {
                nodeSpacingXZ = cruiseNodeSpacingXZ,
                layerSpacingY = cruiseLayerSpacingY,
                minY = cruiseMinY,
                maxY = cruiseMaxY,
                edgeMaxDistance = cruiseEdgeMaxDistance,
                softCostSamples = cruiseSoftSamples,
                lowAltitudePenalty = cruiseLowAltPenalty,
                penaltyBaseY = cruisePenaltyBaseY
            };

            cruiseAstar = new AStarAirLane();

            localPlanner = new LocalGridPlannerVariety
            {
                cellSize = localCellSize,
                localRadius = localRadius,
                tieEpsilon = tieEpsilon
            };

            BuildWaypointCache();
            EnsureInitialTarget();

            lastDbRevision = (volumeCollector != null && volumeCollector.database != null) ? volumeCollector.database.revision : 0;

            DebugPhase = "None";
            DebugTactic = "-";
            DebugBand = TacticalDirector.AltitudeBand.Mid;

            int id = GetInstanceID();
            float r01 = Mathf.Abs((id * 1103515245 + 12345) & 0x7fffffff) / (float)int.MaxValue;
            heavyPhaseOffset = r01;

            DebugLastPlanOk = false;
            DebugLastPlanMessage = "Init";
        }

        void Update()
        {
            if (volumeCollector == null || volumeCollector.database == null || controller == null)
                return;

            // Dynamic update: 1x per frame
            if (s_lastDynTickFrame != Time.frameCount || s_lastDynTickCollector != volumeCollector)
            {
                s_lastDynTickFrame = Time.frameCount;
                s_lastDynTickCollector = volumeCollector;
                volumeCollector.TickDynamic(Time.deltaTime);
            }

            int revNow = volumeCollector.database.revision;
            if (revNow != lastDbRevision)
            {
                lastDbRevision = revNow;
                planTimer = 999f;
                cruiseGraphReady = false;
            }

            // Think (light)
            decisionTimer += Time.deltaTime;
            if (decisionTimer >= 1f / Mathf.Max(1f, decisionHz))
            {
                decisionTimer = 0f;

                EnsureCruiseGraph();
                MaybeAdvanceTarget();

                currentGoal = BuildGoalFromTarget(currentTarget);

                DebugTarget = currentTarget;
                DebugGoal = currentGoal;

                // DebugView互換（戦闘無し）
                DebugMelee = DebugShooting = DebugBoost = DebugEvade = false;
                DebugPhase = "None";
                DebugTactic = "-";
                DebugBand = TacticalDirector.AltitudeBand.Mid;

                // KeepOut内か（ゴール地点を軽く判定）
                volumeCollector.database.EvaluatePoint(currentGoal, 0.8f, out var flags, out _);
                DebugInKeepOut = (flags & NavFlags.KeepOut) != 0;

                // mode判定：terminalRange以内ならTerminal(=Local3D)、それ以外はLane(=Cruise3D)
                DebugFlatDistance = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentGoal.x, currentGoal.z)
                );

                mode = (DebugFlatDistance <= terminalRange) ? AIMode.Terminal : AIMode.Lane;

                if (logModeChange && mode != lastMode)
                {
                    Log(LogLevel.Info, $"ModeChange: {lastMode} -> {mode} (flat={DebugFlatDistance:0.0})");
                    lastMode = mode;
                }
            }

            // Plan (heavy)
            float planInterval = (planHz <= 0f) ? 0f : (1f / Mathf.Max(0.1f, planHz));
            planTimer += Time.deltaTime;

            bool goalMoved = (Vector2.Distance(
                new Vector2(currentGoal.x, currentGoal.z),
                new Vector2(lastPlannedGoal.x, lastPlannedGoal.z)
            ) >= Mathf.Max(0.01f, replanGoalDeltaXZ));

            bool timeToPlan = (planHz <= 0f) || (planTimer >= planInterval);

            bool phaseOk = true;
            if (staggerHeavyWork && planInterval > 0f)
            {
                float phase = (Time.time / planInterval) % 1f;
                float distPhase = Mathf.Abs(phase - heavyPhaseOffset);
                distPhase = Mathf.Min(distPhase, 1f - distPhase);
                phaseOk = distPhase < 0.18f;
            }

            if ((timeToPlan && phaseOk) || goalMoved)
            {
                planTimer = 0f;
                Plan3D(currentGoal);
                lastPlannedGoal = currentGoal;
            }

            controller.Tick(Time.deltaTime);
        }

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

        void EnsureCruiseGraph()
        {
            if (cruiseGraphReady && cruiseGraph != null && cruiseGraph.nodes.Count > 0) return;

            cachedWorldBounds = EstimateWorldBounds();
            cachedWorldBounds.Expand(new Vector3(1400f, 0f, 1400f));

            cruiseBuilder.nodeSpacingXZ = cruiseNodeSpacingXZ;
            cruiseBuilder.layerSpacingY = cruiseLayerSpacingY;
            cruiseBuilder.minY = cruiseMinY;
            cruiseBuilder.maxY = cruiseMaxY;
            cruiseBuilder.edgeMaxDistance = cruiseEdgeMaxDistance;
            cruiseBuilder.softCostSamples = cruiseSoftSamples;
            cruiseBuilder.lowAltitudePenalty = cruiseLowAltPenalty;
            cruiseBuilder.penaltyBaseY = cruisePenaltyBaseY;

            cruiseGraph = cruiseBuilder.Build(cachedWorldBounds, volumeCollector.database, 0.7f);
            cruiseGraphReady = cruiseGraph != null && cruiseGraph.nodes.Count > 0;
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

        int FindNearestCruiseNode(Vector3 p)
        {
            if (cruiseGraph == null || cruiseGraph.nodes.Count == 0) return -1;

            int best = 0;
            float bestD = float.PositiveInfinity;

            for (int i = 0; i < cruiseGraph.nodes.Count; i++)
            {
                Vector3 np = cruiseGraph.nodes[i].pos;
                float dx = np.x - p.x;
                float dy = np.y - p.y;
                float dz = np.z - p.z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        void Plan3D(Vector3 goal)
        {
            if (!cruiseGraphReady || cruiseGraph == null || cruiseGraph.nodes.Count == 0)
            {
                var direct = new List<Vector3> { transform.position, goal };
                SendPathIfChanged(direct);
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Cruise3DGraph not ready -> direct";
                return;
            }

            if (mode == AIMode.Terminal)
            {
                PlanTerminalLocal3D(goal);
                return;
            }

            int s = FindNearestCruiseNode(transform.position);
            int g = FindNearestCruiseNode(goal);
            if (s < 0 || g < 0)
            {
                var direct = new List<Vector3> { transform.position, goal };
                SendPathIfChanged(direct);
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Nearest node missing -> direct";
                return;
            }

            bool ok = cruiseAstar.FindPath(cruiseGraph, s, g, cruisePath);
            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(cruisePath.Count + 2);
                path.Add(transform.position);
                path.AddRange(cruisePath);
                path.Add(goal);

                SendPathIfChanged(path);
                DebugLastPlanMessage = $"Cruise3D ok nodes={cruisePath.Count}";
            }
            else
            {
                var direct = new List<Vector3> { transform.position, goal };
                SendPathIfChanged(direct);
                DebugLastPlanMessage = "Cruise3D failed -> direct";
            }
        }

        int ComputeVarietySeed()
        {
            int t = Mathf.FloorToInt(Time.time / Mathf.Max(0.25f, varietyPeriod));
            int b = transform.GetInstanceID();
            unchecked { return b * 19349663 ^ t * 83492791; }
        }

        void PlanTerminalLocal3D(Vector3 goal)
        {
            localPlanner.cellSize = localCellSize;
            localPlanner.localRadius = localRadius;
            localPlanner.tieEpsilon = tieEpsilon;

            int seed = ComputeVarietySeed();

            bool ok = localPlanner.FindPath(
                transform.position, goal,
                volumeCollector.database,
                agentRadius: 0.9f,
                maxClimbRate: controller.cruise.maxClimbRate,
                maxSpeed: controller.cruise.maxSpeed,
                desiredY: DebugDesiredY,
                ceilingY: DebugCeilingY,
                seed: seed,
                outPath: localPath
            );

            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(localPath.Count + 1);
                path.Add(transform.position);
                path.AddRange(localPath);
                SendPathIfChanged(path);
                DebugLastPlanMessage = $"Terminal3D ok nodes={localPath.Count}";
            }
            else
            {
                var direct = new List<Vector3> { transform.position, goal };
                SendPathIfChanged(direct);
                DebugLastPlanMessage = "Terminal3D failed -> direct";
            }
        }

        void SendPathIfChanged(List<Vector3> path)
        {
            if (path == null || path.Count == 0) return;
            if (!PathChangedSignificantly(path)) return;

            controller.SetPath(path);

            lastSentPath.Clear();
            for (int i = 0; i < path.Count; i++) lastSentPath.Add(path[i]);
        }

        bool PathChangedSignificantly(List<Vector3> newPath)
        {
            if (lastSentPath.Count == 0) return true;
            int n = Mathf.Min(3, Mathf.Min(newPath.Count, lastSentPath.Count));

            for (int i = 0; i < n; i++)
            {
                float d = Vector3.Distance(newPath[i], lastSentPath[i]);
                if (d > pathChangeEpsilon) return true;
            }
            return false;
        }

        // -----------------------
        // Waypoints / Roam
        // -----------------------
        void BuildWaypointCache()
        {
            cachedWaypoints.Clear();

            if (waypoints != null && waypoints.Length > 0)
                for (int i = 0; i < waypoints.Length; i++)
                    if (waypoints[i] != null) cachedWaypoints.Add(waypoints[i]);

            if (cachedWaypoints.Count == 0 && waypointRoot != null)
                for (int i = 0; i < waypointRoot.childCount; i++)
                    cachedWaypoints.Add(waypointRoot.GetChild(i));

            if (shuffleWaypoints && cachedWaypoints.Count > 1)
                Shuffle(cachedWaypoints);

            if (cachedWaypoints.Count == 0 && routeMode == RouteMode.Waypoints)
                routeMode = RouteMode.RandomRoam;

            if (autoEstimateRoamBoundsFromColliders)
                roamBounds = EstimateRoamBoundsFallback(roamBounds);
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
            Vector2 a = new Vector2(transform.position.x, transform.position.z);
            Vector2 b = new Vector2(currentTarget.x, currentTarget.z);
            float dist = Vector2.Distance(a, b);

            bool close = dist <= waypointReachDistXZ;
            bool timeOk = (Time.time - lastGoalSwitchTime) >= minSecondsBetweenGoalSwitch;

            if (!close || !timeOk)
            {
                if (routeMode == RouteMode.Waypoints && cachedWaypoints.Count > 0)
                {
                    var t = cachedWaypoints[Mathf.Clamp(waypointIndex, 0, cachedWaypoints.Count - 1)];
                    if (t != null) currentTarget = t.position;
                }
                return;
            }

            if (routeMode == RouteMode.Waypoints && cachedWaypoints.Count > 0)
            {
                waypointIndex++;
                if (waypointIndex >= cachedWaypoints.Count)
                    waypointIndex = loopWaypoints ? 0 : cachedWaypoints.Count - 1;

                currentTarget = cachedWaypoints[waypointIndex].position;
                lastGoalSwitchTime = Time.time;
                return;
            }

            currentTarget = PickRoamTarget(transform.position);
            lastGoalSwitchTime = Time.time;
        }

        Vector3 PickRoamTarget(Vector3 from)
        {
            for (int i = 0; i < 24; i++)
            {
                float rx = Random.Range(roamBounds.min.x, roamBounds.max.x);
                float rz = Random.Range(roamBounds.min.z, roamBounds.max.z);

                float d = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(rx, rz));
                if (d < roamMinSegment) continue;
                if (d > roamMaxSegment) continue;

                return new Vector3(rx, from.y, rz);
            }

            return new Vector3(
                Random.Range(roamBounds.min.x, roamBounds.max.x),
                from.y,
                Random.Range(roamBounds.min.z, roamBounds.max.z)
            );
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
    }
}
