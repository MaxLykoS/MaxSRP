using System;
using UnityEditor;
using UnityEngine;

namespace MaxTerrain.Editor
{
    [CustomEditor(typeof(MeshWorldVtDecal))]
    [CanEditMultipleObjects]
    public sealed class MeshWorldVtDecalEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("通过 Transform 调整投影盒的位置、旋转和尺寸（Scale 即盒子的完整 XYZ 尺寸）。" +
                "贴花用于 DirectDelta 制作预览和地形烘焙；发布场景读取烘焙时的固定副本。", MessageType.Info);
            Draw("targetTerrain", "所属地形");
            Draw("stableId", "稳定 ID");
            Draw("order", "覆盖顺序");
            Draw("materialId", "材质 ID");
            Draw("tint", "颜色乘数");
            Draw("opacity", "透明度");
            Draw("channels", "覆盖通道");
            EditorGUILayout.Space();
            Draw("uvMetres", "纹理重复尺寸（米）");
            Draw("uvOffset", "纹理 UV 偏移");
            Draw("maskTexture", "透明度遮罩");
            Draw("maskChannel", "遮罩通道");
            Draw("maskUv", "遮罩 UV 缩放与偏移");
            Draw("feather", "边缘渐隐（米）");
            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var decal = (MeshWorldVtDecal)target;
                if (!decal.targetTerrain)
                    EditorGUILayout.HelpBox("请指定所属地形。未指定时，该对象不会参与预览、烘焙或实时绘制。", MessageType.Warning);
                else if (decal.targetTerrain.gameObject.scene != decal.gameObject.scene)
                    EditorGUILayout.HelpBox("所属地形必须与贴花位于同一个场景。", MessageType.Error);
                else if (decal.targetTerrain.workflowMode == MeshTerrainWorkflowMode.Published)
                    EditorGUILayout.HelpBox("当前是运行验证场景：静态内容来自上一次烘焙的固定副本。请在制作场景编辑，再重新烘焙；修改这个作者对象不会改写发布数据。", MessageType.Info);
                try
                {
                    var box = decal.Capture();
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.Vector3Field("世界投影盒尺寸（米）", box.size);
                }
                catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
            }
            if (GUILayout.Button("生成新的贴花 ID"))
            {
                foreach (UnityEngine.Object value in targets)
                {
                    Undo.RecordObject(value, "Regenerate terrain decal ID");
                    ((MeshWorldVtDecal)value).RegenerateStableId();
                    EditorUtility.SetDirty(value);
                }
            }
            EditorGUILayout.LabelField("制作场景中可编辑，烘焙后运行场景读取固定副本；不提供运行时移动贴花。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("复制对象后请分配新 ID；关闭对象或组件会将它从收集结果中排除。", EditorStyles.wordWrappedMiniLabel);
        }

        void Draw(string propertyName, string label)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip));
        }
    }
}
