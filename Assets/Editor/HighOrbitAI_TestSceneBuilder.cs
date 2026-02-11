#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HighOrbitAI
{
    public static class HighOrbitAI_TestSceneBuilder
    {
        [MenuItem("Tools/HighOrbitAI/Create Test Scene (One Shot)")]
        public static void Create()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "HighOrbitAI_TestScene";

            Random.InitState(12345);

            // -------------------------
            // Lighting
            // -------------------------
            var lightGO = new GameObject("Directional Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // -------------------------
            // Camera (FollowCam)
            // -------------------------
            var camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            var cam = camGO.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 8000f;
            camGO.transform.position = new Vector3(0f, 220f, -340f);
            camGO.transform.rotation = Quaternion.LookRotation((Vector3.zero - camGO.transform.position).normalized, Vector3.up);

            // -------------------------
            // Layers
            // -------------------------
            int obstacleLayer = 0; // Default
            int groundLayer = 1;   // TransparentFX (床専用：VCに拾わせない)
            int allyLayer = 2;     // Ignore Raycast (味方：VCに拾わせない)
            int waterLayer = 4;    // Water (敵：ターゲット用)

            // -------------------------
            // Ground (thick, huge, meaningful)
            // -------------------------
            var groundRoot = new GameObject("GroundRoot");
            groundRoot.transform.position = Vector3.zero;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(groundRoot.transform);

            // 超でかい＆厚い床：見た目と判定が安定する
            ground.transform.position = new Vector3(0f, -5f, 0f);
            ground.transform.localScale = new Vector3(2000f, 10f, 2000f);

            ground.layer = groundLayer; // TransparentFX
            GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.BatchingStatic);

            // 境界が分かる「縁」(薄い壁)
            CreateBorderWall(groundRoot.transform, groundLayer, new Vector3(0f, 25f, 1000f), new Vector3(2000f, 60f, 20f), "Border_N");
            CreateBorderWall(groundRoot.transform, groundLayer, new Vector3(0f, 25f, -1000f), new Vector3(2000f, 60f, 20f), "Border_S");
            CreateBorderWall(groundRoot.transform, groundLayer, new Vector3(1000f, 25f, 0f), new Vector3(20f, 60f, 2000f), "Border_E");
            CreateBorderWall(groundRoot.transform, groundLayer, new Vector3(-1000f, 25f, 0f), new Vector3(20f, 60f, 2000f), "Border_W");

            // 高度目盛りポール（床の意味＝高度が体感できる）
            CreateAltitudePole(new Vector3(-260f, 0f, -260f), 300f, groundLayer);
            CreateAltitudePole(new Vector3(-260f, 0f,  260f), 300f, groundLayer);
            CreateAltitudePole(new Vector3( 260f, 0f, -260f), 300f, groundLayer);
            CreateAltitudePole(new Vector3( 260f, 0f,  260f), 300f, groundLayer);

            // -------------------------
            // VolumeCollector (collect obstacles only)
            // -------------------------
            var vcGO = new GameObject("VolumeCollector");
            var vc = vcGO.AddComponent<VolumeCollector>();

            // 障害物だけ Default(0) を拾う。床(TransparentFX)は拾わない。
            vc.staticMask = (1 << obstacleLayer);
            vc.dynamicMask = 0;
            vc.agentRadius = 0.9f;
            vc.dynamicCellSize = 10f;

            // Editor実行では Awake が走らず database が null のままなので初期化
            if (vc.database == null)
            {
                vc.database = new VolumeDatabase();
                vc.database.Initialize(vc.dynamicCellSize);
            }

            // -------------------------
            // Low altitude obstacle city (dense)
            // -------------------------
            var cityRoot = new GameObject("Obstacles_LowAltitude");
            cityRoot.layer = obstacleLayer;

            int cityCount = 85;
            float cityRange = 260f;

            for (int i = 0; i < cityCount; i++)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                b.name = $"Bld_{i:00}";
                b.transform.SetParent(cityRoot.transform);

                float sx = Random.Range(10f, 30f);
                float sz = Random.Range(10f, 30f);
                float sy = Random.Range(18f, 110f);

                float x = Random.Range(-cityRange, cityRange);
                float z = Random.Range(-cityRange, cityRange);

                b.transform.position = new Vector3(x, sy * 0.5f, z);
                b.transform.localScale = new Vector3(sx, sy, sz);

                b.layer = obstacleLayer; // Default => obstacle
                GameObjectUtility.SetStaticEditorFlags(b, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            }

            // 通路（中央を空ける）
            ClearCorridor(cityRoot.transform, Vector3.zero, 45f);

            // -------------------------
            // High altitude sparse obstacles (few)
            // -------------------------
            var skyRoot = new GameObject("Obstacles_HighAltitude");
            skyRoot.layer = obstacleLayer;

            int skyCount = 14;
            for (int i = 0; i < skyCount; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = $"SkyRock_{i:00}";
                s.transform.SetParent(skyRoot.transform);

                float r = Random.Range(6f, 18f);
                float x = Random.Range(-320f, 320f);
                float z = Random.Range(-320f, 320f);
                float y = Random.Range(140f, 320f);

                s.transform.position = new Vector3(x, y, z);
                s.transform.localScale = Vector3.one * (r * 2f);

                s.layer = obstacleLayer; // obstacle
                GameObjectUtility.SetStaticEditorFlags(s, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            }

            // -------------------------
            // KeepOutZone
            // -------------------------
            var keep = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            keep.name = "KeepOutZone";
            keep.transform.position = new Vector3(40f, 35f, 10f);
            keep.transform.localScale = Vector3.one * 90f;
            keep.layer = obstacleLayer;

            var keepCol = keep.GetComponent<SphereCollider>();
            keepCol.isTrigger = true;

            var keepOut = keep.AddComponent<KeepOutZone>();
            keepOut.margin = 12f;
            keepOut.isDynamic = false;

            // -------------------------
            // Enemies (visual anchors)
            // -------------------------
            var enemyRoot = new GameObject("Enemies");

            for (int i = 0; i < 4; i++)
            {
                var e = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                e.name = $"Enemy_{i:00}";
                e.transform.SetParent(enemyRoot.transform);

                float x = Random.Range(-160f, 160f);
                float z = Random.Range(-160f, 160f);
                e.transform.position = new Vector3(x, 6f, z);
                e.transform.localScale = new Vector3(1.6f, 2.2f, 1.6f);
                e.layer = waterLayer;

                var rb = e.AddComponent<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = true;

                if (e.GetComponent<EnemyMarker>() == null) e.AddComponent<EnemyMarker>();
                TryAddThreatRelayIfExists(e, i);
            }

            // -------------------------
            // Allies
            // -------------------------
            var allyRoot = new GameObject("Allies");

            var allyA = CreateAlly_RouteAI("Ally_A", new Vector3(-120f, 35f, -40f), allyLayer, vc, allyRoot.transform, groundLayer);
            var allyB = CreateAlly_RouteAI("Ally_B", new Vector3(-90f,  70f,  10f), allyLayer, vc, allyRoot.transform, groundLayer);
            var allyC = CreateAlly_RouteAI("Ally_C", new Vector3(-150f, 55f,  50f), allyLayer, vc, allyRoot.transform, groundLayer);

            // -------------------------
            // FollowCam attach (no Input usage)
            // -------------------------
            var follow = camGO.AddComponent<FollowCam>();
            follow.targets = new Transform[] { allyA, allyB, allyC };
            follow.target = allyA;
            follow.offset = new Vector3(0f, 18f, -38f);
            follow.lookOffset = new Vector3(0f, 6f, 0f);
            follow.positionSmoothTime = 0.10f;
            follow.rotationSmoothTime = 0.08f;
            follow.autoCycle = true;
            follow.cycleSeconds = 6f;

            // -------------------------
            // Build volumes once
            // -------------------------
            vc.Build();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[HighOrbitAI] Test Scene created. Ground is thick & huge; altitude poles added.");
        }

        static void CreateBorderWall(Transform parent, int layer, Vector3 pos, Vector3 scale, string name)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cube);
            w.name = name;
            w.transform.SetParent(parent);
            w.transform.position = pos;
            w.transform.localScale = scale;
            w.layer = layer;
            GameObjectUtility.SetStaticEditorFlags(w, StaticEditorFlags.BatchingStatic);
        }

        static void CreateAltitudePole(Vector3 basePos, float height, int layer)
        {
            var root = new GameObject("AltitudePole");
            root.transform.position = basePos;

            // メインポール
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.SetParent(root.transform);
            pole.transform.position = basePos + new Vector3(0f, height * 0.5f, 0f);
            pole.transform.localScale = new Vector3(2.2f, height * 0.5f, 2.2f);
            pole.layer = layer;

            // 50mごとのリング
            for (int y = 50; y <= height; y += 50)
            {
                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = $"Mark_{y}m";
                ring.transform.SetParent(root.transform);
                ring.transform.position = basePos + new Vector3(0f, y, 0f);
                ring.transform.localScale = new Vector3(5.2f, 0.25f, 5.2f);
                ring.layer = layer;
                Object.DestroyImmediate(ring.GetComponent<Collider>()); // 見た目だけ
            }
        }

        static Transform CreateAlly_RouteAI(
            string name,
            Vector3 pos,
            int layer,
            VolumeCollector vc,
            Transform parent,
            int groundLayer)
        {
            var a = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            a.name = name;
            a.transform.SetParent(parent);
            a.transform.position = pos;
            a.transform.localScale = new Vector3(1.2f, 2.0f, 1.2f);
            a.layer = layer;

            var col = a.GetComponent<CapsuleCollider>();
            col.isTrigger = true;

            var fc = a.AddComponent<FlightController>();

            var ai = a.AddComponent<HighOrbitAI>();
            ai.volumeCollector = vc;
            ai.controller = fc;

            // ★地面判定を確実に：床レイヤー(TransparentFX)も含める
            ai.groundMask = (1 << groundLayer) | (1 << 0); // Ground + Default

            // 超軽量＆スムーズ推奨
            ai.logEnabled = false;
            ai.decisionHz = 10f;
            ai.planHz = 4f;
            ai.replanGoalDeltaXZ = 14f;

            var dbg = a.AddComponent<HighOrbitAIDebugView>();
            dbg.ai = ai;
            dbg.controller = fc;

            return a.transform;
        }

        static void ClearCorridor(Transform root, Vector3 center, float radius)
        {
            var cols = root.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                var p = c.bounds.center;
                p.y = 0f;

                var cc = center; cc.y = 0f;
                if (Vector3.Distance(p, cc) < radius)
                {
                    Object.DestroyImmediate(c.gameObject);
                }
            }
        }

        static void TryAddThreatRelayIfExists(GameObject enemy, int index)
        {
            var t = System.Type.GetType("HighOrbitAI.ThreatInfoRelay, Assembly-CSharp");
            if (t == null) return;

            var comp = enemy.AddComponent(t);

            TrySetFloat(comp, "WeaponThreat01", (index == 0) ? 0.9f : Random.Range(0.2f, 0.8f));
            TrySetFloat(comp, "Hp01", Random.Range(0.4f, 1.0f));
            TrySetFloat(comp, "LockOnThreat01", (index == 1) ? 0.95f : Random.Range(0.1f, 0.7f));
        }

        static void TrySetFloat(object obj, string propOrFieldName, float value)
        {
            if (obj == null) return;
            var t = obj.GetType();

            var p = t.GetProperty(propOrFieldName);
            if (p != null && p.CanWrite && p.PropertyType == typeof(float))
            {
                p.SetValue(obj, value);
                return;
            }

            var f = t.GetField(propOrFieldName);
            if (f != null && f.FieldType == typeof(float))
            {
                f.SetValue(obj, value);
            }
        }
    }
}
#endif
