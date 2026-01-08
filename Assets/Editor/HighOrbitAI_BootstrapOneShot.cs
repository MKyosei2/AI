// Assets/HighOrbitAI_BootstrapOneShot.cs
using UnityEngine;
using HighOrbitAI;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class HighOrbitAI_BootstrapOneShot : MonoBehaviour { }

/// ---------------------------
/// Runtime helper components
/// ---------------------------

public class AutoMovingTarget : MonoBehaviour
{
    public Vector3 center = Vector3.zero;
    public float radius = 650f;          // 広大ワールド用に拡大
    public float height = 1.2f;
    public float angularSpeedDeg = 18f;  // ゆっくりにして見やすく
    public bool figureEight = true;

    float t;

    void Update()
    {
        t += Time.deltaTime * angularSpeedDeg * Mathf.Deg2Rad;

        if (figureEight)
        {
            float x = Mathf.Sin(t) * radius;
            float z = Mathf.Sin(t) * Mathf.Cos(t) * radius;
            transform.position = center + new Vector3(x, height, z);
        }
        else
        {
            float x = Mathf.Cos(t) * radius;
            float z = Mathf.Sin(t) * radius;
            transform.position = center + new Vector3(x, height, z);
        }
    }
}

/// <summary>
/// “AIが画面外に行かない”優先の追従カメラ（広大ワールド向け調整）
/// </summary>
public class SmartAICameraFollow : MonoBehaviour
{
    public Transform target;

    [Header("Distance")]
    public float minDistance = 80f;
    public float maxDistance = 220f;
    public float speedToMaxDistance = 35f;

    [Header("Height")]
    public float minHeight = 45f;
    public float maxHeight = 140f;

    [Header("Look Ahead")]
    public float lookAheadTime = 0.5f;

    [Header("Smoothing")]
    public float positionSharpness = 16f;
    public float rotationSharpness = 18f;

    [Header("Viewport Safety")]
    public float safeMargin = 0.16f;

    Camera cam;
    Vector3 lastTargetPos;
    Vector3 targetVel;
    bool inited;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;
    }

    void LateUpdate()
    {
        if (target == null || cam == null) return;

        float dt = Time.deltaTime;
        if (!inited)
        {
            lastTargetPos = target.position;
            targetVel = Vector3.zero;
            inited = true;
        }

        Vector3 v = (target.position - lastTargetPos) / Mathf.Max(dt, 1e-6f);
        targetVel = Vector3.Lerp(targetVel, v, 0.35f);
        lastTargetPos = target.position;

        Vector3 aim = target.position + targetVel * lookAheadTime;

        Vector3 forward = targetVel.sqrMagnitude > 0.1f ? targetVel.normalized : target.forward;
        Vector3 back = -forward;

        float speed = targetVel.magnitude;
        float t01 = Mathf.Clamp01(speed / Mathf.Max(1f, speedToMaxDistance));
        float dist = Mathf.Lerp(minDistance, maxDistance, t01);
        float height = Mathf.Lerp(minHeight, maxHeight, t01);

        Vector3 desiredPos = aim + back * dist + Vector3.up * height;

        transform.position = ExpLerp(transform.position, desiredPos, positionSharpness, dt);

        Quaternion desiredRot = Quaternion.LookRotation((aim - transform.position).normalized, Vector3.up);
        transform.rotation = ExpSlerp(transform.rotation, desiredRot, rotationSharpness, dt);

        KeepTargetInView(aim, back, dist, height, dt);
    }

    void KeepTargetInView(Vector3 aim, Vector3 back, float dist, float height, float dt)
    {
        Vector3 vp = cam.WorldToViewportPoint(aim);

        bool outOfView =
            vp.z <= 0f ||
            vp.x < safeMargin || vp.x > (1f - safeMargin) ||
            vp.y < safeMargin || vp.y > (1f - safeMargin);

        if (!outOfView) return;

        float extra = 1.5f;
        float extraH = 1.2f;

        Vector3 hardPos = aim + back * (dist * extra) + Vector3.up * (height * extraH);

        transform.position = Vector3.Lerp(transform.position, hardPos, 1f - Mathf.Exp(-35f * dt));

        Quaternion hardRot = Quaternion.LookRotation((aim - transform.position).normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, hardRot, 1f - Mathf.Exp(-35f * dt));
    }

    static Vector3 ExpLerp(Vector3 current, Vector3 target, float sharpness, float dt)
        => Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    static Quaternion ExpSlerp(Quaternion current, Quaternion target, float sharpness, float dt)
        => Quaternion.Slerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
}

public class ZoneGizmo : MonoBehaviour
{
    public Color wireColor = Color.white;
    public Color fillColor = new Color(1, 1, 1, 0.08f);

    void OnDrawGizmos()
    {
        var bc = GetComponent<BoxCollider>();
        if (bc == null) return;

        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = fillColor;
        Gizmos.DrawCube(bc.center, bc.size);

        Gizmos.color = wireColor;
        Gizmos.DrawWireCube(bc.center, bc.size);
    }
}

#if UNITY_EDITOR
public static class HighOrbitAI_BootstrapMenu
{
    const string L_STATIC = "StaticObstacle";
    const string L_DYNAMIC = "DynamicObstacle";
    const string L_TARGET = "Target";
    const string L_ENEMY  = "Enemy";

    [MenuItem("Tools/HighOrbitAI/Bootstrap Huge World (AI Only)")]
    public static void Bootstrap()
    {
        EnsureLayer(L_STATIC);
        EnsureLayer(L_DYNAMIC);
        EnsureLayer(L_TARGET);
        EnsureLayer(L_ENEMY);

        DeleteIfExists("NavWorld");
        DeleteIfExists("WorldRoot");
        DeleteIfExists("Target");
        DeleteIfExists("Enemy_HighOrbitAI");
        DeleteIfExists("Main Camera"); // 作り直してOKにする
        DeleteIfExists("Directional Light");

        // 色
        Color cGround   = new Color(0.55f, 0.75f, 0.55f);
        Color cRoad     = new Color(0.20f, 0.22f, 0.24f);
        Color cGrid     = new Color(0.85f, 0.85f, 0.85f);
        Color cBuilding = new Color(0.18f, 0.18f, 0.20f);
        Color cKeepOut  = new Color(1.00f, 0.20f, 0.20f);
        Color cSoft     = new Color(1.00f, 0.92f, 0.25f);
        Color cTarget   = new Color(0.10f, 0.85f, 1.00f);
        Color cEnemy    = new Color(1.00f, 0.20f, 1.00f);
        Color cLandmark = new Color(0.55f, 0.25f, 1.00f);

        var root = new GameObject("WorldRoot");

        // ---- ライト（視認性UP）
        var lightGo = new GameObject("Directional Light");
        var dl = lightGo.AddComponent<Light>();
        dl.type = LightType.Directional;
        dl.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // ---- 巨大地面（タイル敷きで“広い”を演出）
        // 1タイル=200m四方程度。9x9 で約1.8km四方。
        int tiles = 9;
        float tileScale = 20f;        // Plane(10x10) * 20 = 200m
        float tileSize = 10f * tileScale;
        float half = (tiles * tileSize) * 0.5f;

        var groundRoot = new GameObject("GroundTiles");
        groundRoot.transform.SetParent(root.transform);

        for (int x = 0; x < tiles; x++)
        for (int z = 0; z < tiles; z++)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
            g.transform.SetParent(groundRoot.transform);
            g.transform.localScale = new Vector3(tileScale, 1f, tileScale);
            g.transform.position = new Vector3(
                (x * tileSize) - half + tileSize * 0.5f,
                0f,
                (z * tileSize) - half + tileSize * 0.5f
            );
            g.layer = 0; // Default（Blockedにしない）
            ApplyColor(g, cGround, 1f, emissive: false);
        }

        // ---- グリッド線（世界のスケールが直感で分かる）
        // 線は細いキューブで描画（軽い）
        var gridRoot = new GameObject("GridLines");
        gridRoot.transform.SetParent(root.transform);

        int gridCount = 21;      // 0〜±1000m を 100m刻みで
        float gridSpan = 2000f;  // 線の長さ
        float gridStep = 100f;

        for (int i = -gridCount/2; i <= gridCount/2; i++)
        {
            float p = i * gridStep;

            // X方向線（Z固定）
            CreateLine(gridRoot.transform,
                new Vector3(-gridSpan * 0.5f, 0.02f, p),
                new Vector3( gridSpan * 0.5f, 0.02f, p),
                (i % 5 == 0) ? 1.0f : 0.35f, // 500m毎に太く
                cGrid
            );

            // Z方向線（X固定）
            CreateLine(gridRoot.transform,
                new Vector3(p, 0.02f, -gridSpan * 0.5f),
                new Vector3(p, 0.02f,  gridSpan * 0.5f),
                (i % 5 == 0) ? 1.0f : 0.35f,
                cGrid
            );
        }

        // ---- ランドマーク塔（遠距離でも位置が分かる）
        var landmarkRoot = new GameObject("Landmarks");
        landmarkRoot.transform.SetParent(root.transform);
        CreateLandmark(landmarkRoot.transform, new Vector3( 800, 60,  800), cLandmark);
        CreateLandmark(landmarkRoot.transform, new Vector3(-800, 60,  800), cLandmark);
        CreateLandmark(landmarkRoot.transform, new Vector3( 800, 60, -800), cLandmark);
        CreateLandmark(landmarkRoot.transform, new Vector3(-800, 60, -800), cLandmark);

        // ---- NavWorld（VolumeCollector）
        var navWorld = new GameObject("NavWorld");
        navWorld.transform.SetParent(root.transform);
        var collector = navWorld.AddComponent<VolumeCollector>();
        collector.agentRadius = 0.5f;
        collector.dynamicCellSize = 12f;
        collector.staticMask = LayerMask.GetMask(L_STATIC);
        collector.dynamicMask = LayerMask.GetMask(L_DYNAMIC);

        // ---- 目立つ建物（障害物）を数個（広大でも目印になる）
        var obstaclesRoot = new GameObject("StaticObstacles");
        obstaclesRoot.transform.SetParent(root.transform);

        CreateBuilding(obstaclesRoot.transform, new Vector3( 250, 18,  120), new Vector3(40, 36, 40), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3(-320, 14, -260), new Vector3(50, 28, 35), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3(  60, 22,  420), new Vector3(60, 44, 45), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3( 520, 16, -420), new Vector3(45, 32, 55), cBuilding);

        // ---- ゾーン（禁止/回避）も大きくして見やすく
        var keep = new GameObject("KeepOut_Zone");
        keep.transform.SetParent(root.transform);
        keep.transform.position = new Vector3(0, 6, 0);
        var keepCol = keep.AddComponent<BoxCollider>();
        keepCol.size = new Vector3(120, 30, 120);
        keepCol.isTrigger = true;

        var keepOut = keep.AddComponent<KeepOutZone>();
        keepOut.margin = 40f;
        keepOut.isDynamic = false;

        var keepVis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        keepVis.name = "KeepOut_Visual";
        keepVis.transform.SetParent(keep.transform, false);
        keepVis.transform.localPosition = keepCol.center;
        keepVis.transform.localScale = keepCol.size;
        Object.DestroyImmediate(keepVis.GetComponent<Collider>());
        ApplyColor(keepVis, cKeepOut, 0.14f, emissive: true);

        var keepG = keep.AddComponent<ZoneGizmo>();
        keepG.wireColor = new Color(cKeepOut.r, cKeepOut.g, cKeepOut.b, 1f);
        keepG.fillColor = new Color(cKeepOut.r, cKeepOut.g, cKeepOut.b, 0.06f);

        var soft = new GameObject("SoftAvoid_Zone");
        soft.transform.SetParent(root.transform);
        soft.transform.position = new Vector3(-250, 6, 450);
        var softCol = soft.AddComponent<BoxCollider>();
        softCol.size = new Vector3(160, 30, 160);
        softCol.isTrigger = true;

        var softAvoid = soft.AddComponent<SoftAvoidZone>();
        softAvoid.margin = 35f;
        softAvoid.costAdd = 35f;
        softAvoid.isDynamic = false;

        var softVis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        softVis.name = "SoftAvoid_Visual";
        softVis.transform.SetParent(soft.transform, false);
        softVis.transform.localPosition = softCol.center;
        softVis.transform.localScale = softCol.size;
        Object.DestroyImmediate(softVis.GetComponent<Collider>());
        ApplyColor(softVis, cSoft, 0.12f, emissive: true);

        var softG = soft.AddComponent<ZoneGizmo>();
        softG.wireColor = new Color(cSoft.r, cSoft.g, cSoft.b, 1f);
        softG.fillColor = new Color(cSoft.r, cSoft.g, cSoft.b, 0.05f);

        // ---- Target（広い範囲で動く）
        var target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        target.name = "Target";
        target.layer = LayerMask.NameToLayer(L_TARGET);
        target.transform.position = new Vector3(-650, 1.2f, -650);
        target.transform.localScale = new Vector3(3f, 1.2f, 3f);
        ApplyColor(target, cTarget, 1f, emissive: true);

        var mover = target.AddComponent<AutoMovingTarget>();
        mover.center = Vector3.zero;
        mover.radius = 650f;
        mover.height = 1.2f;
        mover.angularSpeedDeg = 18f;
        mover.figureEight = true;

        // ---- Enemy（AI）
        var enemy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        enemy.name = "Enemy_HighOrbitAI";
        enemy.layer = LayerMask.NameToLayer(L_ENEMY);
        enemy.transform.position = new Vector3(650, 380f, 650); // 高軌道をより高く
        var enemyCol = enemy.GetComponent<SphereCollider>();
        if (enemyCol != null) enemyCol.isTrigger = true;
        enemy.transform.localScale = Vector3.one * 6f; // 見やすく大きく
        ApplyColor(enemy, cEnemy, 1f, emissive: true);

        var flight = enemy.AddComponent<FlightController>();
        flight.maxSpeed = 35f;
        flight.maxAccel = 55f;
        flight.maxYawDegPerSec = 220f;
        flight.maxClimbRate = 28f;

        var ai = enemy.AddComponent<global::HighOrbitAI.HighOrbitAI>();
        ai.player = target.transform;
        ai.volumeCollector = collector;
        ai.controller = flight;

        ai.Hcruise = 380f;
        ai.cruiseBand = 30f;
        ai.decisionHz = 15f;

        ai.descendRange = 140f;
        ai.ascendRange  = 260f;

        ai.localRadius = 120f;
        ai.localCellSize = 8f;
        ai.agentRadius = 0.8f;

        // ---- Camera
        var camGo2 = new GameObject("Main Camera");
        var cam = camGo2.AddComponent<Camera>();
        camGo2.tag = "MainCamera";
        cam.fieldOfView = 62f;
        cam.farClipPlane = 8000f;

        var camFollow = camGo2.AddComponent<SmartAICameraFollow>();
        camFollow.target = enemy.transform;

        // Build volumes
        collector.Build();

        SceneView.lastActiveSceneView?.FrameSelected();
        Debug.Log("Bootstrap completed: Huge world + grid + landmarks created.");
    }

    static void CreateLine(Transform parent, Vector3 a, Vector3 b, float thickness, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "GridLine";
        go.transform.SetParent(parent);
        Object.DestroyImmediate(go.GetComponent<Collider>());

        Vector3 mid = (a + b) * 0.5f;
        Vector3 dir = (b - a);
        float len = dir.magnitude;
        if (len < 0.001f) len = 0.001f;

        go.transform.position = mid;
        go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        go.transform.localScale = new Vector3(thickness, 0.05f, len);

        ApplyColor(go, color, 0.55f, emissive: false);
    }

    static void CreateLandmark(Transform parent, Vector3 pos, Color color)
    {
        var tower = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        tower.name = "LandmarkTower";
        tower.transform.SetParent(parent);
        tower.transform.position = new Vector3(pos.x, pos.y * 0.5f, pos.z);
        tower.transform.localScale = new Vector3(12f, pos.y * 0.5f, 12f);
        Object.DestroyImmediate(tower.GetComponent<Collider>()); // 目印なので衝突不要
        ApplyColor(tower, color, 1f, emissive: true);
    }

    static void CreateBuilding(Transform parent, Vector3 pos, Vector3 size, Color color)
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.transform.SetParent(parent);
        b.transform.position = pos;
        b.transform.localScale = size;
        b.layer = LayerMask.NameToLayer(L_STATIC);
        b.name = $"Building_{pos.x}_{pos.z}";
        ApplyColor(b, color, 1f, emissive: false);
    }

    static void DeleteIfExists(string name)
    {
        var go = GameObject.Find(name);
        if (go != null) Object.DestroyImmediate(go);
    }

    static void EnsureLayer(string layerName)
    {
        if (LayerMask.NameToLayer(layerName) != -1) return;

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layersProp = tagManager.FindProperty("layers");

        for (int i = 8; i <= 31; i++)
        {
            var sp = layersProp.GetArrayElementAtIndex(i);
            if (sp.stringValue == layerName) return;

            if (string.IsNullOrEmpty(sp.stringValue))
            {
                sp.stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                return;
            }
        }

        Debug.LogWarning($"No empty user layer slots left to create layer: {layerName}");
    }

    // -----------------------------
    // Color / Material utilities
    // -----------------------------
    static void ApplyColor(GameObject go, Color rgb, float alpha, bool emissive)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        Shader shader = FindBestShader();
        var mat = new Material(shader);

        Color c = new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(alpha));

        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color", c);

        if (emissive)
        {
            Color e = new Color(rgb.r, rgb.g, rgb.b, 1f);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.SetColor("_EmissionColor", e);
                mat.EnableKeyword("_EMISSION");
            }
            if (mat.HasProperty("_EmissiveColor"))
                mat.SetColor("_EmissiveColor", e);
        }

        if (alpha < 0.999f)
            SetupMaterialForTransparency(mat);

        r.material = mat;
    }

    static Shader FindBestShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s != null) return s;

        s = Shader.Find("Standard");
        if (s != null) return s;

        return Shader.Find("Sprites/Default");
    }

    static void SetupMaterialForTransparency(Material mat)
    {
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return;
        }

        if (mat.HasProperty("_Mode"))
        {
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else
        {
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
    }
}
#endif
