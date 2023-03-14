using UnityEditor;
using UnityEngine;

namespace MaxSRP
{
    [CustomEditor(typeof(ScenePRTBaker))]
    public class ScenePRTBakerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (GUILayout.Button("Compute all meshes"))
            {
                (target as ScenePRTBaker).BakePRT();
            }
        }
    }
}