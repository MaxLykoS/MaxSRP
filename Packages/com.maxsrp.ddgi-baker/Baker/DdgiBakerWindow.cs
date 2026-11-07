using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP.DDGIBaker.Editor
{
    internal sealed class DdgiBakerWindow : EditorWindow
    {
        [SerializeField] private string meshPath;
        [SerializeField] private string output;
        [SerializeField] private DdgiBakeSettings settings = new DdgiBakeSettings();
        private Vector2 scroll;

        [MenuItem("MaxSRP/GI/DDGI TOD Baker")]
        private static void Open() => GetWindow<DdgiBakerWindow>("DDGI TOD Baker");

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Bakes static opaque gray Lambert geometry and a unit sun along the NativeGI solar path. " +
                "Leave NGMESH empty to use static MeshRenderers from the active scene.", MessageType.Info);
            bool supported = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 && SystemInfo.supportsRayTracing;
            if (!supported)
                EditorGUILayout.HelpBox("Restart Unity with -force-d3d12 on a DXR GPU to bake.", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            meshPath = EditorGUILayout.TextField("NGMESH (optional)", meshPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFilePanel("Open NativeGI geometry", "", "ngmesh");
                if (selected.Length > 0)
                    meshPath = selected;
            }
            EditorGUILayout.EndHorizontal();
            output = EditorGUILayout.TextField("New output directory", output);
            EditorGUILayout.LabelField("Default", "OfflineBaseline/DDGIBaker/<timestamp>");
            using (var serialized = new SerializedObject(this))
            {
                serialized.Update();
                EditorGUILayout.PropertyField(serialized.FindProperty(nameof(settings)), true);
                serialized.ApplyModifiedProperties();
            }
            if (GUILayout.Button("Fit volume to input geometry"))
                Execute(() => settings.Fit(ReadGeometry().Bounds));
            using (new EditorGUI.DisabledScope(!supported || EditorApplication.isPlayingOrWillChangePlaymode || Lightmapping.isRunning))
                if (GUILayout.Button("Bake TOD keys"))
                    Execute(() => DdgiBaker.Run(ReadGeometry(), settings, Destination()));
            EditorGUILayout.EndScrollView();
        }

        private BakeGeometry ReadGeometry() => string.IsNullOrWhiteSpace(meshPath)
            ? BakeGeometry.FromScene(SceneManager.GetActiveScene()) : BakeGeometry.Read(meshPath);

        private string Destination() => string.IsNullOrWhiteSpace(output)
            ? Path.Combine("OfflineBaseline", "DDGIBaker", DateTime.Now.ToString("yyyyMMdd-HHmmss")) : output;

        private static void Execute(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { Debug.Log("DDGI bake canceled."); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
