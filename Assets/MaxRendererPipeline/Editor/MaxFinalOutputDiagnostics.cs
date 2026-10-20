using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    // Capture actual in-flight camera textures, then the Editor's visible viewport.
    // Only CopyTexture commands are inserted into the production command buffer.
    internal static class MaxFinalOutputDiagnostics
    {
        private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/FinalOutput"));
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<Pending> Copies = new List<Pending>();
        private static double started, capturedAt, presentAfter;
        private static bool scheduled;
        private static Rect screenRect;
        private static float pixelsPerPoint;
        private static string screenCamera;
        private static RenderTexture editorLdr;
        private static TextureInfo editorLdrInfo;
        private sealed class Pending { public RenderTexture hdr, final; public CameraInfo info; }
        [Serializable] private sealed class TextureInfo
        {
            public string name, graphicsFormat, format, filter;
            public int width, height, samples;
            public bool sRGB;
        }
        [Serializable] private sealed class CameraInfo
        {
            public string file, camera, type, editorWindow;
            public TextureInfo source, destination;
            public Vector4 parameters;
            public Rect pixelRect, windowRect;
            public Vector2 gameZoom, gameTargetSize;
            public bool explicitTarget;
        }
        [Serializable] private sealed class Report
        {
            public string capturedUtc, unityVersion, api, colorSpace, scene;
            public string scope = "CopyTexture snapshots immediately after actual ColorOutput draw, before Editor processing. Linear f32 reads are sampled through an FP32 target to decode sRGB explicitly. Native destination readback is also saved. Screen pixels are captured only for the active Scene viewport after repaint; other windows must not cover it.";
            public string screenCamera;
            public Rect screenRect;
            public float pixelsPerPoint;
            public string screenError;
            public TextureInfo editorLdr;
            public string colorOutputShaderSha256;
            public List<CameraInfo> cameras = new List<CameraInfo>();
            public List<TextureInfo> editorRenderTextures = new List<TextureInfo>();
        }
        private static object Member(object instance, string name)
        {
            if (instance == null) return null;
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Flags); if (field != null) return field.GetValue(instance);
                var property = type.GetProperty(name, Flags); if (property != null) return property.GetValue(instance, null);
            }
            return null;
        }
        private static TextureInfo Info(RenderTexture rt) => rt == null ? null : new TextureInfo
        { name = rt.name, graphicsFormat = rt.graphicsFormat.ToString(), format = rt.format.ToString(), filter = rt.filterMode.ToString(),
            width = rt.width, height = rt.height, samples = rt.antiAliasing, sRGB = rt.sRGB };
        [InitializeOnLoadMethod] private static void OnLoad()
        {
            if (File.Exists(Path.Combine(Output, "request.txt"))) Begin();
        }
        [MenuItem("MaxSRP/Diagnostics/Capture Final Output And Editor")]
        private static void Begin()
        {
            Cleanup(); Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "request.txt"), "Capture real camera output and visible Scene viewport after repaint.");
            File.Delete(Path.Combine(Output, "error.txt"));
            started = EditorApplication.timeSinceStartup; capturedAt = 0; presentAfter = 0; scheduled = false; screenRect = default;
            MaxColorOutputPass.EditorOutputCapture += CopyCamera;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll();
        }
        private static RenderTexture CopyTarget(RenderTexture source)
        {
            var descriptor = source.descriptor;
            descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1;
            descriptor.enableRandomWrite = false; descriptor.useMipMap = false;
            var result = new RenderTexture(descriptor) { hideFlags = HideFlags.HideAndDontSave, name = "FinalOutputDiagnosticCopy" };
            result.filterMode = source.filterMode;
            result.wrapMode = source.wrapMode;
            if (!result.Create()) { UnityEngine.Object.DestroyImmediate(result); throw new InvalidOperationException("Cannot allocate camera output copy."); }
            return result;
        }
        private static void CopyCamera(CommandBuffer cmd, Camera camera, RenderTexture hdr, RenderTexture depth, Vector4 parameters)
        {
            if (EditorApplication.timeSinceStartup - started < 3 || Copies.Exists(p => p.info.type == camera.cameraType.ToString())) return;
            if (camera.cameraType != CameraType.SceneView && camera.cameraType != CameraType.Game) return;
            Pending pending = null;
            try
            {
                RenderTexture destination = camera.targetTexture;
                EditorWindow window = null;
                if (camera.cameraType == CameraType.SceneView)
                {
                    foreach (SceneView view in SceneView.sceneViews) if (view.camera == camera) { window = view; break; }
                    if (window != SceneView.lastActiveSceneView) return;
                }
                else
                {
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
                        if (candidate.GetType().Name == "GameView")
                        { window = candidate; if (destination == null) destination = Member(candidate, "m_TargetTexture") as RenderTexture; break; }
                }
                if (destination == null || !destination.IsCreated()) return;
                if (destination.antiAliasing != 1 || hdr.antiAliasing != 1) throw new InvalidOperationException("Diagnostic needs single-sample camera targets.");
                var info = new CameraInfo { file = camera.cameraType.ToString().ToLowerInvariant(), camera = camera.name,
                    type = camera.cameraType.ToString(), editorWindow = window != null ? window.titleContent.text : "", source = Info(hdr),
                    destination = Info(destination), parameters = parameters, pixelRect = camera.pixelRect,
                    explicitTarget = camera.targetTexture != null, windowRect = window != null ? window.position : default };
                var zoom = Member(window, "m_ZoomArea");
                var scale = Member(zoom, "scale"); if (scale is Vector2) info.gameZoom = (Vector2)scale;
                var size = Member(window, "targetSize"); if (size is Vector2) info.gameTargetSize = (Vector2)size;
                pending = new Pending { info = info };
                pending.hdr = CopyTarget(hdr); pending.final = CopyTarget(destination);
                cmd.CopyTexture(hdr, pending.hdr);
                cmd.CopyTexture(destination, pending.final);
                Copies.Add(pending); capturedAt = EditorApplication.timeSinceStartup;
            }
            catch (Exception e)
            {
                if (pending != null && !Copies.Contains(pending)) { Destroy(pending.hdr); Destroy(pending.final); }
                File.WriteAllText(Path.Combine(Output, "error.txt"), e.ToString());
                MaxColorOutputPass.EditorOutputCapture -= CopyCamera;
            }
        }
        private static void OnSceneGUI(SceneView view)
        {
            if (Event.current.type != EventType.Repaint || view != SceneView.lastActiveSceneView || Copies.Count == 0) return;
            // Scene GUI is called after the 3D image has been drawn to the window.
            Vector2 origin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            screenRect = new Rect(origin.x, origin.y, view.cameraViewport.width, view.cameraViewport.height);
            screenCamera = view.camera.name;
            // Unity has just released its temporary LDRSceneTarget after the
            // HDR -> sRGB8 -> HDR clamp round trip. Copy before the pool reuses it.
            if (editorLdr == null)
                foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
                    if (rt.name == "LDRSceneTarget" && rt.IsCreated() && rt.width == view.camera.targetTexture.width && rt.height == view.camera.targetTexture.height)
                    {
                        editorLdrInfo = Info(rt); editorLdr = CopyTarget(rt);
                        Graphics.CopyTexture(rt, editorLdr); break;
                    }
            // delayCall in this repaint can still precede the window's Present.
            // Let the image reach the screen before sampling it, without a blocking wait.
            presentAfter = EditorApplication.timeSinceStartup + .25;
            SceneView.duringSceneGui -= OnSceneGUI;
        }
        private static void Update()
        {
            if (EditorApplication.isCompiling) return;
            if (Copies.Count == 0 && EditorApplication.timeSinceStartup - started > 90)
            { File.WriteAllText(Path.Combine(Output, "error.txt"), "No active camera output copied after 90 seconds."); Cleanup(); }
            else if (!scheduled && Copies.Count > 0 && EditorApplication.timeSinceStartup > (presentAfter > 0 ? presentAfter : capturedAt + 3))
            { scheduled = true; EditorApplication.delayCall += Finish; }
        }
        private static void Finish()
        {
            EditorApplication.delayCall -= Finish;
            var report = new Report { capturedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                api = SystemInfo.graphicsDeviceType.ToString(), colorSpace = QualitySettings.activeColorSpace.ToString(),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path, screenRect = screenRect,
                screenCamera = screenCamera, pixelsPerPoint = pixelsPerPoint };
            report.editorLdr = editorLdrInfo;
            var previous = RenderTexture.active;
            try
            {
                // Read the already-presented screen FIRST: bulk GPU readbacks/file I/O
                // can otherwise display Unity's busy panel over the viewport.
                if (screenRect.width > 0 && screenRect.height > 0)
                {
                    try
                    {
                        int width = Mathf.RoundToInt(screenRect.width * pixelsPerPoint), height = Mathf.RoundToInt(screenRect.height * pixelsPerPoint);
                        var colors = InternalEditorUtility.ReadScreenPixel(screenRect.position, width, height);
                        var png = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                        try { png.SetPixels(colors); png.Apply(false, false); File.WriteAllBytes(Path.Combine(Output, "scene_screen.png"), png.EncodeToPNG()); }
                        finally { UnityEngine.Object.DestroyImmediate(png); }
                    }
                    catch (Exception e) { report.screenError = e.ToString(); }
                }
                foreach (var pending in Copies)
                {
                    report.cameras.Add(pending.info);
                    SaveLinear(pending.hdr, pending.info.file + "_hdr.f32");
                    SaveLinear(pending.final, pending.info.file + "_output.f32");
                    SaveNative(pending.final, pending.info.file + "_output_native.f32");
                }
                if (editorLdr != null)
                {
                    SaveLinear(editorLdr, "scene_editor_ldr.f32");
                    SaveNative(editorLdr, "scene_editor_ldr_native.f32");
                }
                foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
                    if (rt.IsCreated() && rt.name != "FinalOutputDiagnosticCopy") report.editorRenderTextures.Add(Info(rt));
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    report.colorOutputShaderSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Path.Combine(Application.dataPath,
                        "MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl")))).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(Output, "capture.json"), JsonUtility.ToJson(report, true));
                File.Delete(Path.Combine(Output, "request.txt"));
                Debug.Log("MaxSRP actual final output captured to " + Output);
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(Output, "error.txt"), e.ToString()); Debug.LogException(e); }
            finally { RenderTexture.active = previous; Cleanup(); }
        }
        private static void SaveLinear(RenderTexture source, string file)
        {
            var copy = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            try { Graphics.Blit(source, copy); SaveNative(copy, file); }
            finally { RenderTexture.ReleaseTemporary(copy); }
        }
        private static void SaveNative(RenderTexture source, string file)
        {
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false); pixels.Apply(false, false);
                File.WriteAllBytes(Path.Combine(Output, file), pixels.GetRawTextureData());
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
        }
        private static void Destroy(RenderTexture rt) { if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); } }
        private static void Cleanup()
        {
            MaxColorOutputPass.EditorOutputCapture -= CopyCamera;
            SceneView.duringSceneGui -= OnSceneGUI; EditorApplication.update -= Update;
            EditorApplication.delayCall -= Finish; AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
            foreach (var pending in Copies) { Destroy(pending.hdr); Destroy(pending.final); } Copies.Clear();
            Destroy(editorLdr); editorLdr = null; editorLdrInfo = null;
        }
    }
}
