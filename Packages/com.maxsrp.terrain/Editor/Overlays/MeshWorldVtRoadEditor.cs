using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    /// <summary>只提供长方形编辑：普通 Transform 移动/水平旋转，四边与四角手柄调整长宽。</summary>
    [CustomEditor(typeof(MeshWorldVtRoad))]
    [CanEditMultipleObjects]
    public sealed class MeshWorldVtRoadEditor : UnityEditor.Editor
    {
        const string CreateMenu = "GameObject/MaxTerrain/Rectangle Road";
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("道路是贴在地形上的 XZ 长方形。移动对象调整位置，绕 Y 轴旋转调整走向；" +
                "拖动 Scene View 的边/角调整宽长。编辑框 Y 不改变路面高度，路面始终使用源地形高度。", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
            Draw("targetTerrain", "所属地形");
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
            bool editable = true;
            foreach (Object value in targets) editable &= CanEdit((MeshWorldVtRoad)value);
            if (!editable)
                EditorGUILayout.HelpBox("请在 DirectDelta 制作场景编辑道路。运行验证场景使用上次烘焙的固定记录，" +
                    "此处的作者对象不会改写发布数据。", MessageType.Info);
            using (new EditorGUI.DisabledScope(!editable))
            {
                if (targets.Length == 1) DrawTransform((MeshWorldVtRoad)target);
                else EditorGUILayout.LabelField("多选时可编辑共同材质参数；长宽手柄请单选一条道路。", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space();
                Draw("stableId", "稳定 ID"); Draw("order", "覆盖顺序");
                Draw("materialId", "材质 ID"); Draw("tint", "颜色乘数");
                Draw("opacity", "透明度"); Draw("channels", "覆盖通道");
                Draw("feather", "左右边缘羽化（米）");
                EditorGUILayout.Space();
                Draw("uvMetres", "横向/纵向纹理重复（米）");
                Draw("uvOffset", "纹理 UV 偏移"); Draw("maskTexture", "透明度遮罩");
                Draw("maskChannel", "遮罩通道"); Draw("maskUv", "遮罩 UV 缩放与偏移");
                serializedObject.ApplyModifiedProperties();
                if (GUILayout.Button("生成新的道路 ID"))
                    foreach (Object value in targets)
                    {
                        Undo.RecordObject(value, "Regenerate rectangle road ID");
                        ((MeshWorldVtRoad)value).RegenerateStableId(); EditorUtility.SetDirty(value);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(value);
                    }
            }
            if (targets.Length == 1)
            {
                var road = (MeshWorldVtRoad)target;
                if (!road.targetTerrain) EditorGUILayout.HelpBox("请指定所属地形；未指定时不会预览或烘焙这条道路。", MessageType.Warning);
                else if (road.gameObject.scene != road.targetTerrain.gameObject.scene)
                    EditorGUILayout.HelpBox("所属地形必须与道路位于同一个场景。", MessageType.Error);
                try
                {
                    var data = road.Capture();
                    if (float.IsNaN(data.feather) || float.IsInfinity(data.feather) || data.feather < 0 || data.feather >= data.width * .5f)
                        EditorGUILayout.HelpBox("左右羽化必须为有限非负数，并小于道路半宽。", MessageType.Error);
                }
                catch (Exception error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
            }
            EditorGUILayout.LabelField("静态道路参与本地烘焙。复制对象后分配新 ID；关闭对象或组件可将其排除。" +
                "Y 缩放不表示路面厚度，端部不生成曲线或路口。", EditorStyles.wordWrappedMiniLabel);
        }

        void Draw(string property, string label)
        {
            SerializedProperty value = serializedObject.FindProperty(property);
            EditorGUILayout.PropertyField(value, new GUIContent(label, value.tooltip));
        }

        static bool CanEdit(MeshWorldVtRoad road) => !road.targetTerrain ||
            road.targetTerrain.workflowMode == MeshTerrainWorkflowMode.Authoring;

        static void DrawTransform(MeshWorldVtRoad road)
        {
            Transform transform = road.transform;
            EditorGUI.BeginChangeCheck();
            Vector3 position = EditorGUILayout.Vector3Field("世界中心（Y 只定位编辑框）", transform.position);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(transform, "Move rectangle road");
                transform.position = position; Changed(transform);
            }
            EditorGUI.BeginChangeCheck();
            float yaw = EditorGUILayout.FloatField("世界 Y 旋转（度）", transform.eulerAngles.y);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(transform, "Rotate rectangle road");
                transform.rotation = Quaternion.Euler(0, yaw, 0); Changed(transform);
            }
            try
            {
                road.ReadRectangle(out _, out _, out _, out float width, out float length);
                EditorGUI.BeginChangeCheck();
                float nextWidth = EditorGUILayout.FloatField("世界宽度（米）", width);
                float nextLength = EditorGUILayout.FloatField("世界长度（米）", length);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(transform, "Resize rectangle road");
                    road.SetWorldSize(Mathf.Max(nextWidth, MinimumWidth(road)), Mathf.Max(nextLength, .01f));
                    Changed(transform);
                }
            }
            catch (Exception error)
            {
                EditorGUILayout.HelpBox(error.Message, MessageType.Error);
                if (GUILayout.Button("保持走向，复位俯仰/侧倾"))
                {
                    Undo.RecordObject(transform, "Level rectangle road");
                    transform.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0); Changed(transform);
                }
            }
        }

        void OnSceneGUI()
        {
            // Unity 针对当前对象逐次调用 Scene GUI，禁止在此读取 targets。
            // Inspector 的多对象编辑仍使用 targets；场景手柄只修改这次的 target。
            var road = target as MeshWorldVtRoad;
            if (!road || !CanEdit(road)) return;
            try
            {
                road.ReadRectangle(out Vector3 centre, out Vector3 right, out Vector3 forward, out float width, out float length);
                Color color = Handles.color; Matrix4x4 matrix = Handles.matrix; CompareFunction zTest = Handles.zTest;
                try
                {
                    // 编辑框表示 XZ 覆盖，不沿地形细分。跨坡时也能看到并抓住手柄，
                    // 但不会改变实际地形深度或把这个框当作渲染网格。
                    Handles.matrix = Matrix4x4.identity; Handles.zTest = CompareFunction.Always;
                    Handles.color = new Color(1, .68f, .08f, 1);
                    Vector3[] corners =
                    {
                        centre - right * (width * .5f) - forward * (length * .5f),
                        centre + right * (width * .5f) - forward * (length * .5f),
                        centre + right * (width * .5f) + forward * (length * .5f),
                        centre - right * (width * .5f) + forward * (length * .5f)
                    };
                    Handles.DrawSolidRectangleWithOutline(corners, new Color(1, .65f, .05f, .025f), Handles.color);
                    Handles.DrawLine(centre - forward * (length * .5f), centre + forward * (length * .5f));
                    if (Event.current.type == EventType.Repaint)
                        Handles.ArrowHandleCap(0, centre, Quaternion.LookRotation(forward, Vector3.up),
                            Mathf.Min(length * .25f, HandleUtility.GetHandleSize(centre) * 2), EventType.Repaint);
                    Handles.Label(centre + right * (width * .5f), $" {width:F2} × {length:F2} m");
                    if (road.feather > 0 && road.feather < width * .5f)
                    {
                        Handles.color = new Color(1, .85f, .25f, .65f);
                        foreach (int sign in new[] { -1, 1 })
                        {
                            Vector3 inset = centre + right * ((width * .5f - road.feather) * sign);
                            Handles.DrawDottedLine(inset - forward * (length * .5f), inset + forward * (length * .5f), 4);
                        }
                    }
                    Handles.color = new Color(1, .68f, .08f, 1);
                    // 边和角保持对边不动；默认 Transform 工具仍可移动/旋转整条道路。
                    Edge(road, centre, right, forward, width, length, 1, 0);
                    Edge(road, centre, right, forward, width, length, -1, 0);
                    Edge(road, centre, right, forward, width, length, 0, 1);
                    Edge(road, centre, right, forward, width, length, 0, -1);
                    Corner(road, centre, right, forward, width, length, -1, -1);
                    Corner(road, centre, right, forward, width, length, 1, -1);
                    Corner(road, centre, right, forward, width, length, 1, 1);
                    Corner(road, centre, right, forward, width, length, -1, 1);
                }
                finally { Handles.color = color; Handles.matrix = matrix; Handles.zTest = zTest; }
            }
            catch (System.IO.InvalidDataException) { } // 临时零缩放等由 Inspector 报错，Undo 后下一帧自动恢复。
        }

        static void Edge(MeshWorldVtRoad road, Vector3 c, Vector3 r, Vector3 f, float w, float l, int sx, int sz)
        {
            Vector3 position = c + r * (w * .5f * sx) + f * (l * .5f * sz);
            Vector3 axis = sx == 0 ? f : r;
            EditorGUI.BeginChangeCheck();
            Vector3 next = Handles.Slider(position, axis, HandleUtility.GetHandleSize(position) * .075f, Handles.DotHandleCap, 0);
            if (EditorGUI.EndChangeCheck()) Resize(road, c, r, f, w, l, sx, sz, next - position);
        }

        static void Corner(MeshWorldVtRoad road, Vector3 c, Vector3 r, Vector3 f, float w, float l, int sx, int sz)
        {
            Vector3 position = c + r * (w * .5f * sx) + f * (l * .5f * sz);
            EditorGUI.BeginChangeCheck();
            Vector3 next = Handles.Slider2D(position, Vector3.up, r, f,
                HandleUtility.GetHandleSize(position) * .065f, Handles.RectangleHandleCap, Vector2.zero);
            if (EditorGUI.EndChangeCheck()) Resize(road, c, r, f, w, l, sx, sz, next - position);
        }

        static void Resize(MeshWorldVtRoad road, Vector3 centre, Vector3 right, Vector3 forward,
            float width, float length, int sx, int sz, Vector3 delta)
        {
            float nextWidth = sx == 0 ? width : Mathf.Max(MinimumWidth(road), width + sx * Vector3.Dot(delta, right));
            float nextLength = sz == 0 ? length : Mathf.Max(.01f, length + sz * Vector3.Dot(delta, forward));
            Undo.RecordObject(road.transform, "Resize rectangle road");
            road.SetWorldSize(nextWidth, nextLength);
            road.transform.position = centre + right * ((nextWidth - width) * .5f * sx) +
                forward * ((nextLength - length) * .5f * sz);
            Changed(road.transform);
        }

        static float MinimumWidth(MeshWorldVtRoad road) => float.IsNaN(road.feather) || float.IsInfinity(road.feather)
            ? .01f : Mathf.Max(.01f, Mathf.Max(road.feather, 0) * 2 + .001f);
        static void Changed(Transform transform)
        {
            EditorUtility.SetDirty(transform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
            if (transform.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
            SceneView.RepaintAll();
        }

        [MenuItem(CreateMenu, false, 10)]
        public static void Create(MenuCommand command)
        {
            GameObject selected = command.context as GameObject;
            if (!selected) selected = Selection.activeGameObject;
            MeshComparison owner = selected ? selected.GetComponentInParent<MeshComparison>() : null;
            if (!owner)
            {
                MeshComparison candidate = null; bool multiple = false;
                foreach (MeshComparison rig in Object.FindObjectsOfType<MeshComparison>())
                    if (rig.gameObject.scene == SceneManager.GetActiveScene() && rig.workflowMode == MeshTerrainWorkflowMode.Authoring)
                    { if (candidate) multiple = true; candidate = rig; }
                if (!multiple) owner = candidate;
            }
            var go = new GameObject("Rectangle Road");
            Undo.RegisterCreatedObjectUndo(go, "Create rectangle road");
            if (owner) SceneManager.MoveGameObjectToScene(go, owner.gameObject.scene);
            var road = go.AddComponent<MeshWorldVtRoad>(); road.targetTerrain = owner;
            go.transform.localScale = new Vector3(10, 1, 100);
            if (SceneView.lastActiveSceneView) go.transform.position = SceneView.lastActiveSceneView.pivot;
            else if (selected) go.transform.position = selected.transform.position;
            Changed(go.transform); Selection.activeGameObject = go;
        }

        [MenuItem(CreateMenu, true)]
        static bool CanCreate() => !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
