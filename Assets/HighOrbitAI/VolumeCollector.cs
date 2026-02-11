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
        [Tooltip("動的Volumeの更新を行うか")]
        public bool enableDynamicUpdates = true;

        [Tooltip("動的Volumeの更新頻度(Hz)。5〜10推奨。0なら毎フレーム更新。")]
        public float dynamicUpdateHz = 5f;

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
            database = new VolumeDatabase();
            database.Initialize(dynamicCellSize);
            Build();
        }

        public void Build()
        {
            database.ClearAll();

            dynamicEntries.Clear();
            CollectLayerColliders();
            CollectZones();
            CollectConditionalVolumes();

            database.BuildStaticBVH();

            // 動的が無いならハッシュも不要（ここが地味に効く）
            if (dynamicEntries.Count > 0)
                database.RebuildDynamicHash();
        }

        /// <summary>
        /// 超軽量：動的更新はここを呼ぶ（間引き込み）
        /// </summary>
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

        // 互換用：従来どおり呼ばれても動く（＝即時更新）
        public void UpdateDynamicVolumes()
        {
            if (dynamicEntries.Count == 0) return;
            UpdateDynamicVolumesImmediate();
        }

        void UpdateDynamicVolumesImmediate()
        {
            for (int i = 0; i < dynamicEntries.Count; i++)
            {
                var e = dynamicEntries[i];

                if (e.collider != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.collider.bounds);
                    continue;
                }
                if (e.keepOut != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.keepOut.GetInflatedBounds(agentRadius));
                    continue;
                }
                if (e.softAvoid != null)
                {
                    database.UpdateDynamicBounds(e.volumeId, e.softAvoid.GetInflatedBounds(agentRadius));
                    continue;
                }
                if (e.conditional != null)
                {
                    e.conditional.GetCurrent(agentRadius, out Bounds b, out NavFlags flags, out float costAdd);
                    flags |= NavFlags.Dynamic;
                    database.UpdateDynamicAll(e.volumeId, b, flags, costAdd);
                    continue;
                }
            }

            // ★ここが重いので「動的がある時だけ」「間引き済み」で呼ぶ
            database.RebuildDynamicHash();
        }

        void CollectLayerColliders()
        {
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            foreach (var col in cols)
            {
                if (!col.enabled) continue;

                var go = col.gameObject;
                int layerBit = 1 << go.layer;

                bool isDyn = (dynamicMask.value & layerBit) != 0;
                bool isSta = (staticMask.value & layerBit) != 0;

                if (!isDyn && !isSta) continue;

                if (go.GetComponent<KeepOutZone>() != null) continue;
                if (go.GetComponent<SoftAvoidZone>() != null) continue;
                if (go.GetComponent<ConditionalVolume>() != null) continue;

                Bounds b = col.bounds;
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

        void CollectZones()
        {
            var keepOuts = FindObjectsByType<KeepOutZone>(FindObjectsSortMode.None);
            foreach (var kz in keepOuts)
            {
                Bounds b = kz.GetInflatedBounds(agentRadius);
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
                Bounds b = sz.GetInflatedBounds(agentRadius);
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

        void CollectConditionalVolumes()
        {
            var cvs = FindObjectsByType<ConditionalVolume>(FindObjectsSortMode.None);
            foreach (var cv in cvs)
            {
                cv.GetCurrent(agentRadius, out Bounds b, out NavFlags flags, out float costAdd);

                flags |= NavFlags.Dynamic;

                int id = database.AddVolume(new VolumeLite(b, flags, costAdd));
                database.RegisterDynamic(id);

                dynamicEntries.Add(new DynamicEntry { volumeId = id, conditional = cv });
            }
        }
    }
}
