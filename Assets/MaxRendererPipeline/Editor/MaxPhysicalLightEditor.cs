using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MaxSRP.EditorTools
{
    [CustomEditor(typeof(MaxPhysicalLight)), CanEditMultipleObjects]
    public sealed class MaxPhysicalLightEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var controls = targets.Cast<MaxPhysicalLight>().ToArray();
            var lights = controls.Select(c => c.GetComponent<Light>()).ToArray();
            LightType type = lights[0].type;
            if (lights.Any(l => l.type != type))
            {
                EditorGUILayout.HelpBox("Select lights of the same type to edit photometric units together.", MessageType.Info);
                return;
            }
            if (type != LightType.Directional && type != LightType.Point)
            {
                EditorGUILayout.HelpBox("MaxSRP currently renders one directional sun and point lights. This light type is not rendered.", MessageType.Info);
                return;
            }

            string label = "Illuminance (lux)";
            if (type == LightType.Point)
            {
                EditorGUI.showMixedValue = controls.Any(c => c.PointUnit != controls[0].PointUnit);
                EditorGUI.BeginChangeCheck();
                var unit = (PointLightUnit)EditorGUILayout.EnumPopup("Intensity Unit", controls[0].PointUnit);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(controls, "Change Light Unit");
                    Undo.RecordObjects(lights, "Change Light Unit");
                    foreach (var control in controls)
                    {
                        Light light = control.GetComponent<Light>();
                        float candela = LightPhotometry.PointCandela(light);
                        control.PointUnit = unit;
                        light.intensity = unit == PointLightUnit.Lumen ? candela * (4.0f * Mathf.PI) : candela;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(control);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(light);
                    }
                }
                EditorGUI.showMixedValue = false;
                if (controls.Any(c => c.PointUnit != controls[0].PointUnit)) return;
                label = controls[0].PointUnit == PointLightUnit.Lumen ? "Luminous Flux (lm)" : "Luminous Intensity (cd)";
            }

            // Edit the native Light field: there is exactly one stored intensity.
            var lightProperties = new SerializedObject(lights);
            lightProperties.Update();
            var intensity = lightProperties.FindProperty("m_Intensity");
            EditorGUILayout.PropertyField(intensity, new GUIContent(label));
            if (intensity.floatValue < 0.0f && !intensity.hasMultipleDifferentValues) intensity.floatValue = 0.0f;
            lightProperties.ApplyModifiedProperties();

            if (type == LightType.Directional)
                EditorGUILayout.HelpBox("Light.intensity is lux above the atmosphere. Only Lighting Settings > Sun Source drives MaxSRP's sun, sky and IBL.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("This edits Light > Intensity. Inverse-square falloff uses metres; Range remains a smooth finite cutoff.", MessageType.Info);
        }

        [MenuItem("CONTEXT/Light/MaxSRP Photometric Controls")]
        private static void AddControls(MenuCommand command)
        {
            var light = (Light)command.context;
            if (!light.TryGetComponent<MaxPhysicalLight>(out _))
                Undo.AddComponent<MaxPhysicalLight>(light.gameObject);
        }
    }
}
