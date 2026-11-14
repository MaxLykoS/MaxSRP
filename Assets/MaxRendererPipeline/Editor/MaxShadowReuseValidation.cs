#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    /// <summary>
    /// Compares full shadow updates with short-lived cached cascades on the same
    /// permanently tightened receiver footprint. The camera and sun must remain fixed.
    /// </summary>
    [InitializeOnLoad]
    public static class MaxShadowReuseValidation
    {
        [Serializable] private sealed class CascadeMetadata
        {
            public RectInt tile;
            public Matrix4x4 shadowVP, shadowView;
            public Vector3[] receiverCorners;
            public float paddingWorld, orthoWidth;
        }
        [Serializable] private sealed class CascadeComparison
        {
            public int cascade;
            public long queriedTexels, changedQueriedTexels, changedUnqueriedTexels, nonClearQueriedTexels;
            public float maximumQueriedDepthDelta;
            public int firstMismatchX = -1, firstMismatchY = -1;
            public bool passed;
        }
        [Serializable] private sealed class Comparison
        {
            public string pair;
            public bool passed;
            public int updatedCascades, reusedCascades;
            public List<CascadeComparison> cascades = new List<CascadeComparison>();
        }
        [Serializable] private sealed class Report
        {
            public string utc, unityVersion, gpu, graphicsAPI, scene, camera, reportPath, message;
            public int cameraId, cameraWidth, cameraHeight, atlasWidth, atlasHeight;
            public Vector3 position, eulerAngles;
            public bool success, baselineStable, readbackStartsAtNativeTop;
            public CascadeMetadata[] geometry;
            public List<Comparison> comparisons = new List<Comparison>();
        }
        private sealed class Frame
        {
            public float[] depth;
            public int width, height;
            public bool nativeTopFirst;
            public CascadeMetadata[] geometry;
        }

        private static Camera s_camera;
        private static Matrix4x4 s_cameraView, s_cameraProjection;
        private static int s_cameraWidth, s_cameraHeight, s_stage, s_warmup, s_reuseWaitFrames;
        private static bool s_running, s_capturing;
        private static bool? s_oldOverride;
        private static bool s_oldDiagnostics, s_oldGeometry;
        private static double s_lastFrame;
        private static Frame s_baseline;
        private static Report s_report;
        private static string s_status = "Not started.";
        private static EditorWindow s_gameView;
        private static Shader s_readbackShader;
        private static bool s_ownsReadbackShader;

        public static bool IsRunning => s_running;
        public static string Status => s_status;

        static MaxShadowReuseValidation()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
        }

        [MenuItem("MaxSRP/Validation/Shadow Reuse/GPU A-B Depth Comparison")]
        public static void RunMenu() => Begin();

        public static void Begin(int cameraInstanceId = 0)
        {
            if (s_running) throw new InvalidOperationException("Shadow depth comparison is already running.");
            if (MaxShadowCasterPass.CaptureDiagnostics || MaxShadowCasterPass.CaptureGeometry)
                throw new InvalidOperationException("Finish the active shadow diagnostic or CPU benchmark first.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run the deterministic depth comparison in Edit Mode.");
            Camera camera = cameraInstanceId == 0 ? Camera.main : EditorUtility.InstanceIDToObject(cameraInstanceId) as Camera;
            if (!camera || !camera.enabled || camera.cameraType != CameraType.Game || !camera.gameObject.scene.IsValid())
                throw new InvalidOperationException("An enabled scene Game camera is required.");
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RFloat))
                throw new InvalidOperationException("RFloat readback is unavailable on this device.");
            // Start from invalid caches; the B phase must demonstrate a real reuse.
            // Both A and B retain the accepted receiver XY culling path.
            MaxShadowCasterPass.InvalidateCaches();
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/MaxRendererPipeline/Editor/MaxShadowCullingDepthReadback.shader");
            s_ownsReadbackShader = !shader;
            if (!shader)
                shader = ShaderUtil.CreateShaderAsset(File.ReadAllText(Path.Combine(Application.dataPath,
                    "MaxRendererPipeline/Editor/MaxShadowCullingDepthReadback.shader")), false);
            if (!shader || ShaderUtil.ShaderHasError(shader))
            {
                if (shader && s_ownsReadbackShader) UnityEngine.Object.DestroyImmediate(shader);
                throw new InvalidOperationException("The shadow depth readback shader is missing or has an import error.");
            }
            s_readbackShader = shader;

            s_camera = camera;
            s_cameraView = camera.worldToCameraMatrix;
            s_cameraProjection = camera.projectionMatrix;
            s_cameraWidth = camera.pixelWidth;
            s_cameraHeight = camera.pixelHeight;
            s_oldOverride = MaxShadowCasterPass.ReuseOverride;
            s_oldDiagnostics = MaxShadowCasterPass.CaptureDiagnostics;
            s_oldGeometry = MaxShadowCasterPass.CaptureGeometry;
            s_report = new Report {
                utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                gpu = SystemInfo.graphicsDeviceName, graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                scene = camera.gameObject.scene.path, camera = camera.name, cameraId = camera.GetInstanceID(),
                cameraWidth = s_cameraWidth, cameraHeight = s_cameraHeight,
                position = camera.transform.position, eulerAngles = camera.transform.eulerAngles
            };
            s_baseline = null;
            s_stage = 0;
            s_warmup = 2;
            s_reuseWaitFrames = 0;
            s_lastFrame = EditorApplication.timeSinceStartup;
            s_running = true;
            s_capturing = false;
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.GetType().FullName == "UnityEditor.GameView") { s_gameView = window; break; }
            MaxShadowCasterPass.ReuseOverride = false;
            MaxShadowCasterPass.CaptureDiagnostics = true;
            MaxShadowCasterPass.CaptureGeometry = true;
            MaxShadowCasterPass.CpuSampleRecorded += OnShadowRendered;
            EditorApplication.update += Tick;
            s_status = "Waiting for initial baseline atlas (A).";
            Tick();
        }

        [MenuItem("MaxSRP/Validation/Shadow Reuse/Cancel GPU Comparison")]
        public static void Cancel()
        {
            if (s_running) Finish(false, "Cancelled before comparison completed.");
        }

        private static void Tick()
        {
            if (!s_running || s_capturing) return;
            if (!s_camera || EditorApplication.isPlayingOrWillChangePlaymode ||
                s_camera.pixelWidth != s_cameraWidth || s_camera.pixelHeight != s_cameraHeight ||
                !SameMatrix(s_camera.worldToCameraMatrix, s_cameraView) ||
                !SameMatrix(s_camera.projectionMatrix, s_cameraProjection))
            {
                Finish(false, "Camera or Edit Mode changed; the A/B pair is no longer comparable.");
                return;
            }
            if (EditorApplication.timeSinceStartup - s_lastFrame > 20)
            {
                Finish(false, "No target-camera shadow frame for 20 seconds. Show the Game view, then run again.");
                return;
            }
            EditorApplication.QueuePlayerLoopUpdate();
            if (s_gameView) s_gameView.Repaint();
        }

        private static void OnShadowRendered(MaxShadowCasterPass.ShadowCpuSample sample)
        {
            if (!s_running || s_capturing || !s_camera || sample.CameraId != s_camera.GetInstanceID()) return;
            s_lastFrame = EditorApplication.timeSinceStartup;
            if (sample.CacheEnabled != (s_stage == 2))
            {
                Finish(false, "Shadow A/B mode changed externally.");
                return;
            }
            if (s_warmup-- > 0) return;
            if (s_stage == 2 && sample.ReusedCascades <= 0)
            {
                if (++s_reuseWaitFrames >= 64)
                    Finish(false, "B updated every cascade for 64 target frames; the test did not observe actual shadow reuse.");
                else s_status = "Waiting for a B frame that actually reuses at least one cascade.";
                return;
            }
            s_capturing = true;
            try
            {
                Frame frame = CaptureFrame();
                if (s_stage == 0)
                {
                    s_baseline = frame;
                    s_report.geometry = frame.geometry;
                    s_report.atlasWidth = frame.width;
                    s_report.atlasHeight = frame.height;
                    s_report.readbackStartsAtNativeTop = frame.nativeTopFirst;
                    s_stage = 1;
                    s_status = "Checking baseline stability (A/A).";
                }
                else
                {
                    Comparison comparison = Compare(s_baseline, frame, s_stage == 1 ? "A/A full-update control" : "A/B short-lived shadow reuse");
                    comparison.updatedCascades = sample.UpdatedCascades;
                    comparison.reusedCascades = sample.ReusedCascades;
                    s_report.comparisons.Add(comparison);
                    if (s_stage == 1)
                    {
                        s_report.baselineStable = comparison.passed;
                        if (!comparison.passed) { Finish(false, "Baseline depth changed between unchanged A frames."); return; }
                        s_stage = 2;
                        MaxShadowCasterPass.ReuseOverride = true;
                        s_status = "Comparing reused cascades (B) with full-update baseline (A).";
                    }
                    else
                    {
                        Finish(comparison.passed, comparison.passed
                            ? "A/A stable; B reused " + sample.ReusedCascades + " cascade(s), and all queried atlas depths match exactly in A/B."
                            : "A/B differs inside the conservative receiver sampling footprint.");
                        return;
                    }
                }
                s_warmup = 2;
            }
            catch (Exception exception)
            {
                Finish(false, exception.ToString());
                Debug.LogException(exception);
            }
            finally { s_capturing = false; }
        }

        private static Frame CaptureFrame()
        {
            RenderTexture source = MaxShadowCasterPass.DiagnosticAtlas;
            if (!source || !source.IsCreated()) throw new InvalidOperationException("No initialized diagnostic atlas.");
            var frame = new Frame { width = source.width, height = source.height, geometry = new CascadeMetadata[4] };
            for (int i = 0; i < 4; ++i)
            {
                frame.geometry[i] = new CascadeMetadata {
                    tile = MaxShadowCasterPass.DiagnosticTiles[i],
                    shadowVP = Shader.GetGlobalMatrix("_ShadowVPMatrix" + i),
                    shadowView = MaxShadowCasterPass.DiagnosticShadowViews[i],
                    receiverCorners = (Vector3[])MaxShadowCasterPass.DiagnosticReceiverCorners[i].Clone(),
                    paddingWorld = MaxShadowCasterPass.DiagnosticPadding[i],
                    orthoWidth = Shader.GetGlobalFloat("_OrthoWidth" + i)
                };
                CascadeMetadata g = frame.geometry[i];
                if (g.tile.width <= 0 || g.tile.height <= 0 || g.orthoWidth <= 0 ||
                    g.tile.xMin < 1 || g.tile.yMin < 1 || g.tile.xMax >= source.width || g.tile.yMax >= source.height)
                    throw new InvalidOperationException("Incomplete shadow geometry diagnostics for cascade " + i);
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture copy = null;
            Texture2D readback = null;
            Material material = null;
            try
            {
                copy = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    { name = "Shadow reuse validation readback", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                if (!copy.Create()) throw new InvalidOperationException("Cannot create RFloat atlas readback.");
                readback = new Texture2D(source.width, source.height, TextureFormat.RFloat, false, true)
                    { hideFlags = HideFlags.HideAndDontSave };
                material = new Material(s_readbackShader)
                    { hideFlags = HideFlags.HideAndDontSave };
                material.SetTexture("_ShadowCullingSource", source);
                material.SetVector("_ShadowCullingSourceSize", new Vector4(source.width, source.height, 0, 0));
                Graphics.Blit(source, copy, material, 0);
                RenderTexture.active = copy;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                frame.depth = readback.GetRawTextureData<float>().ToArray();
                // Sentinels occupy only the atlas corner outside every tile. They
                // independently identify ReadPixels row orientation on this backend.
                float first = frame.depth[0], last = frame.depth[(source.height - 1) * source.width];
                if (first == -1000 && last == -2000) frame.nativeTopFirst = true;
                else if (first == -2000 && last == -1000) frame.nativeTopFirst = false;
                else throw new InvalidOperationException("Raw depth copy failed its native-row sentinel calibration.");
            }
            finally
            {
                RenderTexture.active = previous;
                if (copy) { copy.Release(); UnityEngine.Object.DestroyImmediate(copy); }
                if (readback) UnityEngine.Object.DestroyImmediate(readback);
                if (material) UnityEngine.Object.DestroyImmediate(material);
            }
            return frame;
        }

        private static Comparison Compare(Frame a, Frame b, string pair)
        {
            if (a.width != b.width || a.height != b.height || a.nativeTopFirst != b.nativeTopFirst)
                throw new InvalidOperationException("Atlas dimensions/readback orientation changed between captures.");
            var result = new Comparison { pair = pair, passed = true };
            long occupied = 0;
            for (int cascade = 0; cascade < 4; ++cascade)
            {
                CascadeMetadata g = a.geometry[cascade], other = b.geometry[cascade];
                if (!g.tile.Equals(other.tile) || g.paddingWorld != other.paddingWorld || g.orthoWidth != other.orthoWidth ||
                    !SameMatrix(g.shadowVP, other.shadowVP) || !SameMatrix(g.shadowView, other.shadowView))
                    throw new InvalidOperationException("Projection, light direction or settings changed in cascade " + cascade);
                for (int i = 0; i < 8; ++i)
                    if (g.receiverCorners[i] != other.receiverCorners[i])
                        throw new InvalidOperationException("Receiver coverage changed between captures.");
                Vector3[] planes = SamplingPlanes(g);
                var check = new CascadeComparison { cascade = cascade };
                for (int y = g.tile.yMin; y < g.tile.yMax; ++y)
                {
                    int row = a.nativeTopFirst ? y : a.height - 1 - y;
                    int first, last;
                    QueryRange(planes, y + 0.5, g.tile, out first, out last);
                    for (int x = g.tile.xMin; x < g.tile.xMax; ++x)
                    {
                        int index = row * a.width + x;
                        float oldDepth = a.depth[index], newDepth = b.depth[index];
                        bool queried = x >= first && x <= last;
                        bool changed = oldDepth != newDepth || float.IsNaN(oldDepth) || float.IsNaN(newDepth) ||
                            float.IsInfinity(oldDepth) || float.IsInfinity(newDepth);
                        if (queried)
                        {
                            ++check.queriedTexels;
                            if (oldDepth != (SystemInfo.usesReversedZBuffer ? 0f : 1f)) ++check.nonClearQueriedTexels;
                            if (changed)
                            {
                                ++check.changedQueriedTexels;
                                check.maximumQueriedDepthDelta = Mathf.Max(check.maximumQueriedDepthDelta, Mathf.Abs(newDepth - oldDepth));
                                if (check.firstMismatchX < 0) { check.firstMismatchX = x; check.firstMismatchY = y; }
                            }
                        }
                        else if (changed) ++check.changedUnqueriedTexels;
                    }
                }
                check.passed = check.queriedTexels > 0 && check.changedQueriedTexels == 0;
                occupied += check.nonClearQueriedTexels;
                result.passed &= check.passed;
                result.cascades.Add(check);
            }
            if (occupied == 0) throw new InvalidOperationException("No non-clear queried depth: a vacuous all-empty atlas cannot validate caster culling.");
            return result;
        }

        // This independent hull is built in atlas texel coordinates using the
        // production GPU VP and ShadowClipToUV convention, not the culling helper.
        private static Vector3[] SamplingPlanes(CascadeMetadata g)
        {
            var points = new List<Vector2>(8);
            foreach (Vector3 corner in g.receiverCorners)
            {
                Vector4 q = g.shadowVP * new Vector4(corner.x, corner.y, corner.z, 1);
                float x = q.x / q.w * 0.5f + 0.5f, y = q.y / q.w * 0.5f + 0.5f;
                if (SystemInfo.graphicsUVStartsAtTop) y = 1 - y;
                points.Add(new Vector2(g.tile.x + x * g.tile.width, g.tile.y + y * g.tile.height));
            }
            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var hull = new List<Vector2>(16);
            foreach (Vector2 p in points)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count + 1;
            for (int i = points.Count - 2; i >= 0; --i)
            {
                Vector2 p = points[i];
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            if (hull.Count < 3) throw new InvalidOperationException("Degenerate receiver sampling footprint.");
            var planes = new Vector3[hull.Count];
            // Extra sub-texel margin avoids excluding a centre only because of
            // CPU projection roundoff; the runtime helper has its own world epsilon.
            float padding = g.paddingWorld * g.tile.width / g.orthoWidth + 0.001f;
            for (int i = 0; i < hull.Count; ++i)
            {
                Vector2 p = hull[i], edge = hull[(i + 1) % hull.Count] - p;
                Vector2 inward = new Vector2(-edge.y, edge.x).normalized;
                planes[i] = new Vector3(inward.x, inward.y, padding - Vector2.Dot(inward, p));
            }
            return planes;
        }

        private static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
            ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

        private static void QueryRange(Vector3[] planes, double y, RectInt tile, out int first, out int last)
        {
            double minimum = tile.xMin, maximum = tile.xMax - 1;
            foreach (Vector3 p in planes)
            {
                double offset = p.y * y + p.z;
                if (p.x > 0.0000001f) minimum = Math.Max(minimum, -offset / p.x - 0.5);
                else if (p.x < -0.0000001f) maximum = Math.Min(maximum, -offset / p.x - 0.5);
                else if (offset < 0) { first = 1; last = 0; return; }
            }
            first = (int)Math.Ceiling(minimum);
            last = (int)Math.Floor(maximum);
        }

        private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; ++i) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Finish(bool success, string message)
        {
            s_running = false;
            EditorApplication.update -= Tick;
            MaxShadowCasterPass.CpuSampleRecorded -= OnShadowRendered;
            MaxShadowCasterPass.ReuseOverride = s_oldOverride;
            MaxShadowCasterPass.CaptureDiagnostics = s_oldDiagnostics;
            MaxShadowCasterPass.CaptureGeometry = s_oldGeometry;
            s_baseline = null;
            s_gameView = null;
            if (s_readbackShader && s_ownsReadbackShader) UnityEngine.Object.DestroyImmediate(s_readbackShader);
            s_readbackShader = null;
            s_status = message;
            if (s_report == null) return;
            s_report.success = success;
            s_report.message = message;
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../OfflineBaseline/ShadowReuse/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-gpu"));
            Directory.CreateDirectory(directory);
            s_report.reportPath = Path.Combine(directory, "depth-comparison.json");
            File.WriteAllText(s_report.reportPath, JsonUtility.ToJson(s_report, true));
            s_status += " Report: " + s_report.reportPath;
            if (success) Debug.Log("Shadow reuse GPU validation: " + s_status);
            else Debug.LogWarning("Shadow reuse GPU validation: " + s_status);
        }
    }
}
#endif
