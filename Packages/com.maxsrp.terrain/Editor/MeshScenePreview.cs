using MaxSRP;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    public enum MeshPreviewMode { Solid, Wire, ShadedWire, LodWire }

    // Editor-only resources: navigating Scene View never changes either Game
    // camera's selection, observer, Clipmap, materials or command buffers.
    public sealed class MeshScenePreview : IDisposable
    {
        const int Batch = 500;
        readonly Matrix4x4[] matrices = new Matrix4x4[Batch];
        readonly Vector4[] patches = new Vector4[Batch], parameters = new Vector4[Batch];
        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        public MeshHeightSource Source { get; private set; }
        public MeshCdlod Geometry { get; private set; }
        public MeshHeightClipmap Clipmap { get; private set; }
        public Material Material { get; private set; }
        public MeshMaterialResident Materials { get; private set; }
        public MeshResidentControlMap ResidentControls { get; private set; }
        public MeshControlMap Controls { get; private set; }
        readonly MeshComparison comparison;
        MeshSurfaceOverlays overlays;
        public MeshWorldVtOverlaySet OverlaySet { get; }
        public bool LiveOverlays { get; }
        public string OverlayError { get; private set; }
        public bool UsesClipmap { get; } // Historical name: selects Delta ID controls, heights always use Clipmap.
        public int Draws { get; private set; }
        public long HeightBytes => Clipmap.GpuBytes;

        public MeshScenePreview(MeshComparison comparison, bool useClipmap)
        {
            if (!comparison) throw new ArgumentNullException(nameof(comparison));
            Shader shader = useClipmap ? comparison.geometryShader : comparison.ResolveDirectShader();
            if (!shader) throw new ArgumentException("The selected terrain preview shader is missing.", nameof(comparison));
            this.comparison = comparison;
            OverlaySet = comparison.EffectiveOverlaySet;
            LiveOverlays = comparison.UsesLiveOverlays;
            UsesClipmap = useClipmap;
            try
            {
                Source = new MeshHeightSource(comparison.DatasetDirectory);
                Geometry = comparison.CreateGeometry(delta: useClipmap);
                Material = new Material(shader)
                { name = "Independent terrain Scene View preview", enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
                if (Material.FindPass("MeshWire") != 1 || Material.FindPass("MeshDepth") != 2)
                    throw new InvalidOperationException("The mesh shader needs its Scene View wire and depth passes.");
                Clipmap = new MeshHeightClipmap(Source);
                if (comparison.surfaceEnabled)
                {
                    string directory = System.IO.Path.Combine(Application.streamingAssetsPath,comparison.EffectiveControlDirectory);
                    MeshControlBake.ValidateMaterialContract(directory,comparison.EffectiveMaterials);
                    Materials = new MeshMaterialResident(comparison.EffectiveMaterials);
                    if (useClipmap) Controls = new MeshControlMap(directory);
                    else ResidentControls = new MeshResidentControlMap(directory);
                    Material.EnableKeyword("DD_SURFACE_ON");
                    if (comparison.UsesLiveOverlays)
                        overlays = new MeshSurfaceOverlays(comparison.EffectiveOverlaySet,
                            comparison.CollectWorldVtDecals, comparison.CollectWorldVtRoads,
                            immutable: comparison.workflowMode == MeshTerrainWorkflowMode.Published);
                }
                for (int i = 0; i < Batch; ++i) matrices[i] = Matrix4x4.identity;
            }
            catch { Dispose(); throw; }
        }

        public void Prepare(Camera lodCamera, MeshPreviewMode mode)
        {
            if (!lodCamera) throw new ArgumentNullException(nameof(lodCamera));
            try
            {
                Vector3 observer = lodCamera.transform.position;
                Geometry.Select(observer, GeometryUtility.CalculateFrustumPlanes(lodCamera));
                Clipmap.UpdateCenter(new Vector2(observer.x, observer.z));
                Geometry.ValidateClipCoverage(Clipmap.Origins); Clipmap.Bind(Material);
                if (Materials != null)
                {
                    Materials.Bind(Material);
                    if (UsesClipmap) { Controls.UpdateCenter(new Vector2(observer.x,observer.z)); Controls.Bind(Material); }
                    else ResidentControls.Bind(Material);
                    Material.SetInt("_DDSurfaceDebug",comparison.surfaceDebug);
                    bool hex = comparison.workflowMode == MeshTerrainWorkflowMode.Published && comparison.publication
                        ? comparison.publication.hexTiling : comparison.hexTiling;
                    Material.SetFloat("_DDHexTiling", hex ? 1 : 0);
                    if (comparison.sun)
                    {
                        Material.SetVector("_DDLightDirection",-comparison.sun.transform.forward);
                        Color color = comparison.sun.color.linear*comparison.sun.intensity;
                        Material.SetColor("_DDLightColor",new Color(color.r,color.g,color.b,1));
                    }
                }
                Material.SetVector("_MeshObserver", Geometry.ObserverParameters(observer));
                Material.SetFloat("_MeshHeightScale", Source.Metadata.heightScale);
                Material.SetInt("_MeshDebug", mode == MeshPreviewMode.LodWire ? 1 : 0);
                // Scene View 拥有独立实时覆盖资源；编辑 Transform 可立即看见，
                // 不启动 Delta VT，不读取含旧装饰的缓存，也不改变 Game 相机状态。
                try { overlays?.Bind(Material); OverlayError = null; }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.IO.InvalidDataException)
                {
                    // 拖动 Scale 时可能暂时经过零尺寸/羽化超界。继续显示基础地形，
                    // 下一次合法编辑或 Undo 会重新绑定；不把作者预览锁死到 Retry。
                    Material.DisableKeyword("DD_LIVE_OVERLAYS");
                    OverlayError = error.Message;
                }
                Material.SetColor("_MeshWireColor", mode == MeshPreviewMode.Wire ? new Color(.9f,.94f,1,1) : new Color(.015f,.02f,.025f,1));
            }
            catch { throw; }
        }

        public void RecordGBuffer(CommandBuffer commands, Camera lodCamera, MeshPreviewMode mode)
        {
            // The host owns the four MRT attachments and command-buffer lifetime.
            // Always supply a complete solid GBuffer, even for a wire overlay;
            // depth without valid material attributes cannot be lit correctly.
            Prepare(lodCamera, mode); Draws = 0;
            commands.SetWireframe(false);
            DrawPass(commands, 0);
        }

        public void RecordOverlay(CommandBuffer commands, MeshPreviewMode mode)
        {
            if (mode == MeshPreviewMode.Solid) return;
            commands.SetWireframe(true);
            DrawPass(commands, 1);
            commands.SetWireframe(false);
        }

        public void RecordShadow(CommandBuffer commands, Camera observer, Matrix4x4 shadowView,
            Matrix4x4 shadowProjection, Plane[] shadowPlanes)
        {
            int pass = Material.FindPass("MaxTerrainShadow");
            if (pass < 0) throw new InvalidOperationException("The terrain shadow pass is missing.");
            // Light-frustum selection is independent of the Scene camera's
            // visible patch list. GBuffer recording reselects the camera view.
            Geometry.Select(observer.transform.position, shadowPlanes);
            // The bridge provides the texture-target GPU projection already.
            properties.SetMatrix("_MaxTerrainShadowVP", shadowProjection * shadowView);
            DrawPass(commands, pass);
        }

        void DrawPass(CommandBuffer commands, int pass)
        {
            for (int first = 0; first < Geometry.Selections.Count; first += Batch)
            {
                int count = Math.Min(Batch, Geometry.Selections.Count - first);
                for (int i = 0; i < count; ++i)
                { var selection = Geometry.Selections[first+i]; patches[i] = selection.Patch; parameters[i] = selection.Parameters; }
                properties.SetVectorArray("_MeshPatch", patches); properties.SetVectorArray("_MeshParams", parameters);
                commands.DrawMeshInstanced(Geometry.Patch, 0, Material, pass, matrices, count, properties); Draws++;
            }
        }

        public void Dispose()
        {
            overlays?.Dispose(); overlays = null;
            Materials?.Dispose(); Materials = null; ResidentControls?.Dispose(); ResidentControls = null; Controls?.Dispose(); Controls = null;
            Clipmap?.Dispose(); Clipmap = null;
            Geometry?.Dispose(); Geometry = null; Source?.Dispose(); Source = null;
            if (Material) Object.DestroyImmediate(Material); Material = null;
        }
    }

    [InitializeOnLoad]
    public static class MeshSceneView
    {
        const string Key = "MaxTerrain.SceneView.";
        static readonly string[] modes = { "Solid", "Wire", "Shaded Wire", "LOD Wire" };
        static readonly PreviewParticipant participant = new PreviewParticipant();
        static MeshScenePreview preview;
        static MeshComparison owner;
        static string error;
        static bool recording;
        static double lastCleanup;
        public static bool Visible { get => SessionState.GetBool(Key+"Visible", true); set => SessionState.SetBool(Key+"Visible", value); }
        public static bool UseClipmap { get => SessionState.GetBool(Key+"Clipmap", true); set => SessionState.SetBool(Key+"Clipmap", value); }
        public static bool GameCameraLod { get => SessionState.GetBool(Key+"GameLod", false); set => SessionState.SetBool(Key+"GameLod", value); }
        public static MeshPreviewMode Mode { get => (MeshPreviewMode)SessionState.GetInt(Key+"Mode", 3); set => SessionState.SetInt(Key+"Mode", (int)value); }
        public static int LastPatchCount => preview == null ? 0 : preview.Geometry.Selections.Count;
        public static int LastDrawCount => preview == null ? 0 : preview.Draws;

        static MeshSceneView()
        {
            MaxRenderBridge.Register(participant);
            SceneView.duringSceneGui += DuringSceneGui;
            EditorApplication.update += CleanupClosedViews;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        static MeshComparison FindComparison()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            MeshComparison fallback = null;
            foreach (var item in Object.FindObjectsOfType<MeshComparison>())
                if (item.enabled && item.gameObject.activeInHierarchy && item.gameObject.scene.isLoaded)
                { if (item.gameObject.scene == active) return item; fallback = item; }
            return fallback;
        }

        static bool EnsurePreview(MeshComparison comparison)
        {
            bool clipmap = comparison.workflowMode == MeshTerrainWorkflowMode.Authoring ? false : UseClipmap;
            if (owner != comparison || preview != null && (preview.UsesClipmap != clipmap ||
                preview.OverlaySet != comparison.EffectiveOverlaySet || preview.LiveOverlays != comparison.UsesLiveOverlays)) Release();
            owner = comparison;
            if (preview != null) return true;
            if (!string.IsNullOrEmpty(error)) return false;
            try { preview = new MeshScenePreview(comparison, clipmap); return true; }
            catch (Exception exception) { error = exception.Message; Debug.LogException(exception); return false; }
        }

        static Camera Observer(Camera camera, MeshComparison comparison) =>
            GameCameraLod && comparison.directCamera ? comparison.directCamera : camera;

        static MeshPreviewMode ModeFor(Camera camera, MeshComparison comparison)
        {
            if (comparison.workflowMode == MeshTerrainWorkflowMode.Authoring) return MeshPreviewMode.Solid;
            foreach (SceneView view in SceneView.sceneViews)
                if (view && view.camera == camera)
                {
                    if (view.cameraMode.drawMode == DrawCameraMode.Wireframe) return MeshPreviewMode.Wire;
                    if (view.cameraMode.drawMode == DrawCameraMode.TexturedWire) return MeshPreviewMode.ShadedWire;
                    break;
                }
            return Mode;
        }

        static void PrepareCamera(Camera camera)
        {
            if (!camera || camera.cameraType != CameraType.SceneView || recording || !Visible || EditorApplication.isCompiling) return;
            MeshComparison comparison = FindComparison();
            if (!comparison) { Release(); return; }
            if (!EnsurePreview(comparison)) return;
            recording = true;
            try
            {
                preview.Prepare(Observer(camera, comparison), ModeFor(camera, comparison));
            }
            catch (Exception exception) { Release(); owner = comparison; error = exception.Message; Debug.LogException(exception); }
            finally { recording = false; }
        }

        // This Editor participant is restricted to SceneView. Game cameras are
        // registered by the runtime terrain and retain independent resources.
        sealed class PreviewParticipant : IMaxRenderParticipant, IMaxRenderOverlayParticipant, IMaxRenderDiagnosticParticipant
        {
            MeshPreviewMode lastMode;
            public bool UsesMaterialDiagnostics => owner &&
                (!owner.surfaceEnabled || owner.surfaceDebug != 0 || lastMode == MeshPreviewMode.LodWire);
            public bool RendersCamera(Camera camera) => camera && camera.cameraType == CameraType.SceneView &&
                Visible && !EditorApplication.isCompiling && FindComparison();
            public void PrepareCamera(ScriptableRenderContext context, Camera camera)
            {
                MeshSceneView.PrepareCamera(camera);
                if (owner) lastMode = ModeFor(camera, owner);
            }
            public void RecordGBuffer(CommandBuffer commands, Camera camera)
            {
                if (preview != null && owner)
                    preview.RecordGBuffer(commands, Observer(camera, owner), ModeFor(camera, owner));
            }
            public void RecordOverlay(CommandBuffer commands, Camera camera)
            {
                if (preview != null && owner) preview.RecordOverlay(commands, ModeFor(camera, owner));
            }
            public void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 shadowView,
                Matrix4x4 shadowProjection, Plane[] shadowPlanes)
            {
                if (preview != null && owner)
                    preview.RecordShadow(commands, Observer(camera, owner), shadowView, shadowProjection, shadowPlanes);
            }
            // Editor preview does not claim an unimplemented previous CDLOD
            // state. GBuffer1.a=0 makes the host reject its temporal history.
            public void RecordMotion(CommandBuffer commands, Camera camera) { }
            public void EndCamera(ScriptableRenderContext context, Camera camera) { }
        }

        static void DuringSceneGui(SceneView view)
        {
            MeshComparison comparison = FindComparison();
            if (!comparison) return;
            // Frame once per Scene window/session. This makes a fresh Scene View
            // useful without forcing the game cameras to move or entering Play.
            string framed = Key+"Framed."+view.GetInstanceID();
            if (Visible && !SessionState.GetBool(framed, false) && EnsurePreview(comparison))
            { SessionState.SetBool(framed, true); FrameNear(view, comparison); }
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(8, 8, Mathf.Max(300, Mathf.Min(view.position.width-24, 790)), 100), GUI.skin.box);
            EditorGUI.BeginChangeCheck();
            GUILayout.BeginHorizontal();
            Visible = GUILayout.Toggle(Visible, "MaxTerrain", GUILayout.Width(132));
            if (comparison.surfaceEnabled)
            {
                GUILayout.Label("IDs", GUILayout.Width(30));
                if (comparison.workflowMode == MeshTerrainWorkflowMode.Authoring)
                    GUILayout.Label("DirectDelta / Resident IDs", GUILayout.Width(165));
                else UseClipmap = EditorGUILayout.Popup(UseClipmap ? 0 : 1, new[] { "Delta / ID Clipmap", "DirectDelta / Resident IDs" }, GUILayout.Width(165)) == 0;
            }
            GUILayout.Label("LOD", GUILayout.Width(30));
            GameCameraLod = EditorGUILayout.Popup(GameCameraLod ? 1 : 0, new[] { "Scene camera", "Game camera mesh" }, GUILayout.Width(155)) == 1;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Mode = (MeshPreviewMode)GUILayout.Toolbar((int)Mode, modes);
            if (GUILayout.Button("Near", GUILayout.Width(48)) && EnsurePreview(comparison)) FrameNear(view, comparison);
            if (GUILayout.Button("World", GUILayout.Width(52))) view.LookAt(new Vector3(5000,250,5000), Quaternion.Euler(65,0,0), 10000);
            GUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck()) { Release(); SceneView.RepaintAll(); }
            if (!string.IsNullOrEmpty(error))
            {
                GUILayout.BeginHorizontal(); GUILayout.Label(error);
                if (GUILayout.Button("Retry", GUILayout.Width(50))) { Release(); SceneView.RepaintAll(); }
                GUILayout.EndHorizontal();
            }
            else if (preview != null && !string.IsNullOrEmpty(preview.OverlayError))
                GUILayout.Label("Overlay preview: " + preview.OverlayError);
            else if (preview != null)
                GUILayout.Label("Scene only | patches " + LastPatchCount + " | draws " + LastDrawCount + " (surface/depth + wire) | height " + (preview.HeightBytes/1048576f).ToString("F2") + " MiB");
            else GUILayout.Label("Editor preview; Game View keeps its independent cameras and resources.");
            GUILayout.EndArea(); Handles.EndGUI();
        }

        static void FrameNear(SceneView view, MeshComparison comparison)
        {
            Vector3 origin = comparison.directCamera ? comparison.directCamera.transform.position : new Vector3(1200,0,1400);
            origin.x = Mathf.Clamp(origin.x, 100, 9900); origin.z = Mathf.Clamp(origin.z, 100, 9900);
            origin.y = preview.Source.SampleMeters(origin.x, origin.z, 0);
            // LookAt takes view SIZE, not eye distance. Keep this close enough
            // for individual 1m triangles to remain readable in Scene View.
            view.LookAt(origin + new Vector3(65,0,80), Quaternion.Euler(32,35,0), 40);
            view.Repaint();
        }

        static void CleanupClosedViews()
        {
            if (EditorApplication.timeSinceStartup-lastCleanup < .5 || preview == null) return;
            lastCleanup = EditorApplication.timeSinceStartup;
            if (!Visible || !FindComparison() || SceneView.sceneViews.Count == 0) { Release(); return; }
        }

        static void PlayModeChanged(PlayModeStateChange change) { Release(); }
        public static void Release()
        {
            preview?.Dispose(); preview = null; owner = null; error = null;
        }
        static void Shutdown()
        {
            MaxRenderBridge.Unregister(participant); SceneView.duringSceneGui -= DuringSceneGui;
            EditorApplication.update -= CleanupClosedViews; EditorApplication.playModeStateChanged -= PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown; EditorApplication.quitting -= Shutdown; Release();
        }
    }
}
