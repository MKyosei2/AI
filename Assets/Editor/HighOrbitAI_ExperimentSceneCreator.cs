// Assets/Editor/HighOrbitAI_ExperimentSceneCreator.cs
// これ1本を入れて、Unityのメニューから実験用Sceneを自動生成できます。
// メニュー: Tools/HighOrbitAI/Create Experiment Scene
//
// ※このスクリプトは「Editor」フォルダ配下に置いてください。

#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class HighOrbitAI_ExperimentSceneCreator
{
    [MenuItem("Tools/HighOrbitAI/Create Experiment Scene")]
    public static void CreateExperimentScene()
    {
        // 現在シーンが未保存なら保存確認
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        // 新規シーン
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- 環境（ライト/カメラ） ---
        var lightGO = new GameObject("Directional Light");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        camGO.transform.position = new Vector3(0, 120, -180);
        camGO.transform.rotation = Quaternion.Euler(20f, 0f, 0f);

        // --- 地面 ---
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(20, 1, 20); // 200x200m
        ground.layer = 0;

        // --- 障害物（静的） ---
        CreateWall("Wall_A", new Vector3(-35, 5, 10), new Vector3(6, 10, 60));
        CreateWall("Wall_B", new Vector3(40, 7, -25), new Vector3(8, 14, 80));
        CreatePillarRing(center: new Vector3(0, 0, 0), radius: 60f, count: 10);

        // --- VolumeCollector 作成 ---
        var vcGO = new GameObject("VolumeCollector");
        var volumeCollectorType = FindType("HighOrbitAI.VolumeCollector");
        if (volumeCollectorType == null)
            throw new Exception("HighOrbitAI.VolumeCollector が見つかりません。スクリプト名/namespaceを確認してください。");

        var vc = vcGO.AddComponent(volumeCollectorType);

        // LayerMaskの既定設定（全部見る）: staticMask/dynamicMask が存在する場合のみ設定
        TrySetLayerMask(vc, "staticMask", ~0);
        TrySetLayerMask(vc, "dynamicMask", ~0);

        // WorldBoundsの暴走を防ぐ（存在する場合のみ）
        TrySetBool(vc, "useOverrideWorldBounds", true);
        TrySetBounds(vc, "overrideWorldBounds", new Bounds(Vector3.zero, new Vector3(420, 160, 420)));
        TrySetFloat(vc, "staticWorldPaddingXZ", 50f);

        // 動的更新は軽め
        TrySetBool(vc, "enableDynamicUpdates", true);
        TrySetFloat(vc, "dynamicUpdateHz", 4f);

        // Build() があれば呼ぶ
        TryCall(vc, "Build");

        // --- Waypoints ---
        var wpRoot = new GameObject("Waypoints");
        Vector3[] wps =
        {
            new Vector3(-140, 0, -140),
            new Vector3( 140, 0, -140),
            new Vector3( 140, 0,  140),
            new Vector3(-140, 0,  140),
        };
        for (int i = 0; i < wps.Length; i++)
        {
            var w = new GameObject($"WP_{i:00}");
            w.transform.parent = wpRoot.transform;
            w.transform.position = wps[i];
        }

        // --- AI Spawn ---
        int aiCount = 18;
        for (int i = 0; i < aiCount; i++)
        {
            var aiGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            aiGO.name = $"AI_{i:00}";
            aiGO.transform.localScale = Vector3.one * 2.4f;

            float x = UnityEngine.Random.Range(-120f, 120f);
            float z = UnityEngine.Random.Range(-120f, 120f);
            aiGO.transform.position = new Vector3(x, 18f + UnityEngine.Random.Range(0f, 15f), z);

            // FlightController（型名が違う場合に備えて Reflectionで）
            var fcType = FindType("HighOrbitAI.FlightController") ?? FindType("FlightController");
            if (fcType == null)
                throw new Exception("FlightController が見つかりません。クラス名/namespaceを確認してください。");
            var fc = aiGO.AddComponent(fcType);

            // HighOrbitAI
            var haiType = FindType("HighOrbitAI.HighOrbitAI") ?? FindType("HighOrbitAI");
            if (haiType == null)
                throw new Exception("HighOrbitAI が見つかりません。クラス名/namespaceを確認してください。");
            var hai = aiGO.AddComponent(haiType);

            // 参照を設定
            TrySetRef(hai, "volumeCollector", vc);
            TrySetRef(hai, "controller", fc);

            // waypointRoot を使う（存在する場合）
            TrySetRef(hai, "waypointRoot", wpRoot.transform);

            // できるだけ軽い既定値（存在する場合だけ）
            TrySetFloat(hai, "decisionHz", 6f);
            TrySetFloat(hai, "planHz", 1.5f);
            TrySetFloat(hai, "replanGoalDeltaXZ", 18f);

            TrySetFloat(hai, "cruiseNodeSpacingXZ", 55f);
            TrySetFloat(hai, "cruiseLayerSpacingY", 55f);
            TrySetInt(hai, "cruiseSoftSamples", 0);
            TrySetFloat(hai, "cruiseBuildBudgetMs", 1.5f);

            TrySetFloat(hai, "terminalRange", 110f);
            TrySetFloat(hai, "localCellSize", 12f);
            TrySetFloat(hai, "localRadius", 90f);

            // Waypointsモード（存在する場合）
            TrySetEnum(hai, "routeMode", "Waypoints");
            TrySetBool(hai, "loopWaypoints", true);
            TrySetBool(hai, "shuffleWaypoints", false);
        }

        // --- 保存 ---
        EnsureFolder("Assets/Scenes");
        string scenePath = "Assets/Scenes/HighOrbitAI_Experiment.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        EditorSceneManager.OpenScene(scenePath);

        Debug.Log($"[HighOrbitAI] Experiment scene created: {scenePath}");
    }

    static void CreateWall(string name, Vector3 pos, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = pos;
        go.transform.localScale = size;
    }

    static void CreatePillarRing(Vector3 center, float radius, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float a = (i / (float)count) * Mathf.PI * 2f;
            var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            p.name = $"Pillar_{i:00}";
            p.transform.position = center + new Vector3(Mathf.Cos(a) * radius, 6f, Mathf.Sin(a) * radius);
            p.transform.localScale = new Vector3(6f, 12f, 6f);
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
        string folder = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folder);
    }

    static Type FindType(string fullName)
    {
        // まずType.GetType
        var t = Type.GetType(fullName);
        if (t != null) return t;

        // 全アセンブリを走査
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < asms.Length; i++)
        {
            t = asms[i].GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    static void TryCall(Component c, string methodName)
    {
        if (c == null) return;
        var m = c.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m != null && m.GetParameters().Length == 0) m.Invoke(c, null);
    }

    static bool TrySetRef(Component c, string fieldOrProp, object value)
    {
        if (c == null) return false;
        var t = c.GetType();

        var f = t.GetField(fieldOrProp, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType.IsInstanceOfType(value))
        {
            f.SetValue(c, value);
            return true;
        }

        var p = t.GetProperty(fieldOrProp, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite && p.PropertyType.IsInstanceOfType(value))
        {
            p.SetValue(c, value);
            return true;
        }
        return false;
    }

    static void TrySetLayerMask(Component c, string name, int maskValue)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(LayerMask))
        {
            LayerMask m = new LayerMask { value = maskValue };
            f.SetValue(c, m);
        }
    }

    static void TrySetBounds(Component c, string name, Bounds b)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(Bounds))
            f.SetValue(c, b);
    }

    static void TrySetBool(Component c, string name, bool v)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(bool))
            f.SetValue(c, v);
    }

    static void TrySetFloat(Component c, string name, float v)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(float))
            f.SetValue(c, v);
    }

    static void TrySetInt(Component c, string name, int v)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(int))
            f.SetValue(c, v);
    }

    static void TrySetEnum(Component c, string name, string enumName)
    {
        if (c == null) return;
        var t = c.GetType();
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType.IsEnum)
        {
            try
            {
                object val = Enum.Parse(f.FieldType, enumName);
                f.SetValue(c, val);
            }
            catch { /* ignore */ }
        }
    }
}
#endif
