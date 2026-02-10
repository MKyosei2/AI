using UnityEngine;

namespace HighOrbitAI
{
    [DisallowMultipleComponent]
    public class HighOrbitAIDebugView : MonoBehaviour
    {
        public HighOrbitAI ai;
        public FlightController controller;

        [Header("Lines")]
        public float pathLineWidth = 0.35f;
        public float velLineWidth = 0.20f;
        public float velScale = 1.8f;

        [Header("Markers")]
        public float markerScale = 2.0f;
        public float textHeight = 6f;

        LineRenderer pathLine;
        LineRenderer velLine;

        Transform mTarget;
        Transform mGoal;
        TextMesh label;

        void Reset()
        {
            ai = GetComponent<HighOrbitAI>();
            controller = GetComponent<FlightController>();
        }

        void Awake()
        {
            if (ai == null) ai = GetComponent<HighOrbitAI>();
            if (controller == null) controller = GetComponent<FlightController>();

            pathLine = CreateLine("AI_PathLine", pathLineWidth);
            velLine  = CreateLine("AI_VelLine",  velLineWidth);

            mTarget = CreateMarker("Target", new Color(0.2f, 0.9f, 1f), markerScale).transform;
            mGoal   = CreateMarker("Goal",   new Color(1f, 0.2f, 1f), markerScale).transform;

            label = CreateWorldText("AI_StateText");
        }

        void LateUpdate()
        {
            if (ai == null || controller == null) return;

            DrawPath();
            DrawVelocity();
            UpdateMarkers();
            UpdateText();
        }

        void DrawPath()
        {
            var p = controller.CurrentPath;
            int n = (p == null) ? 0 : p.Count;

            if (n <= 1)
            {
                pathLine.positionCount = 0;
                return;
            }

            pathLine.positionCount = n;
            for (int i = 0; i < n; i++)
                pathLine.SetPosition(i, p[i] + Vector3.up * 0.15f);

            if (ai.CurrentMode == HighOrbitAI.AIMode.Lane)
                SetLineColor(pathLine, new Color(0.25f, 1f, 0.35f));
            else
                SetLineColor(pathLine, new Color(1f, 0.55f, 0.15f));
        }

        void DrawVelocity()
        {
            Vector3 v = controller.Velocity;
            if (v.sqrMagnitude < 0.01f)
            {
                velLine.positionCount = 0;
                return;
            }

            velLine.positionCount = 2;
            velLine.SetPosition(0, transform.position + Vector3.up * 0.8f);
            velLine.SetPosition(1, transform.position + Vector3.up * 0.8f + v * velScale);

            SetLineColor(velLine, Color.white);
        }

        void UpdateMarkers()
        {
            mTarget.position = ai.DebugTarget + Vector3.up * 0.6f;
            mGoal.position   = ai.DebugGoal   + Vector3.up * 0.6f;
        }

        void UpdateText()
        {
            string okStr = ai.DebugLastPlanOk ? "OK" : "NG";
            string modeStr = ai.CurrentMode.ToString();
            string koStr = ai.DebugInKeepOut ? "KeepOut" : "-";

            string combat =
                (ai.DebugMelee ? "M" : "-") +
                (ai.DebugShooting ? "S" : "-") +
                (ai.DebugBoost ? "B" : "-") +
                (ai.DebugEvade ? "E" : "-");

            label.text =
                "[" + modeStr + "] " + okStr + "  " + koStr + "\n" +
                "Phase:" + ai.DebugPhase + "  Tactic:" + ai.DebugTactic + "  Band:" + ai.DebugBand + "\n" +
                "Combat(MSBE):" + combat + "\n" +
                "Profile:" + controller.CurrentProfile + " -> " + controller.TargetProfile + "\n" +
                "DistXZ:" + ai.DebugFlatDistance.ToString("0.0") + "\n" +
                "Alt:" + ai.DebugDesiredY.ToString("0.0") + "/" + ai.DebugCeilingY.ToString("0.0") + "\n" +
                ai.DebugLastPlanMessage + "\n" +
                "Speed:" + controller.Velocity.magnitude.ToString("0.0");

            label.transform.position = transform.position + Vector3.up * textHeight;

            if (Camera.main != null)
            {
                Vector3 dir = label.transform.position - Camera.main.transform.position;
                if (dir.sqrMagnitude > 0.001f)
                    label.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
        }

        LineRenderer CreateLine(string name, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 8;
            lr.numCornerVertices = 6;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            return lr;
        }

        GameObject CreateMarker(string name, Color color, float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * scale;

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                var mat = new Material(Shader.Find("Sprites/Default"));
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                r.material = mat;
            }
            return go;
        }

        TextMesh CreateWorldText(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var tm = go.AddComponent<TextMesh>();
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.15f;
            tm.fontSize = 60;
            tm.color = Color.white;
            tm.text = "";
            return tm;
        }

        void SetLineColor(LineRenderer lr, Color c)
        {
            lr.startColor = c;
            lr.endColor = c;
            if (lr.material != null)
            {
                if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", c);
                if (lr.material.HasProperty("_Color")) lr.material.SetColor("_Color", c);
            }
        }
    }
}
