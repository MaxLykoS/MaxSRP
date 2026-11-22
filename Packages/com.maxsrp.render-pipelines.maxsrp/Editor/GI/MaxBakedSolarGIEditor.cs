using UnityEditor;
using UnityEngine;

namespace MaxSRP.EditorTools
{
    [CustomEditor(typeof(MaxBakedSolarGI)), CanEditMultipleObjects]
    public sealed class MaxBakedSolarGIEditor : Editor
    {
        private SerializedProperty m_debugMode;
        private bool m_showTODState = true;

        private void OnEnable()
        {
            m_debugMode = null;
            if (!HasLiveTargets()) return;
            m_debugMode = serializedObject.FindProperty("IndirectLightingDebug");
        }

        private bool HasLiveTargets()
        {
            // Unity may retain this Inspector while a scene unload destroys
            // one or more inspected objects. Do not create a SerializedObject
            // for that stale selection or silently edit only its survivors.
            if (!target) return false;
            var inspectedTargets = targets;
            if (inspectedTargets == null || inspectedTargets.Length == 0) return false;
            foreach (var inspectedTarget in inspectedTargets)
                if (!inspectedTarget) return false;
            return true;
        }

        public override void OnInspectorGUI()
        {
            if (!HasLiveTargets()) return;
            serializedObject.Update();
            if (m_debugMode == null)
                m_debugMode = serializedObject.FindProperty("IndirectLightingDebug");
            if (m_debugMode == null) return;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            if (HasSolarSetup())
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("日月过渡预览", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                DrawTimePreset("日落 16:49", 16f + 49f / 60f);
                DrawTimePreset("淡入 17:06", 17.1f);
                DrawTimePreset("入夜 17:23", 17f + 23f / 60f);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                DrawTimePreset("午夜 00:00", 0f);
                DrawTimePreset("淡出 06:54", 6.9f);
                DrawTimePreset("日出 07:11", 7f + 11f / 60f);
                EditorGUILayout.EndHorizontal();
                DrawTODState();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("光照分量调试", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("当前模式", m_debugMode.hasMultipleDifferentValues
                ? "多个不同状态" : ModeLabel((MaxIndirectLightingDebugMode)m_debugMode.intValue));

            DrawPreset(MaxIndirectLightingDebugMode.AllEnabled, "All Enabled",
                "恢复正常场景：直射、天光 IBL、TOD DDGI、Lightmap、自发光，以及配置允许的天空、云和空气透视。");
            DrawPreset(MaxIndirectLightingDebugMode.DirectOnly, "Direct Only",
                "仅显示方向光与点光直射，不含自发光；其余光照关闭，背景为黑，隐藏天体盘与云，不叠加空气透视。保留正常 HDR 后效。");
            DrawPreset(MaxIndirectLightingDebugMode.SkyOnly, "Sky Only",
                "仅显示天光 IBL 的漫反射和镜面反射，不含 DDGI、Lightmap 或自发光；天空背景、天体盘与云不显示，不叠加空气透视。保留正常 HDR 后效。");
            DrawPreset(MaxIndirectLightingDebugMode.DDGIOnly, "DDGI Only",
                "仅显示当前时刻的烘焙 Probe 间接光，不含其他光照或自发光；背景为黑，隐藏天体盘与云，不叠加空气透视。保留正常 HDR 后效。");
            DrawPreset(MaxIndirectLightingDebugMode.LightmapOnly, "Lightmap Only",
                "仅显示烘焙间接漫反射，不含自发光、DDGI、天光或直射；背景为黑，不叠加天空、云或空气透视。保留正常 HDR 曝光和后效。");
            DrawPreset(MaxIndirectLightingDebugMode.SkyVisOnly, "SkyVis Only",
                "仅显示原始烘焙天空可见度的 0–1 灰度，不乘材质 AO 或屏幕空间 AO，不受曝光与 Film 影响；背景为黑，隐藏天空、云和空气透视。");
            EditorGUILayout.HelpBox("Only 模式严格独显对应分量：背景为黑，隐藏天空盘与云，并跳过空气透视。Lightmap Only 等光照分量保留正常 HDR 后效；SkyVis Only 是不受 AO、曝光和 Film 影响的线性灰度遮罩。对比烘焙瑕疵时建议锁定同一曝光。", MessageType.Info);
            EditorGUILayout.Space();

            DrawPropertiesExcluding(serializedObject, "m_Script", "IndirectLightingDebug");
            if (serializedObject.ApplyModifiedProperties())
            {
                foreach (MaxBakedSolarGI component in targets)
                    component.ApplyToPipeline();
                RefreshViews();
            }
        }

        private bool HasSolarSetup()
        {
            foreach (MaxBakedSolarGI component in targets)
                if (component.Sun || component.Coarse) return true;
            return false;
        }

        private void DrawTODState()
        {
            m_showTODState = EditorGUILayout.Foldout(m_showTODState, "当前 TOD 数据（只读）", true);
            if (!m_showTODState) return;
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (MaxBakedSolarGI component in targets)
                {
                    if (targets.Length > 1)
                        EditorGUILayout.LabelField(component.name, EditorStyles.miniBoldLabel);
                    float altitude = component.SolarAltitudeDegrees;
                    EditorGUILayout.FloatField(new GUIContent("太阳高度（度）",
                        "连续 24 小时太阳轨道的真实高度；0 度为地平线。读取数据不推进 TOD、流送或历史状态。"), altitude);
                    EditorGUILayout.TextField("阶段", PhaseLabel(altitude, component.CurrentHour));
                    EditorGUILayout.FloatField(new GUIContent("月光权重（0–1）",
                        "太阳高度从 0 降至 -6 度时平滑淡入；黎明反向。同一权重只乘入月光源能量一次。"), component.MoonWeight);
                    EditorGUILayout.FloatField(new GUIContent("月光方向参考时刻（时）",
                        "实时月光使用该烘焙键对应的方向；DDGI 采样随当前 TOD 时钟更新。"), component.ResolvedMoonBakeHour);
                    Vector3 direction = component.MoonDirection;
                    EditorGUILayout.Vector3Field(new GUIContent("固定月方向（世界）",
                        "指向月亮的世界空间单位方向，不随相机旋转或夜间时钟变化。"), direction);
                    EditorGUILayout.FloatField(new GUIContent("淡入后月光标称值（lux）",
                        "Moon Illuminance Lux × 月光权重，仅为淡入后的标称值；未计月色的线性滤镜、接收面入射角和大气透射，也不验证太阳源绑定。源无效时不会产生实际照明。"), component.EffectiveMoonIlluminanceLux);
                }
            }
            foreach (MaxBakedSolarGI component in targets)
            {
                Light source = component.Sun;
                string issue = !source ? "未指定太阳源" : !source.isActiveAndEnabled ? "太阳源未启用" :
                    source.type != LightType.Directional ? "太阳源需要使用方向光" :
                    !(source.intensity > 0.0f) ? "太阳源强度必须大于 0" : null;
                if (issue != null)
                    EditorGUILayout.HelpBox((targets.Length > 1 ? component.name + "：" : "") +
                        issue + "。配置有效的方向光后才能使用日月 TOD。", MessageType.Warning);
            }
            EditorGUILayout.HelpBox("当前轨道：日落约 16:49 开始淡入，17:23 达到完整月光；黎明约 06:37 开始淡出，07:11 结束。太阳高度 0 → -6 度驱动过渡；午夜稳定，24:00 / 00:00 连续。", MessageType.Info);
        }

        private static string PhaseLabel(float altitude, float hour)
        {
            if (altitude > 0.0f) return "白天";
            if (altitude <= -6.0f) return "夜间（完整月光）";
            float clock = float.IsNaN(hour) || float.IsInfinity(hour) ? 12.0f : Mathf.Repeat(hour, 24.0f);
            return clock >= 12.0f ? "黄昏（暮光与月光淡入）" : "黎明（暮光与月光淡出）";
        }

        private void DrawTimePreset(string label, float hour)
        {
            if (!GUILayout.Button(label)) return;
            Undo.RecordObjects(targets, "Preview Moon TOD Time");
            foreach (MaxBakedSolarGI component in targets)
            {
                component.Hour = hour;
                component.ApplyToPipeline();
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);
            }
            serializedObject.Update();
            RefreshViews();
        }

        private void DrawPreset(MaxIndirectLightingDebugMode mode, string label, string tooltip)
        {
            if (GUILayout.Button(new GUIContent(label, tooltip), GUILayout.MinHeight(22f)))
                ApplyPreset(mode);
        }

        private void ApplyPreset(MaxIndirectLightingDebugMode mode)
        {
            // Button actions update the whole selection directly, including
            // when invoked outside Unity's multi-object Inspector GUI context.
            Undo.RecordObjects(targets, "Change Indirect Lighting Debug Mode");
            foreach (MaxBakedSolarGI component in targets)
            {
                component.IndirectLightingDebug = mode;
                component.SolarOnly = false;
                component.ApplyToPipeline();
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);
            }
            serializedObject.Update();
            RefreshViews();
        }

        private static void RefreshViews()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static string ModeLabel(MaxIndirectLightingDebugMode mode)
        {
            switch (mode)
            {
                case MaxIndirectLightingDebugMode.AllEnabled: return "All Enabled";
                case MaxIndirectLightingDebugMode.DirectOnly: return "Direct Only";
                case MaxIndirectLightingDebugMode.SkyOnly: return "Sky Only";
                case MaxIndirectLightingDebugMode.DDGIOnly: return "DDGI Only";
                case MaxIndirectLightingDebugMode.LightmapOnly: return "Lightmap Only";
                case MaxIndirectLightingDebugMode.SkyVisOnly: return "SkyVis Only";
                default: return "未知模式";
            }
        }
    }
}
