using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxTerrain.Editor
{
    public static class MeshSceneBuilder
    {
        public const string ScenePath = "Assets/Samples/MaxTerrain/Scenes/MaxTerrain10km.unity";
        public const string ShaderPath = "Packages/com.maxsrp.terrain/Shaders/DeltaMesh.shader";
        public static string DataDirectory => Path.Combine(Application.streamingAssetsPath, "MaxTerrain10km");
        public static void Build()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save open untitled scenes before building the comparison. Existing scenes are preserved.");
            if (!File.Exists(Path.Combine(DataDirectory, MeshHeightLayout.MetadataName))) MeshHeightBake.Build(DataDirectory);
            using (var source = new MeshHeightSource(DataDirectory)) { }
            Directory.CreateDirectory("Assets/Samples/MaxTerrain/Scenes");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var owner = new GameObject("DirectDelta vs Delta - mesh only");
                var rig = owner.AddComponent<MeshComparison>();
                rig.geometryShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                rig.directShader = rig.ResolveDirectShader();
                if (!rig.geometryShader || !rig.directShader) throw new InvalidOperationException("A terrain shader is missing.");
                rig.directCamera = MakeCamera("DirectDelta camera", new Rect(0, 0, .5f, 1), 0);
                rig.deltaCamera = MakeCamera("Delta camera", new Rect(.5f, 0, .5f, 1), 1);
                rig.SetShot(0);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
            Debug.Log("Created " + ScenePath + ". Independent mesh-only data: " + DataDirectory);
        }
        static Camera MakeCamera(string name, Rect viewport, float depth)
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.rect = viewport; camera.depth = depth; camera.fieldOfView = 60;
            camera.nearClipPlane = .3f; camera.farClipPlane = 30000;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .18f, .25f, 0);
            camera.allowHDR = false; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
            camera.useOcclusionCulling = false;
            return camera;
        }
        public static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            MeshSceneArchive.Restore(ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
        }
    }
}
