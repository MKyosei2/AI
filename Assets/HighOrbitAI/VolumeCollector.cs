using System.Collections.Generic;
using UnityEngine;

namespace HighOrbitAI
{
    /// <summary>
    /// シーンからCollider/Zoneコンポーネントを集め、VolumeDatabaseを構築する。
    /// - 静的: StaticBVHへ
    /// - 動的: SpatialHashへ
    /// </summary>
    public class VolumeCollector : MonoBehaviour
    {
        [Header("Layer masks (推奨)")]
        public LayerMask staticMask;
        public LayerMask dynamicMask;

        [Header("Common")]
        [Tooltip("AIの半径（KeepOut膨張などに使う）")]
        public float agentRadius = 0.5f;

        [Tooltip("Dynamic Hash のセルサイズ（大きいほど軽いが粗い）")]
        public float dynamicCellSize = 10f;

        [Header("Generated")]
        public VolumeDatabase database;

        // 動的登録の追跡（Bounds更新用）
        readonly List<DynamicEntry> dynamicEntries = new List<DynamicEntry>(256);

        struct DynamicEntry
        {
            public int volumeId;
            public Collider collider;
            public KeepOutZone keepOut;
            public SoftAvoidZone softAvoid;
        }

        void Awake()
        {
            Build();
        }

        public void Build()
        {
            database = new VolumeDatabase();
            database.Initialize(dynamicCellSize);

            dynamicEntries.Clear();

            // 1) Layer由来のColliderを収集
            CollectLayerColliders();

            // 2) KeepOut/SoftAvoidコンポーネントを収集（Layer不要）
            CollectZones();

            // 3) Static BVH & Dynamic Hash
            database.BuildStaticBVH();
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

                // どちらにも入ってなければ無視
                if (!isDyn && !isSta) continue;

                // Zoneコンポーネントが付いてる場合は、Zones側で処理するので二重登録しない
                if (go.GetComponent<KeepOutZone>() != null) continue;
                if (go.GetComponent<SoftAvoidZone>() != null) continue;

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
            // KeepOut
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
                else
                {
                    database.RegisterStatic(id);
                }
            }

            // SoftAvoid
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
                else
                {
                    database.RegisterStatic(id);
                }
            }
        }

        /// <summary>
        /// decisionHzなど低レートで呼ぶ。動いたDynamicのBoundsを更新→Hash再構築。
        /// （最小実装として全再構築。必要なら差分更新に拡張可能）
        /// </summary>
        public void UpdateDynamicVolumes()
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
            }

            database.RebuildDynamicHash();
        }
    }
}
