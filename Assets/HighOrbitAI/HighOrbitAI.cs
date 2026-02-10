using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class HighOrbitAI : MonoBehaviour
    {
        public enum LogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Verbose = 4 }

        [Header("Logging")]
        public bool logEnabled = true;
        public LogLevel logLevel = LogLevel.Info;
        public float logThrottleSeconds = 0.15f;
        public bool logEachDecisionTick = true;
        public bool logPlanner = true;

        float lastLogTime = -999f;

        void Log(LogLevel lvl, string msg)
        {
            if (!logEnabled) return;
            if ((int)lvl > (int)logLevel) return;

            float now = Time.time;
            if (lvl >= LogLevel.Info && (now - lastLogTime) < Mathf.Max(0f, logThrottleSeconds))
                return;

            lastLogTime = now;
            string prefix = $"[AI:{name}] ";

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

        [Header("Refs")]
        public VolumeCollector volumeCollector;
        public FlightController controller;

        [Header("Combat (Optional)")]
        public Transform combatTarget;
        public MonoBehaviour combatStateProvider;
        ICombatStateProvider combat;

        public enum RouteMode { Waypoints, RandomRoam }
        [Header("Route Source (Non-Combat)")]
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

        [Header("Decision")]
        public float decisionHz = 20f;

        [Header("Altitude Policy (Base)")]
        public float lowHeight = 10f;
        public float ceilingAboveGround = 35f;
        public float minAboveGround = 3f;

        public LayerMask groundMask = ~0;
        public float groundCastHeight = 1200f;
        public float groundMaxDistance = 2500f;

        [Header("Route Planning")]
        public float agentRadius = 0.8f;

        public float laneRange = 260f;
        public float terminalRange = 160f;

        [Header("Lane Graph")]
        public float laneNodeSpacing = 35f;
        public float laneEdgeMaxDist = 120f;

        [Header("Local Grid")]
        public float localCellSize = 6f;
        public float localRadius = 140f;

        [Header("Variety")]
        public float tieEpsilon = 0.02f;
        public float varietyPeriod = 2.0f;

        [Header("Tactics (Next-Gen)")]
        public bool enableTactics = true;
        public float tacticsActivateDistXZ = 600f;

        public float bandLowAdd = 0f;
        public float bandMidAdd = 25f;
        public float bandHighAdd = 80f;

        TacticalDirector tactics = new TacticalDirector();

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

        // ★追加：Combatデバッグ
        public bool DebugMelee { get; private set; }
        public bool DebugShooting { get; private set; }
        public bool DebugBoost { get; private set; }
        public bool DebugEvade { get; private set; }

        public string DebugTactic { get; private set; }
        public TacticalDirector.AltitudeBand DebugBand { get; private set; }

        AIMode mode = AIMode.Lane;

        LowAltitudeLaneGraph laneGraph;
        LowAltitudeLaneGraphBuilder laneBuilder;
        AStarLaneVariety laneAstar;
        LocalGridPlannerVariety localPlanner;

        readonly List<Vector3> lanePath = new List<Vector3>(256);
        readonly List<Vector3> localPath = new List<Vector3>(256);

        readonly List<Transform> cachedWaypoints = new List<Transform>(128);
        int waypointIndex = 0;

        float decisionTimer;
        bool graphReady;
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

            combat = null;
            if (combatStateProvider != null) combat = combatStateProvider as ICombatStateProvider;
            if (combat == null) combat = GetComponent<ICombatStateProvider>();

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

            localPlanner = new LocalGridPlannerVariety
            {
                cellSize = localCellSize,
                localRadius = localRadius,
                tieEpsilon = tieEpsilon
            };

            BuildWaypointCache();
            EnsureInitialTarget();

            lastDbRevision = (volumeCollector != null && volumeCollector.database != null) ? volumeCollector.database.revision : 0;

            DebugLastPlanOk = false;
            DebugLastPlanMessage = "Init";
            DebugTactic = "-";
            DebugBand = TacticalDirector.AltitudeBand.Mid;

            Log(LogLevel.Info, "Started (Route AI + Tactics).");
        }

        void Update()
        {
            if (volumeCollector == null || volumeCollector.database == null || controller == null)
                return;

            volumeCollector.UpdateDynamicVolumes();

            int revNow = volumeCollector.database.revision;
            if (revNow != lastDbRevision)
            {
                lastDbRevision = revNow;
                decisionTimer = 999f;
            }

            decisionTimer += Time.deltaTime;
            if (decisionTimer >= (1f / Mathf.Max(1f, decisionHz)))
            {
                decisionTimer = 0f;

                EnsureLaneGraph();
                SelectNonCombatTarget();
                ApplyTacticsIfActive();

                currentGoal = BuildGoalFromTarget(currentTarget, DebugBand);
                DebugTarget = currentTarget;
                DebugGoal = currentGoal;

                float dx = currentGoal.x - transform.position.x;
                float dz = currentGoal.z - transform.position.z;
                DebugFlatDistance = Mathf.Sqrt(dx * dx + dz * dz);

                if (DebugFlatDistance >= laneRange) mode = AIMode.Lane;
                else if (DebugFlatDistance <= terminalRange) mode = AIMode.Terminal;

                if (mode == AIMode.Lane) PlanLane(currentGoal);
                else PlanLocal(currentGoal);

                if (logEachDecisionTick && logLevel >= LogLevel.Info)
                {
                    Log(LogLevel.Info,
                        $"Tick mode={mode}, tactic={DebugTactic}, band={DebugBand}, target={V3(DebugTarget)}, goal={V3(DebugGoal)}, distXZ={DebugFlatDistance:0.0}, ok={DebugLastPlanOk}, msg={DebugLastPlanMessage}");
                }
            }

            controller.Tick(Time.deltaTime);
            EnforceKeepOut();
        }

        void SelectNonCombatTarget()
        {
            if (routeMode == RouteMode.RandomRoam)
            {
                if (ShouldSwitchGoalXZ(currentTarget))
                    currentTarget = PickRandomRoamTarget();
                return;
            }

            if (cachedWaypoints.Count == 0)
            {
                routeMode = RouteMode.RandomRoam;
                currentTarget = PickRandomRoamTarget();
                return;
            }

            currentTarget = cachedWaypoints[waypointIndex].position;
            if (ShouldSwitchGoalXZ(currentTarget))
                AdvanceWaypoint();
        }

        bool ShouldSwitchGoalXZ(Vector3 target)
        {
            if (Time.time - lastGoalSwitchTime < minSecondsBetweenGoalSwitch) return false;

            float dx = target.x - transform.position.x;
            float dz = target.z - transform.position.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            return d <= waypointReachDistXZ;
        }

        void EnsureInitialTarget()
        {
            if (routeMode == RouteMode.RandomRoam)
            {
                currentTarget = PickRandomRoamTarget();
                return;
            }

            if (cachedWaypoints.Count == 0)
            {
                routeMode = RouteMode.RandomRoam;
                currentTarget = PickRandomRoamTarget();
                return;
            }

            waypointIndex = Mathf.Clamp(waypointIndex, 0, cachedWaypoints.Count - 1);
            currentTarget = cachedWaypoints[waypointIndex].position;
        }

        void AdvanceWaypoint()
        {
            lastGoalSwitchTime = Time.time;

            if (shuffleWaypoints)
            {
                waypointIndex = Random.Range(0, cachedWaypoints.Count);
                currentTarget = cachedWaypoints[waypointIndex].position;
                return;
            }

            waypointIndex++;
            if (waypointIndex >= cachedWaypoints.Count)
            {
                if (loopWaypoints) waypointIndex = 0;
                else waypointIndex = cachedWaypoints.Count - 1;
            }

            currentTarget = cachedWaypoints[waypointIndex].position;
        }

        void BuildWaypointCache()
        {
            cachedWaypoints.Clear();

            if (waypoints != null && waypoints.Length > 0)
            {
                for (int i = 0; i < waypoints.Length; i++)
                    if (waypoints[i] != null) cachedWaypoints.Add(waypoints[i]);
            }

            if (cachedWaypoints.Count == 0 && waypointRoot != null)
            {
                for (int i = 0; i < waypointRoot.childCount; i++)
                {
                    var t = waypointRoot.GetChild(i);
                    if (t != null) cachedWaypoints.Add(t);
                }
            }

            if (cachedWaypoints.Count == 0)
            {
                var rp = FindFirstObjectByType<RoutePath>();
                if (rp != null && rp.points != null && rp.points.Count > 0)
                {
                    for (int i = 0; i < rp.points.Count; i++)
                        if (rp.points[i] != null) cachedWaypoints.Add(rp.points[i]);
                }
            }
        }

        Vector3 PickRandomRoamTarget()
        {
            if (autoEstimateRoamBoundsFromColliders && cachedWorldBounds.size.sqrMagnitude > 1f)
                roamBounds = cachedWorldBounds;

            Vector2 dir = Random.insideUnitCircle.normalized;
            float seg = Random.Range(roamMinSegment, roamMaxSegment);

            Vector3 p = transform.position + new Vector3(dir.x, 0f, dir.y) * seg;

            Vector3 c = roamBounds.center;
            Vector3 e = roamBounds.extents;
            p.x = Mathf.Clamp(p.x, c.x - e.x, c.x + e.x);
            p.z = Mathf.Clamp(p.z, c.z - e.z, c.z + e.z);

            lastGoalSwitchTime = Time.time;
            return p;
        }

        void ApplyTacticsIfActive()
        {
            // combat flags debug update
            DebugMelee = combat != null && combat.IsMeleeEngaging;
            DebugShooting = combat != null && combat.IsShooting;
            DebugBoost = combat != null && combat.IsBoosting;
            DebugEvade = combat != null && combat.IsEvading;

            if (!enableTactics || combatTarget == null)
            {
                DebugTactic = "-";
                DebugBand = TacticalDirector.AltitudeBand.Mid;
                return;
            }

            Vector3 to = combatTarget.position - transform.position;
            float distXZ = Mathf.Sqrt(to.x * to.x + to.z * to.z);
            if (distXZ > tacticsActivateDistXZ)
            {
                DebugTactic = "-";
                DebugBand = TacticalDirector.AltitudeBand.High;
                return;
            }

            volumeCollector.database.EvaluatePoint(transform.position, agentRadius, out var flags, out _);
            DebugInKeepOut = (flags & NavFlags.KeepOut) != 0;

            var res = tactics.Decide(
                transform.position,
                transform.forward,
                combatTarget,
                volumeCollector.database,
                agentRadius,
                DebugInKeepOut,
                combat,
                Time.time);

            DebugTactic = res.tactic.ToString();
            DebugBand = res.band;

            currentTarget = res.targetPos;

            controller.SetProfile(res.profile, res.profileHold);
            controller.SetExtraSteer(res.extraSteer);
        }

        Vector3 BuildGoalFromTarget(Vector3 target, TacticalDirector.AltitudeBand band)
        {
            float groundY;
            bool hasGround = GroundSampler.TryGetGroundY(target, groundCastHeight, groundMaxDistance, groundMask, out groundY);
            if (!hasGround) groundY = target.y;

            float add =
                band == TacticalDirector.AltitudeBand.Low ? bandLowAdd :
                band == TacticalDirector.AltitudeBand.High ? bandHighAdd :
                bandMidAdd;

            float desiredY = Mathf.Clamp(
                groundY + lowHeight + add,
                groundY + minAboveGround,
                groundY + ceilingAboveGround + add);

            float ceilingY = groundY + ceilingAboveGround + add;

            DebugDesiredY = desiredY;
            DebugCeilingY = ceilingY;

            return new Vector3(target.x, desiredY, target.z);
        }

        void EnsureLaneGraph()
        {
            if (graphReady) return;

            cachedWorldBounds = EstimateWorldBounds();
            cachedWorldBounds.Expand(new Vector3(1200, 800, 1200));

            laneBuilder.nodeSpacing = laneNodeSpacing;
            laneBuilder.edgeMaxDistance = laneEdgeMaxDist;

            laneGraph = laneBuilder.Build(cachedWorldBounds, lowHeight, ceilingAboveGround, volumeCollector.database, agentRadius);
            graphReady = (laneGraph != null && laneGraph.nodes.Count > 0);
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
                return;
            }

            int start = FindNearestLaneNode(transform.position);
            int end = FindNearestLaneNode(goal);
            if (start < 0 || end < 0)
            {
                DebugLastPlanOk = false;
                DebugLastPlanMessage = "Lane: no nodes";
                controller.SetPath(new List<Vector3> { transform.position, goal });
                return;
            }

            int seed = ComputeVarietySeed();
            laneAstar.tieEpsilon = tieEpsilon;

            bool ok = laneAstar.FindPath(laneGraph, start, end, seed, lanePath);
            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(lanePath.Count + 2);
                path.Add(transform.position);
                path.AddRange(lanePath);
                path.Add(goal);
                controller.SetPath(path);

                DebugLastPlanMessage = $"Lane: path={path.Count}";
                if (logPlanner && logLevel >= LogLevel.Info)
                    Log(LogLevel.Info, $"PlanLane OK pts={path.Count} len={PathLength(path):0.0}");
            }
            else
            {
                controller.SetPath(new List<Vector3> { transform.position, goal });
                DebugLastPlanMessage = "Lane: A* failed -> direct";
            }
        }

        void PlanLocal(Vector3 goal)
        {
            int seed = ComputeVarietySeed();

            localPlanner.cellSize = localCellSize;
            localPlanner.localRadius = localRadius;
            localPlanner.tieEpsilon = tieEpsilon;

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

            DebugLastPlanOk = ok;

            if (ok)
            {
                var path = new List<Vector3>(localPath.Count + 1);
                path.Add(transform.position);
                path.AddRange(localPath);
                controller.SetPath(path);

                DebugLastPlanMessage = $"Terminal: path={path.Count}";
                if (logPlanner && logLevel >= LogLevel.Info)
                    Log(LogLevel.Info, $"PlanLocal OK pts={path.Count} len={PathLength(path):0.0}");
            }
            else
            {
                controller.SetPath(new List<Vector3> { transform.position, goal });
                DebugLastPlanMessage = "Terminal: A* failed -> direct";
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

        void EnforceKeepOut()
        {
            if (!DebugInKeepOut) return;

            Vector3 fwd = transform.forward;
            Vector3 right = transform.right;
            Vector3 push = (right * 0.8f) + (fwd * 0.2f) + (Vector3.up * 0.25f);

            controller.ApplyKeepOutPush(push, Time.deltaTime);
        }
    }
}
