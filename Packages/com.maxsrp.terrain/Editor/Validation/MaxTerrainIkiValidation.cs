using MaxSRP;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    /// <summary>
    /// Editor-only imported-asset evidence. Run in the disposable validation
    /// project with a graphics device, without -quit or -nographics. The live
    /// scenes and runtime rendering algorithms are never modified or saved.
    /// </summary>
    [InitializeOnLoad]
    public static class MaxTerrainIkiValidation
    {
        const string RunKey = "MaxTerrain.IkiValidation.Run";
        const string StartKey = "MaxTerrain.IkiValidation.Start";
        const string ReadbackShader = "Packages/com.maxsrp.terrain/Editor/Validation/MaxTerrainIkiValidationReadback.compute";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Probe probe = new Probe();
        static readonly List<Capture> captures = new List<Capture>();
        static readonly List<string> errors = new List<string>();
        static readonly HashSet<int> combinedColorBins = new HashSet<int>();
        static MeshComparison rig;
        static Camera camera;
        static RenderTexture outputTarget;
        static Viewpoint[] viewpoints;
        static bool attached, finishing, pendingCapture;
        static int stage, frames, totalFrames;
        static double lastFrameTime;

        [Serializable] sealed class Viewpoint
        {
            public string name;
            public int materialId = -1;
            public float x, z;
            public bool overview;
        }
        [Serializable] sealed class Capture
        {
            public string name, error;
            public int view, surfaceDebug, referenceMaterialId;
            public float x, z;
            public Vector3 cameraPosition, cameraEuler;
            public bool complete, passed, terrainReady, vtReady, pinnedReady;
            public int frameCount, width, height, colorBins, purplePixels, coloredPixels, nonFinite;
            public int rvtPages, svtPages, feedback, completedFeedback, feedbackErrors, failedSourcePages;
            public int validMotion, rejectedMotion, movingMotion;
            public int minimumDepthPixels, maximumDepthPixels;
            public float minimumDepth = float.PositiveInfinity, maximumDepth = float.NegativeInfinity;
            public string rawEncoding = "Float32 little-endian; raw GPU row order. GBuffer/motion=RGBA, depth=R. Preview GB0 is sRGB, other previews raw.";
            public MaxTerrainGpuCapture gpu;
            [NonSerialized] internal Color[][] buffers = new Color[4][];
            [NonSerialized] internal Color[] motion;
            [NonSerialized] internal float[] depth;
            [NonSerialized] internal int pending;

            internal void Fail(string value) => error = string.IsNullOrEmpty(error) ? value : error + "\n" + value;
        }
        [Serializable] sealed class Report
        {
            public bool passed, published;
            public string error, gpu, api, scene, materialFingerprint, publicationRecipe;
            public string scope = "Editor GPU evidence for imported Iki assets. Single terrain camera, unchanged MaxTerrain evaluation and host TAA. Not mobile/performance/original-game equivalence evidence.";
            public int frames, materialCount, maximumMaterials, uniqueAlbedoBins;
            public List<Capture> captures = new List<Capture>();
            public List<string> loggedErrors = new List<string>();
        }

        static MaxTerrainIkiValidation()
        {
            EditorApplication.update += Update;
            Application.logMessageReceived += Log;
        }

        static bool Published => !string.Equals(Environment.GetEnvironmentVariable("MAX_TERRAIN_IKI_VALIDATION_SCENE"),
            "authoring", StringComparison.OrdinalIgnoreCase);
        static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("MAX_TERRAIN_IKI_VALIDATION_OUTPUT") ??
            Path.Combine(Environment.GetEnvironmentVariable("MAX_TERRAIN_VALIDATION_OUTPUT") ?? "OfflineBaseline/MaxTerrain/IkiImport", Published ? "IkiPublishedGpu" : "IkiAuthoringGpu"));
        static int PositiveSetting(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;
        }
        static int Warmup => PositiveSetting("MAX_TERRAIN_IKI_VALIDATION_WARMUP", Published ? 180 : 90);
        static int DiagnosticWarmup => PositiveSetting("MAX_TERRAIN_IKI_VALIDATION_F1_WARMUP", 64);

        public static void RunBatch()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Start Iki GPU validation outside Play.");
                Directory.CreateDirectory(Output);
                SessionState.SetBool(RunKey, true);
                SessionState.SetString(StartKey, EditorApplication.timeSinceStartup.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                EditorSceneManager.OpenScene(Published ? MeshTerrainWorkflow.PublishedScene : MeshTerrainWorkflow.AuthoringScene);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        static object Field(object owner, string name) => owner?.GetType().GetField(name, Fields)?.GetValue(owner);
        static T Property<T>(object owner, string name) => owner == null ? default : (T)owner.GetType().GetProperty(name).GetValue(owner);
        static MeshWorldVirtualTexture Vt => Property<MeshWorldVirtualTexture>(Field(rig, "delta"), "WorldVt");

        static void Attach()
        {
            rig = Object.FindObjectOfType<MeshComparison>();
            if (!rig) return;
            if (!string.IsNullOrEmpty(rig.Error)) throw new InvalidOperationException(rig.Error);
            if (!rig.IsReady) return;
            Require(rig.EffectiveMaterials && rig.EffectiveMaterials.Count == 50, "Expected the 50-layer imported Iki material set.");
            Require(rig.Source != null && rig.Source.Metadata.recipe == "iki-2p56-1m-v1", "The active terrain is not the imported Iki heightfield.");
            Require(rig.directCamera && rig.deltaCamera, "Both workflow camera references must exist.");
            rig.directCamera.enabled = !Published;
            rig.deltaCamera.gameObject.SetActive(Published);
            rig.deltaCamera.enabled = Published;
            camera = Published ? rig.deltaCamera : rig.directCamera;
            camera.rect = new Rect(0, 0, 1, 1);
            rig.directCamera.rect = new Rect(0, 0, 1, 1);
            outputTarget = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "Iki validation camera output" };
            Require(outputTarget.Create(), "Cannot allocate validation camera output.");
            camera.targetTexture = outputTarget;
            viewpoints = new[]
            {
                new Viewpoint { name = "central", x = 5000, z = 5000 },
                MaterialPoint(32), MaterialPoint(40), MaterialPoint(49),
                new Viewpoint { name = "fit-hotspot-a", x = 5640, z = 4104 },
                new Viewpoint { name = "fit-hotspot-b", x = 5896, z = 3848 },
                new Viewpoint { name = "whole-island", x = 5000, z = 5000, overview = true }
            };
            captures.Clear(); combinedColorBins.Clear();
            stage = frames = totalFrames = 0;
            lastFrameTime = EditorApplication.timeSinceStartup;
            ApplyView();
            MaxRenderBridge.Register(probe);
            attached = true;
        }

        static Viewpoint MaterialPoint(int id)
        {
            Vector2[] locations = rig.EffectiveMaterials.referenceLocations;
            Require(locations != null && id < locations.Length, "Missing imported reference location for material " + id);
            return new Viewpoint { name = "material-" + id, materialId = id, x = locations[id].x, z = locations[id].y };
        }

        static void ApplyView()
        {
            Viewpoint view = viewpoints[stage / 2];
            bool overview = view.overview;
            rig.debugMode = 0;
            rig.surfaceDebug = stage % 2 == 0 ? 0 : 1;
            rig.worldVtMode = 1;
            rig.directCamera.orthographic = rig.deltaCamera.orthographic = overview;
            rig.directCamera.orthographicSize = rig.deltaCamera.orthographicSize = 4300;
            rig.directCamera.fieldOfView = 55;
            rig.directCamera.nearClipPlane = .25f;
            rig.directCamera.farClipPlane = 20000;
            var target = new Vector3(view.x, rig.Source.SampleMeters(view.x, view.z, 0), view.z);
            rig.directCamera.transform.position = overview ? new Vector3(5000, 10500, 5000) : target + new Vector3(0, 14, -20);
            rig.directCamera.transform.LookAt(overview ? new Vector3(5000, 0, 5000) : target + Vector3.up * .5f,
                overview ? Vector3.forward : Vector3.up);
            rig.Sync();
            MaxRenderBridge.InvalidateTemporalHistory(camera);
            frames = 0;
            lastFrameTime = EditorApplication.timeSinceStartup;
        }

        static void Update()
        {
            if (!SessionState.GetBool(RunKey, false) || finishing) return;
            try
            {
                if (double.TryParse(SessionState.GetString(StartKey, "0"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double began) &&
                    EditorApplication.timeSinceStartup - began > PositiveSetting("MAX_TERRAIN_IKI_VALIDATION_TIMEOUT", 1200))
                    throw new TimeoutException("Iki GPU validation exceeded its overall timeout.");
                if (!EditorApplication.isPlaying) return;
                if (!attached) { Attach(); return; }
                if (!rig || !rig.IsReady || !string.IsNullOrEmpty(rig.Error))
                    throw new InvalidOperationException(rig ? rig.Error ?? "Terrain lost readiness." : "Terrain was destroyed.");
                if (Published)
                {
                    string error = Property<string>(Field(rig, "delta"), "WorldVtError");
                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                }
                if (pendingCapture)
                {
                    Capture item = captures[captures.Count - 1];
                    if (item.pending == 0 && item.gpu.complete)
                    {
                        Analyze(item);
                        item.complete = true;
                        SaveProgress();
                        pendingCapture = false;
                        stage++;
                        if (stage == viewpoints.Length * 2) { Finish(null); return; }
                        ApplyView();
                    }
                }
                if (EditorApplication.timeSinceStartup - lastFrameTime > 180)
                    throw new TimeoutException("Terrain rendering or GPU readback stalled for 180 seconds.");
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        sealed class Probe : IMaxRenderParticipant
        {
            public bool RendersCamera(Camera candidate) => attached && candidate == camera;
            public void PrepareCamera(ScriptableRenderContext context, Camera candidate) { }
            public void RecordGBuffer(CommandBuffer commands, Camera candidate) { }
            public void RecordMotion(CommandBuffer commands, Camera candidate) { }
            public void RecordShadow(CommandBuffer commands, Camera candidate, Matrix4x4 view, Matrix4x4 projection, Plane[] planes) { }
            public void EndCamera(ScriptableRenderContext context, Camera candidate)
            {
                if (finishing || pendingCapture || candidate != camera) return;
                frames++; totalFrames++;
                lastFrameTime = EditorApplication.timeSinceStartup;
                int needed = stage % 2 == 0 ? Warmup : DiagnosticWarmup;
                if (frames >= needed && (!Published || Vt != null && Vt.Ready && Vt.PinnedReady))
                {
                    Record(context);
                    return;
                }
                // A brief slow translation in the central lit shot establishes
                // measured nonzero terrain motion without adding runtime hooks.
                if (stage == 0 && frames >= needed - 16 && frames < needed)
                {
                    rig.directCamera.transform.position += Vector3.right * .03f;
                    rig.Sync();
                }
            }
        }

        static void Record(ScriptableRenderContext context)
        {
            pendingCapture = true;
            Viewpoint view = viewpoints[stage / 2];
            var item = new Capture
            {
                name = (stage / 2).ToString("D2") + "-" + view.name + (stage % 2 == 0 ? "-lit" : "-albedo-f1"),
                view = stage / 2, surfaceDebug = rig.surfaceDebug, referenceMaterialId = view.materialId,
                x = view.x, z = view.z, cameraPosition = camera.transform.position, cameraEuler = camera.transform.eulerAngles,
                frameCount = frames, terrainReady = rig.IsReady
            };
            MeshWorldVirtualTexture vt = Vt;
            if (vt != null)
            {
                item.vtReady = vt.Ready; item.pinnedReady = vt.PinnedReady;
                item.rvtPages = vt.RvtPagesRendered; item.svtPages = vt.SvtPagesUploaded;
                item.feedback = vt.FeedbackSubmitted; item.completedFeedback = vt.CompletedFeedback;
                item.feedbackErrors = vt.FeedbackErrors; item.failedSourcePages = vt.FailedSourcePages;
            }
            captures.Add(item);
            item.gpu = MaxTerrainValidationGpu.Record(context, camera, item.name, Output);
            var commands = new CommandBuffer { name = "Iki imported-asset raw evidence" };
            RenderTexture depthCopy = null;
            var readableCopies = new List<RenderTexture>();
            try
            {
                var cameras = Field(Field(RenderPipelineManager.currentPipeline, "m_targets"), "m_cameraResources") as IDictionary;
                object resources = cameras?[camera.GetInstanceID()];
                var buffers = Field(resources, "Buffers") as RenderTexture[];
                var depth = Field(resources, "Depth") as RenderTexture;
                Require(buffers != null && buffers.Length == 4 && depth, "Missing camera GBuffer/depth resources.");
                var temporal = Field(Field(RenderPipelineManager.currentPipeline, "m_passes"), "TemporalAA");
                var histories = Field(temporal, "m_histories") as IDictionary;
                object history = histories != null && histories.Contains(camera.GetInstanceID()) ? histories[camera.GetInstanceID()] : null;
                var motion = Field(history, "Motion") as RenderTexture;
                item.width = depth.width; item.height = depth.height;
                item.pending = 5 + (motion ? 1 : 0);
                for (int channel = 0; channel < 4; channel++)
                {
                    int capturedChannel = channel;
                    RenderTexture readable = MaxTerrainValidationGpu.CopyColorForReadback(commands, buffers[channel], "Iki raw GBuffer" + channel + " evidence copy");
                    readableCopies.Add(readable);
                    commands.RequestAsyncReadback(readable, 0, request =>
                    {
                        try
                        {
                            if (request.hasError) item.Fail("Raw GBuffer" + capturedChannel + " readback failed.");
                            else
                            {
                                item.buffers[capturedChannel] = request.GetData<Color>().ToArray();
                                File.WriteAllBytes(Path.Combine(Output, item.name + "-gb" + capturedChannel + ".rgba32f"), request.GetData<byte>().ToArray());
                            }
                        }
                        catch (Exception exception) { item.Fail(exception.ToString()); }
                        finally { readable.Release(); Object.Destroy(readable); item.pending--; }
                    });
                }
                var copyShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ReadbackShader);
                Require(copyShader, "Missing Editor-only depth readback shader.");
                depthCopy = new RenderTexture(item.width, item.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                { name = "Iki validation exact depth readback", enableRandomWrite = true };
                Require(depthCopy.Create(), "Cannot allocate RFloat depth readback target.");
                int kernel = copyShader.FindKernel("CopyDepth");
                commands.SetComputeIntParams(copyShader, "_IkiValidationSize", item.width, item.height);
                commands.SetComputeTextureParam(copyShader, kernel, "_IkiValidationDepth", depth);
                commands.SetComputeTextureParam(copyShader, kernel, "_IkiValidationDepthCopy", depthCopy);
                commands.DispatchCompute(copyShader, kernel, (item.width + 7) / 8, (item.height + 7) / 8, 1);
                var capturedDepth = depthCopy;
                commands.RequestAsyncReadback(depthCopy, 0, request =>
                {
                    try
                    {
                        if (request.hasError) item.Fail("Raw depth readback failed.");
                        else
                        {
                            item.depth = request.GetData<float>().ToArray();
                            File.WriteAllBytes(Path.Combine(Output, item.name + "-depth.r32f"), request.GetData<byte>().ToArray());
                        }
                    }
                    catch (Exception exception) { item.Fail(exception.ToString()); }
                    finally { capturedDepth.Release(); Object.Destroy(capturedDepth); item.pending--; }
                });
                if (motion)
                {
                    RenderTexture readable = MaxTerrainValidationGpu.CopyColorForReadback(commands, motion, "Iki raw motion evidence copy");
                    readableCopies.Add(readable);
                    commands.RequestAsyncReadback(readable, 0, request =>
                    {
                        try
                        {
                            if (request.hasError) item.Fail("Raw motion readback failed.");
                            else
                            {
                                item.motion = request.GetData<Color>().ToArray();
                                File.WriteAllBytes(Path.Combine(Output, item.name + "-motion.rgba32f"), request.GetData<byte>().ToArray());
                            }
                        }
                        catch (Exception exception) { item.Fail(exception.ToString()); }
                        finally { readable.Release(); Object.Destroy(readable); item.pending--; }
                    });
                }
                context.ExecuteCommandBuffer(commands);
            }
            catch (Exception exception)
            {
                item.Fail(exception.ToString()); item.pending = 0;
                if (depthCopy) { depthCopy.Release(); Object.Destroy(depthCopy); }
                foreach (var copy in readableCopies) if (copy) { copy.Release(); Object.Destroy(copy); }
            }
            finally { commands.Release(); }
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Color v) => Finite(v.r) && Finite(v.g) && Finite(v.b) && Finite(v.a);
        static void Analyze(Capture item)
        {
            try
            {
                Require(item.gpu.complete && string.IsNullOrEmpty(item.gpu.error), "Existing GPU evidence failed: " + item.gpu.error);
                Require(item.depth != null && item.depth.Length == item.width * item.height, "Incomplete raw depth evidence.");
                foreach (Color[] buffer in item.buffers) Require(buffer != null && buffer.Length == item.depth.Length, "Incomplete raw GBuffer evidence.");
                var bins = new HashSet<int>();
                int geometry = 0;
                for (int i = 0; i < item.depth.Length; i++)
                {
                    float depth = item.depth[i];
                    if (!Finite(depth)) { item.nonFinite++; continue; }
                    item.minimumDepth = Mathf.Min(item.minimumDepth, depth); item.maximumDepth = Mathf.Max(item.maximumDepth, depth);
                    if (depth <= 0) item.minimumDepthPixels++;
                    if (depth >= 1) item.maximumDepthPixels++;
                    bool hasGeometry = SystemInfo.usesReversedZBuffer ? depth > 1e-7f : depth < 1 - 1e-7f;
                    if (!hasGeometry) continue;
                    geometry++;
                    foreach (Color[] buffer in item.buffers) if (!Finite(buffer[i])) item.nonFinite++;
                    // Both F1 and an invalid-material diagnostic use the
                    // existing negative-AO sentinel in GBuffer3.
                    Color albedo = item.buffers[3][i].a < 0 ? item.buffers[3][i] : item.buffers[0][i];
                    if (albedo.r > .95f && albedo.g < .05f && albedo.b > .95f) item.purplePixels++;
                    if (albedo.r + albedo.g + albedo.b > 1e-5f) item.coloredPixels++;
                    int code = Mathf.Clamp((int)(albedo.r * 31), 0, 31) | Mathf.Clamp((int)(albedo.g * 31), 0, 31) << 5 |
                        Mathf.Clamp((int)(albedo.b * 31), 0, 31) << 10;
                    bins.Add(code); combinedColorBins.Add(code);
                    if (item.motion != null)
                    {
                        Color motion = item.motion[i];
                        if (!Finite(motion)) item.nonFinite++;
                        else if (motion.a > .5f)
                        {
                            item.validMotion++;
                            if (motion.r * motion.r + motion.g * motion.g > 1e-12f) item.movingMotion++;
                        }
                        else if (motion.a < -.5f) item.rejectedMotion++;
                    }
                }
                item.colorBins = bins.Count;
                Require(geometry > 100 && item.gpu.geometry > 100, "Insufficient GPU terrain coverage.");
                Require(Math.Abs(geometry - (long)item.gpu.geometry) <= Math.Max(8, geometry / 1000), "Raw depth and GPU geometry statistics disagree.");
                Require(item.nonFinite == 0 && item.gpu.nonFinite == 0, "Non-finite GBuffer, depth or motion values.");
                Require(item.purplePixels <= Math.Max(8, geometry / 1000), "Purple invalid-material pixels exceed 0.1% of geometry.");
                Require(item.coloredPixels >= geometry * .95 && item.colorBins >= 4, "Imported surface is empty or lacks material detail.");
                Require(item.gpu.normals >= geometry * .95 && item.gpu.roughness >= geometry * .95, "Invalid normal/roughness output.");
                if (item.surfaceDebug == 0) Require(item.gpu.passed, "Lit GBuffer evidence did not pass existing gates.");
                if (Published)
                {
                    Require(item.vtReady && item.pinnedReady && item.feedback > 0 && item.completedFeedback > 0,
                        "Published VT did not establish its complete pinned source and feedback loop.");
                    Require(item.feedbackErrors == 0 && item.failedSourcePages == 0, "Published VT source or feedback failures.");
                    Require(item.gpu.taaActive && item.gpu.taaHistoryValid && item.validMotion > 100, "Shared TAA/motion evidence is missing.");
                    if (item.view == 0 && item.surfaceDebug == 0) Require(item.movingMotion > 100, "Moving central shot lacks valid nonzero motion.");
                }
                item.passed = string.IsNullOrEmpty(item.error);
            }
            catch (Exception exception) { item.Fail(exception.ToString()); item.passed = false; }
            finally
            {
                // Raw artifacts are already durable; avoid retaining fourteen
                // complete GBuffer snapshots in the validation Editor heap.
                item.buffers = null; item.motion = null; item.depth = null;
            }
        }

        static void Log(string message, string stack, LogType type)
        {
            if (SessionState.GetBool(RunKey, false) && EditorApplication.isPlaying &&
                (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) errors.Add(message + "\n" + stack);
        }

        static Report MakeReport(string error)
        {
            return new Report
            {
                error = error, published = Published, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                scene = Published ? MeshTerrainWorkflow.PublishedScene : MeshTerrainWorkflow.AuthoringScene,
                materialFingerprint = rig && rig.EffectiveMaterials ? rig.EffectiveMaterials.sourceFingerprint : null,
                publicationRecipe = rig && rig.publication ? rig.publication.recipeIdentity : null,
                frames = totalFrames, materialCount = rig && rig.EffectiveMaterials ? rig.EffectiveMaterials.Count : 0,
                maximumMaterials = MeshMaterialSet.Capacity, uniqueAlbedoBins = combinedColorBins.Count,
                captures = new List<Capture>(captures), loggedErrors = new List<string>(errors)
            };
        }

        static void SaveProgress()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "iki-gpu-progress.json"), JsonUtility.ToJson(MakeReport(null), true));
        }

        static void Finish(string failure)
        {
            if (finishing) return;
            finishing = true;
            var report = MakeReport(failure);
            try
            {
                Require(string.IsNullOrEmpty(failure), failure);
                Require(attached && rig && rig.IsReady && string.IsNullOrEmpty(rig.Error), "Imported terrain did not remain ready.");
                Require(captures.Count == 14, "All seven lit/F1 viewpoint pairs must complete.");
                foreach (Capture item in captures) Require(item.complete && item.passed, item.name + ": " + item.error);
                Require(combinedColorBins.Count >= 16, "The combined imported material evidence lacks color diversity.");
                Require(errors.Count == 0, "The GPU run emitted error logs.");
                if (Published) Require(Vt != null && Vt.RvtPagesRendered > 0 && Vt.SvtPagesUploaded > 0 && string.IsNullOrEmpty(Vt.Error),
                    "The published run did not exercise both RVT production and SVT upload.");
                report.passed = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); }
            finally
            {
                SessionState.SetBool(RunKey, false);
                MaxRenderBridge.Unregister(probe);
                if (camera) camera.targetTexture = null;
                if (outputTarget) { outputTarget.Release(); Object.Destroy(outputTarget); }
                Directory.CreateDirectory(Output);
                File.WriteAllText(Path.Combine(Output, "iki-gpu.json"), JsonUtility.ToJson(report, true));
                EditorApplication.Exit(report.passed ? 0 : 1);
            }
        }

        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
