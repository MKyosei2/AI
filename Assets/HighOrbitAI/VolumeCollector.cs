using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    public class VolumeCollector : MonoBehaviour
    {
        public LayerMask staticMask;
        public LayerMask dynamicMask;

        public float agentRadius = 0.8f;
        public float dynamicCellSize = 10f;

        [Header("Perf")]
        public bool enableDynamicUpdates = true;

        [Tooltip("動的Volumeの更新頻度(Hz)。0なら毎フレーム更新。2〜5推奨。")]
        public float dynamicUpdateHz = 4f;

        [Header("World Bounds (for Cruise Graph)")]
        [Tooltip("Build時に収集した“静的”領域Bounds。Cruiseグラフ生成の基準に使う。")]
        public Bounds staticWorldBounds;

        [Tooltip("staticWorldBoundsに足す余白(XZ)。大きいほど重い。")]
        public float staticWorldPaddingXZ = 80f;

        [Tooltip("静的Boundsの代わりにこのBoundsを強制使用（巨大マップ対策）。")]
        public bool useOverrideWorldBounds = false;

        public Bounds overrideWorldBounds = new Bounds(Vector3.zero, new Vector3(800, 200, 800));

        public VolumeDatabase database;

        readonly List<DynamicEntry> dynamicEntries = new List<DynamicEntry>(256);

        struct DynamicEntry
        {
            public int volumeId;
            public Collider collider;
            public KeepOutZone keepOut;
            public SoftAvoidZone softAvoid;
            public ConditionalVolume conditional;
        }

        float dynTimer;

        void Awake()
        {
            EnsureDatabase();
            Build();
        }

        // ★追加：EditorからBuildが呼ばれても落ちないようにする
        void EnsureDatabase()
        {
            if (database == null)
            {
                database = new VolumeDatabase();
                database.Initialize(dynamicCellSize);
            }
            else
            {
                // dynamicCellSize が Inspector で変更された場合に備える
                // VolumeDatabase側に再Initializeが不要ならこのままでOK
                //（必要なら、VolumeDatabaseに「Initialized」フラグがある前提で調整）
            }
        }

        public Bounds GetWorldBoundsForCruise()
        {
            if (useOverrideWorldBounds) return overrideWorldBounds;

            var b = staticWorldBounds;
            float pad = Mathf.Max(0f, staticWorldPaddingXZ);
            b.Expand(new Vector3(pad * 2f, 0f, pad * 2f));
            return b;
        }

        public void Build()
        {
            // ★ここが今回のNullRef対策の核心
            EnsureDatabase();

            database.ClearAll();

            dynamicEntries.Clear();

            bool boundsInit = false;
            Bounds bounds = default;

            CollectLayerColliders(ref boundsInit, ref bounds);
            CollectZones(ref boundsInit, ref bounds);
            CollectConditionalVolumes(ref boundsInit, ref bounds);

            if (!boundsInit)
                bounds = new Bounds(transform.position, new Vector3(400, 200, 400));

            staticWorldBounds = bounds;

            database.BuildStaticBVH();

            if (dynamicEntries.Count > 0)
                database.RebuildDynamicHash();
        }

        public void TickDynamic(float dt)
        {
            if (!enableDynamicUpdates) return;
            if (dynamicEntries.Count == 0) return;

            if (dynamicUpdateHz <= 0f)
            {
                UpdateDynamicVolumesImmediate();
                return;
            }

            dynTimer += dt;
            float interval = 1f / Mathf.Max(0.1f, dynamicUpdateHz);
            if (dynTimer < interval) return;

            dynTimer = 0f;
            UpdateDynamicVolumesImmediate();
        }

        // 互換用
        public void UpdateDynamicVolumes()
        {
            if (dynamicEntries.Count == 0) return;
            UpdateDynamicVolumesImmediate();
        }

        void UpdateDynamicVolumesImmediate()
        {
            bool anyChanged = false;

            for (int i = 0; i < dynamicEntries.Count; i++)
            {
                var e = dynamicEntries[i];
                int before = database.dynamicRevision;

                if (e.collider != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.collider.bounds);
                }
                else if (e.keepOut != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.keepOut.GetInflatedBounds(agentRadius));
                }
                else if (e.softAvoid != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.softAvoid.GetInflatedBounds(agentRadius));
                }
                else if (e.conditional != null)
                {
                    e.conditional.GetCurrent(agentRadius, out Bounds b, out NavFlags flags, out float costAdd);
                    flags |= NavFlags.Dynamic;
                    database.UpdateDynamicAll(e.volumeId, b, flags, costAdd);
                }

                if (database.dynamicRevision != before)
                    anyChanged = true;
            }

            if (anyChanged)
                database.RebuildDynamicHash();
        }

        void Encapsulate(ref bool init, ref Bounds b, Bounds add)
        {
            if (!init)
            {
                b = add;
                init = true;
            }
            else b.Encapsulate(add);
        }

        void CollectLayerColliders(ref bool boundsInit, ref Bounds bounds)
        {
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            foreach (var col in cols)
            {
                if (col == null || !col.enabled) continue;

                var go = col.gameObject;
                int layerBit = 1 << go.layer;

                bool isDyn = (dynamicMask.value & layerBit) != 0;
                bool isSta = (staticMask.value & layerBit) != 0;

                if (!isDyn && !isSta) continue;

                // Zone/Conditionalは別扱い
                if (go.GetComponent<KeepOutZone>() != null) continue;
                if (go.GetComponent<SoftAvoidZone>() != null) continue;
                if (go.GetComponent<ConditionalVolume>() != null) continue;

                Bounds b = col.bounds;

                // ★静的だけをBounds計算に使う（動的でBoundsが暴れるのを避ける）
                if (isSta)
                    Encapsulate(ref boundsInit, ref bounds, b);

                var flags = NavFlags.Blocked | (isDyn ? NavFlags.Dynamic : NavFlags.None);
                int id = database.AddVolume(new VolumeLite(b, flags, 0f));

                if (isDyn)
                {
                    database.RegisterDynamic(id);
                    dynamicEntries.Add(new DynamicEntry { volumeId = id, collider = col });
                }
                else
                {
                    database.RegisterStatic(id);
                }
            }
        }

        void CollectZones(ref bool boundsInit, ref Bounds bounds)
        {
            var keepOuts = FindObjectsByType<KeepOutZone>(FindObjectsSortMode.None);
            foreach (var kz in keepOuts)
            {
                if (kz == null) continue;

                Bounds b = kz.GetInflatedBounds(agentRadius);

                if (!kz.isDynamic)
                    Encapsulate(ref boundsInit, ref bounds, b);

                var flags = NavFlags.KeepOut | (kz.isDynamic ? NavFlags.Dynamic : NavFlags.None);
                int id = database.AddVolume(new VolumeLite(b, flags, 0f));

                if (kz.isDynamic)
                {
                    database.RegisterDynamic(id);
                    dynamicEntries.Add(new DynamicEntry { volumeId = id, keepOut = kz });
                }
                else database.RegisterStatic(id);
            }

            var softs = FindObjectsByType<SoftAvoidZone>(FindObjectsSortMode.None);
            foreach (var sz in softs)
            {
                if (sz == null) continue;

                Bounds b = sz.GetInflatedBounds(agentRadius);

                if (!sz.isDynamic)
                    Encapsulate(ref boundsInit, ref bounds, b);

                var flags = NavFlags.SoftAvoid | (sz.isDynamic ? NavFlags.Dynamic : NavFlags.None);
                int id = database.AddVolume(new VolumeLite(b, flags, sz.costAdd));

                if (sz.isDynamic)
                {
                    database.RegisterDynamic(id);
                    dynamicEntries.Add(new DynamicEntry { volumeId = id, softAvoid = sz });
                }
                else database.RegisterStatic(id);
            }
        }

        void CollectConditionalVolumes(ref bool boundsInit, ref Bounds bounds)
        {
            var cvs = FindObjectsByType<ConditionalVolume>(FindObjectsSortMode.None);
            foreach (var cv in cvs)
            {
                if (cv == null) continue;

                cv.GetCurrent(agentRadius, out Bounds b, out NavFlags flags, out float costAdd);
                flags |= NavFlags.Dynamic;

                int id = database.AddVolume(new VolumeLite(b, flags, costAdd));
                database.RegisterDynamic(id);

                dynamicEntries.Add(new DynamicEntry { volumeId = id, conditional = cv });
            }
        }
    }
}
