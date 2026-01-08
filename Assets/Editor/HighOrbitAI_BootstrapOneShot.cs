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
    public float radius = 55f;
    public float height = 1.2f;
    public float angularSpeedDeg = 35f;
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
/// “AIが画面外に行かない” を最優先した追従カメラ
/// - AIの速度方向の後ろに回り込む（先読み）
/// - 速度が上がるほど距離を取る
/// - Viewportチェックで外れそうなら即補正
/// </summary>
public class SmartAICameraFollow : MonoBehaviour
{
    public Transform target;

    [Header("Distance")]
    public float minDistance = 35f;
    public float maxDistance = 90f;
    public float speedToMaxDistance = 25f;

    [Header("Height")]
    public float minHeight = 18f;
    public float maxHeight = 60f;

    [Header("Look Ahead")]
    public float lookAheadTime = 0.35f;

    [Header("Smoothing")]
    public float positionSharpness = 12f; // 大きいほど追従が速い（画面外防止）
    public float rotationSharpness = 14f;

    [Header("Viewport Safety")]
    [Tooltip("AIがこの範囲内に入るように補正する（0.0〜0.5）。0.12くらいがおすすめ")]
    public float safeMargin = 0.12f;

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

        // 速度推定
        Vector3 v = (target.position - lastTargetPos) / Mathf.Max(dt, 1e-6f);
        targetVel = Vector3.Lerp(targetVel, v, 0.35f);
        lastTargetPos = target.position;

        // 先読み位置
        Vector3 aim = target.position + targetVel * lookAheadTime;

        // “後ろ方向”を速度ベクトルから決める（止まってる時はカメラの前方向を使う）
        Vector3 forward = targetVel.sqrMagnitude > 0.1f ? targetVel.normalized : target.forward;
        Vector3 back = -forward;

        // 速度に応じて距離を可変
        float speed = targetVel.magnitude;
        float t01 = Mathf.Clamp01(speed / Mathf.Max(1f, speedToMaxDistance));
        float dist = Mathf.Lerp(minDistance, maxDistance, t01);
        float height = Mathf.Lerp(minHeight, maxHeight, t01);

        // 希望位置（速度後方 + 高さ）
        Vector3 desiredPos = aim + back * dist + Vector3.up * height;

        // まず通常追従（シャープに）
        transform.position = ExpLerp(transform.position, desiredPos, positionSharpness, dt);

        // 常にターゲットを見る
        Quaternion desiredRot = Quaternion.LookRotation((aim - transform.position).normalized, Vector3.up);
        transform.rotation = ExpSlerp(transform.rotation, desiredRot, rotationSharpness, dt);

        // 画面外防止：Viewportで安全領域に収まっているかチェックして補正
        KeepTargetInView(aim, back, dist, height, dt);
    }

    void KeepTargetInView(Vector3 aim, Vector3 back, float dist, float height, float dt)
    {
        Vector3 vp = cam.WorldToViewportPoint(aim);

        // vp.z <= 0 はカメラ背面
        bool outOfView =
            vp.z <= 0f ||
            vp.x < safeMargin || vp.x > (1f - safeMargin) ||
            vp.y < safeMargin || vp.y > (1f - safeMargin);

        if (!outOfView) return;

        // 外れそうなら「距離を増やす」「高さを増やす」「回り込みを強める」を即適用
        float extra = 1.35f;     // 距離ブースト
        float extraH = 1.15f;    // 高さブースト

        Vector3 hardPos = aim + back * (dist * extra) + Vector3.up * (height * extraH);

        // すぐ戻す（補正は強め）
        transform.position = Vector3.Lerp(transform.position, hardPos, 1f - Mathf.Exp(-30f * dt));

        Quaternion hardRot = Quaternion.LookRotation((aim - transform.position).normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, hardRot, 1f - Mathf.Exp(-30f * dt));
    }

    static Vector3 ExpLerp(Vector3 current, Vector3 target, float sharpness, float dt)
    {
        return Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
    }

    static Quaternion ExpSlerp(Quaternion current, Quaternion target, float sharpness, float dt)
    {
        return Quaternion.Slerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
    }
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

    [MenuItem("Tools/HighOrbitAI/Bootstrap Empty Scene (AI Only, Colored, Camera Lock)")]
    public static void Bootstrap()
    {
        EnsureLayer(L_STATIC);
        EnsureLayer(L_DYNAMIC);
        EnsureLayer(L_TARGET);
        EnsureLayer(L_ENEMY);

        DeleteIfExists("NavWorld");
        DeleteIfExists("Ground");
        DeleteIfExists("Target");
        DeleteIfExists("Enemy_HighOrbitAI");
        DeleteIfExists("KeepOut_Zone");
        DeleteIfExists("SoftAvoid_Zone");
        DeleteIfExists("StaticObstacles");

        Color cGround   = new Color(0.55f, 0.75f, 0.55f);
        Color cBuilding = new Color(0.18f, 0.18f, 0.20f);
        Color cKeepOut  = new Color(1.00f, 0.20f, 0.20f);
        Color cSoft     = new Color(1.00f, 0.92f, 0.25f);
        Color cTarget   = new Color(0.10f, 0.85f, 1.00f);
        Color cEnemy    = new Color(1.00f, 0.20f, 1.00f);

        // Ground
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(20, 1, 20);
        ground.layer = 0;
        ApplyColor(ground, cGround, 1f, emissive: false);

        // NavWorld
        var navWorld = new GameObject("NavWorld");
        var collector = navWorld.AddComponent<VolumeCollector>();
        collector.agentRadius = 0.5f;
        collector.dynamicCellSize = 10f;
        collector.staticMask = LayerMask.GetMask(L_STATIC);
        collector.dynamicMask = LayerMask.GetMask(L_DYNAMIC);

        // Buildings
        var obstaclesRoot = new GameObject("StaticObstacles");
        CreateBuilding(obstaclesRoot.transform, new Vector3( 20,  8,  10), new Vector3(12, 16, 12), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3(-25,  6, -15), new Vector3(14, 12, 10), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3(  0, 10,  30), new Vector3(18, 20, 14), cBuilding);
        CreateBuilding(obstaclesRoot.transform, new Vector3( 35,  5, -30), new Vector3(10, 10, 18), cBuilding);

        // KeepOut
        var keep = new GameObject("KeepOut_Zone");
        keep.transform.position = new Vector3(0, 2, 0);
        var keepCol = keep.AddComponent<BoxCollider>();
        keepCol.size = new Vector3(14, 8, 14);
        keepCol.isTrigger = true;

        var keepOut = keep.AddComponent<KeepOutZone>();
        keepOut.margin = 12f;
        keepOut.isDynamic = false;

        var keepVis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        keepVis.name = "KeepOut_Visual";
        keepVis.transform.SetParent(keep.transform, false);
        keepVis.transform.localPosition = keepCol.center;
        keepVis.transform.localScale = keepCol.size;
        Object.DestroyImmediate(keepVis.GetComponent<Collider>());
        ApplyColor(keepVis, cKeepOut, 0.18f, emissive: true);

        var keepG = keep.AddComponent<ZoneGizmo>();
        keepG.wireColor = new Color(cKeepOut.r, cKeepOut.g, cKeepOut.b, 1f);
        keepG.fillColor = new Color(cKeepOut.r, cKeepOut.g, cKeepOut.b, 0.08f);

        // SoftAvoid
        var soft = new GameObject("SoftAvoid_Zone");
        soft.transform.position = new Vector3(-10, 2, 25);
        var softCol = soft.AddComponent<BoxCollider>();
        softCol.size = new Vector3(20, 8, 20);
        softCol.isTrigger = true;

        var softAvoid = soft.AddComponent<SoftAvoidZone>();
        softAvoid.margin = 10f;
        softAvoid.costAdd = 25f;
        softAvoid.isDynamic = false;

        var softVis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        softVis.name = "SoftAvoid_Visual";
        softVis.transform.SetParent(soft.transform, false);
        softVis.transform.localPosition = softCol.center;
        softVis.transform.localScale = softCol.size;
        Object.DestroyImmediate(softVis.GetComponent<Collider>());
        ApplyColor(softVis, cSoft, 0.15f, emissive: true);

        var softG = soft.AddComponent<ZoneGizmo>();
        softG.wireColor = new Color(cSoft.r, cSoft.g, cSoft.b, 1f);
        softG.fillColor = new Color(cSoft.r, cSoft.g, cSoft.b, 0.07f);

        // Target
        var target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        target.name = "Target";
        target.layer = LayerMask.NameToLayer(L_TARGET);
        target.transform.position = new Vector3(-40, 1.2f, -40);
        target.transform.localScale = new Vector3(1.2f, 0.6f, 1.2f);
        ApplyColor(target, cTarget, 1f, emissive: true);

        var mover = target.AddComponent<AutoMovingTarget>();
        mover.center = Vector3.zero;
        mover.radius = 55f;
        mover.height = 1.2f;
        mover.angularSpeedDeg = 35f;
        mover.figureEight = true;

        // Enemy
        var enemy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        enemy.name = "Enemy_HighOrbitAI";
        enemy.layer = LayerMask.NameToLayer(L_ENEMY);
        enemy.transform.position = new Vector3(40, 200f, 40);
        var enemyCol = enemy.GetComponent<SphereCollider>();
        if (enemyCol != null) enemyCol.isTrigger = true;
        ApplyColor(enemy, cEnemy, 1f, emissive: true);

        var flight = enemy.AddComponent<FlightController>();
        flight.maxSpeed = 22f;
        flight.maxAccel = 40f;
        flight.maxYawDegPerSec = 240f;
        flight.maxClimbRate = 20f;

        var ai = enemy.AddComponent<global::HighOrbitAI.HighOrbitAI>();
        ai.player = target.transform;
        ai.volumeCollector = collector;
        ai.controller = flight;

        ai.Hcruise = 200f;
        ai.cruiseBand = 20f;
        ai.decisionHz = 15f;
        ai.descendRange = 85f;
        ai.ascendRange  = 150f;
        ai.localRadius = 70f;
        ai.localCellSize = 5f;
        ai.agentRadius = 0.5f;

        // Camera: “AIを画面内に固定” 追従
        Camera cam = Camera.main;
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera");
            cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
        }
        cam.fieldOfView = 65f;

        var camFollow = cam.GetComponent<SmartAICameraFollow>();
        if (camFollow == null) camFollow = cam.gameObject.AddComponent<SmartAICameraFollow>();
        camFollow.target = enemy.transform;

        // Build volumes
        collector.Build();

        SceneView.lastActiveSceneView?.FrameSelected();
        Debug.Log("Bootstrap completed: Camera locks AI in view.");
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
            {
                mat.SetColor("_EmissiveColor", e);
            }
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
