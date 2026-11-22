using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxTerrain.Editor
{
    public static class MeshSurfaceSceneBuilder
    {
        public const string ScenePath = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaSurface10km.unity";
        const string WorldVtScenePath = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaWorldVt10km.unity";
        public const string HeightDirectoryName = "DirectDeltaMaladetas10km";
        public const string ControlDirectoryName = "DirectDeltaControls16MaladetasV1";
        public static string HeightDirectory => Path.Combine(Application.streamingAssetsPath,HeightDirectoryName);
        public static string ControlDirectory => Path.Combine(Application.streamingAssetsPath,ControlDirectoryName);
        public static void Build()
        {
            string sourceRoot = Environment.GetEnvironmentVariable("DIRECT_DELTA_SOURCE_PROJECT");
            if (string.IsNullOrEmpty(sourceRoot)) sourceRoot = Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Build(sourceRoot);
        }
        public static void Build(string sourceRoot)
        {
            for (int i=0;i<SceneManager.sceneCount;i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save untitled scenes first. Existing scenes are preserved.");
            if (!File.Exists(Path.Combine(HeightDirectory,MeshHeightLayout.MetadataName)))
                throw new FileNotFoundException("Bake Maladetas first using Packages/com.maxsrp.terrain/Editor/bake_maladetas.py.");
            MeshMaterialSet materials = AssetDatabase.LoadAssetAtPath<MeshMaterialSet>(MeshMaterialBuilder.AssetPath);
            if (!materials) materials = MeshMaterialBuilder.Build(sourceRoot);
            materials.Validate(); MeshMaterialBuilder.ValidatePayloads(materials);
            // 原素材顺序不是覆盖顺序。离线同步重排压缩数组及材质元数据后再绑定新控制图。
            materials = MeshMaterialOrderBuilder.Build(materials);
            // 离线去掉Gravel源法线的整体倾斜；两侧继续共用一对压缩数组，shader不增加操作。
            materials = MeshNormalPreparation.Build(sourceRoot, materials);
            if (!File.Exists(Path.Combine(ControlDirectory,MeshControlContract.MetadataName))) MeshControlBake.Build(ControlDirectory,1,HeightDirectory);
            Directory.CreateDirectory("Assets/Samples/MaxTerrain/Scenes"); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var rig = new GameObject("DirectDelta vs Delta - shared compressed Surface16").AddComponent<MeshComparison>();
                rig.terrainConfiguration = AssetDatabase.LoadAssetAtPath<MaxTerrainConfig>("Packages/com.maxsrp.terrain/DefaultConfigs/DefaultTerrain.asset");
                rig.geometryShader = AssetDatabase.LoadAssetAtPath<Shader>(MeshSceneBuilder.ShaderPath);
                rig.directShader = rig.ResolveDirectShader();
                rig.surfaceEnabled = true; rig.surfaceMaterials = materials; rig.controlRelativeDirectory = ControlDirectoryName;
                rig.datasetRelativeDirectory = HeightDirectoryName;
                rig.directCamera = Camera("DirectDelta camera",new Rect(0,0,.5f,1),0);
                rig.deltaCamera = Camera("Delta camera",new Rect(.5f,0,.5f,1),1);
                rig.sun = new GameObject("Surface sunlight").AddComponent<Light>(); rig.sun.type = LightType.Directional;
                rig.sun.transform.rotation = Quaternion.LookRotation(-new Vector3(-.45f,.8f,.3f).normalized);
                rig.sun.color = new Color(1,.97f,.9f); rig.sun.intensity = 3; rig.sun.shadows = LightShadows.None;
                RenderSettings.sun = rig.sun; rig.SetShot(3);
                EditorSceneManager.SaveScene(scene,ScenePath); AssetDatabase.SaveAssets();
            }
            finally
            { EditorSceneManager.CloseScene(scene,true); if(previous.IsValid() && previous.isLoaded)SceneManager.SetActiveScene(previous); }
            Debug.Log("Built shared 16-material compressed terrain: "+ScenePath);
        }
        static Camera Camera(string name,Rect rect,float depth)
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.rect=rect; camera.depth=depth; camera.fieldOfView=60; camera.nearClipPlane=.3f; camera.farClipPlane=30000;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.12f,.18f,.25f,0);
            camera.allowHDR=true; camera.allowMSAA=false; camera.renderingPath=RenderingPath.Forward; camera.useOcclusionCulling=false;
            return camera;
        }
        public static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            MeshSceneArchive.Restore(ScenePath);
            EditorSceneManager.OpenScene(ScenePath); EditorApplication.ExecuteMenuItem("Window/General/Game");
        }
        public static void OpenWorldVt()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            MeshSceneArchive.Restore(WorldVtScenePath);
            EditorSceneManager.OpenScene(WorldVtScenePath);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
        }
    }
}
