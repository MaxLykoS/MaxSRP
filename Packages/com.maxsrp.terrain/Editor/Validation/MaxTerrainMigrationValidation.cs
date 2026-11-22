using MaxSRP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    /// <summary>
    /// Explicit Editor validation, never a runtime component in either scene.
    /// Natural frames are counted by the host SRP bridge, not Editor update ticks.
    /// RunNaturalBatch must run with a graphics device and without -quit; the
    /// validator exits after all three real Play sessions finish.
    /// </summary>
    [InitializeOnLoad]
    public static class MaxTerrainMigrationValidation
    {
        const string RunKey = "MaxTerrain.Validation.Run";
        const string PhaseKey = "MaxTerrain.Validation.Phase";
        const string StoppingKey = "MaxTerrain.Validation.Stopping";
        const string SinglePhaseKey = "MaxTerrain.Validation.SinglePhase";
        static readonly FrameProbe probe = new FrameProbe();
        static readonly List<string> errors = new List<string>();
        static MeshComparison rig;
        static int frames;
        static int directCallbacksAfterWarmup;
        static bool stopping;
        static bool captureRequested;
        static bool advancing;
        static bool phaseInitialized;
        static RenderTexture phaseOutput;
        static RenderTexture previousDirectOutput, previousDeltaOutput;
        static double lastFrameTime;
        static readonly List<MaxTerrainGpuCapture> captures = new List<MaxTerrainGpuCapture>();

        [Serializable] sealed class Inspection
        {
            public bool passed;
            public string pipeline, runtimeAssembly, error;
            public List<SceneInspection> scenes = new List<SceneInspection>();
        }
        [Serializable] sealed class SceneInspection
        {
            public string scene, mode, recipe;
            public int missingScripts, missingReferences, roads, decals, activeCameras;
            public float sunLux;
        }
        [Serializable] sealed class Phase
        {
            public bool passed, ready, pinnedReady;
            public string scene, error;
            public int frames, directDraws, deltaDraws, feedback, completedFeedback;
            public bool directCameraEnabled;
            public int directCameraCallbacksAfterWarmup;
            public string directDrawCountMeaning = "Last submitted batch count, not a cumulative count or current-frame activity flag.";
            public int rvt, svt, invalidKeys, overflowFrames, feedbackErrors, sourceFailures;
            public int truncatedFrames, latestFeedbackUniqueKeys, latestFeedbackFailedSamples;
            public List<string> loggedErrors = new List<string>();
            public List<MaxTerrainGpuCapture> gpu = new List<MaxTerrainGpuCapture>();
        }
        [Serializable] sealed class NaturalReport
        {
            public bool passed;
            public string error;
            public string scope = "Editor D3D11: imported authoring/published scenes, natural SRP Play frames. Not mobile validation.";
            public List<Phase> phases = new List<Phase>();
        }

        static MaxTerrainMigrationValidation()
        {
            EditorApplication.playModeStateChanged += PlayState;
            EditorApplication.update += Update;
            Application.logMessageReceived += Log;
            // Unity can reload the Editor domain while Application.isPlaying
            // still reports true during ExitPlaymode. Never restart/clear a
            // completed phase's persistent stopping marker in that interval.
            if (SessionState.GetBool(RunKey, false) && EditorApplication.isPlaying &&
                !SessionState.GetBool(StoppingKey, false))
                BeginPhase();
        }

        static string OutputDirectory
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("MAX_TERRAIN_VALIDATION_OUTPUT");
                return string.IsNullOrEmpty(path) ? Path.GetFullPath("OfflineBaseline/MaxTerrain/Import01") : Path.GetFullPath(path);
            }
        }

        [MenuItem("Tools/MaxTerrain/Validation/Inspect imported scenes")]
        public static void InspectScenes() => Inspect(false);
        public static void InspectBatch() => Inspect(true);

        static void Inspect(bool exit)
        {
            var report = new Inspection();
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Inspect outside Play.");
                var pipeline = QualitySettings.renderPipeline ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
                report.pipeline = pipeline ? pipeline.GetType().FullName : "Built-in";
                report.runtimeAssembly = typeof(MeshComparison).Assembly.GetName().Name;
                Require(report.pipeline == "MaxSRP.MaxRendererPipelineAsset", "The active pipeline must be MaxSRP.");
                Require(report.runtimeAssembly == "MaxTerrain", "The terrain must compile in its independent MaxTerrain assembly.");
                foreach (string path in new[] { MeshTerrainWorkflow.AuthoringScene, MeshTerrainWorkflow.PublishedScene })
                {
                    var scene = EditorSceneManager.OpenScene(path);
                    var item = new SceneInspection { scene = path };
                    report.scenes.Add(item);
                    MeshComparison terrain = null;
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                        {
                            item.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject);
                            foreach (Component component in node.GetComponents<Component>())
                            {
                                if (!component) continue;
                                using (var serialized = new SerializedObject(component))
                                {
                                    var property = serialized.GetIterator();
                                    while (property.Next(true))
                                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                                            property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                                            item.missingReferences++;
                                }
                            }
                        }
                        var found = root.GetComponentInChildren<MeshComparison>(true);
                        if (found) { Require(!terrain, "One terrain rig per workflow scene is required."); terrain = found; }
                        item.roads += root.GetComponentsInChildren<MeshWorldVtRoad>(true).Length;
                        item.decals += root.GetComponentsInChildren<MeshWorldVtDecal>(true).Length;
                    }
                    Require(terrain, "Missing terrain comparison.");
                    item.mode = terrain.workflowMode.ToString();
                    item.recipe = terrain.publication ? terrain.publication.recipeIdentity : "";
                    Require(terrain.geometryShader && terrain.ResolveDirectShader() && terrain.worldVtConsumerShader &&
                        terrain.singlePageProducerShader && terrain.publication, "Missing terrain asset references.");
                    terrain.EffectiveMaterials.Validate();
                    terrain.publication.Validate();
                    foreach (Shader shader in new[] { terrain.geometryShader, terrain.ResolveDirectShader(), terrain.worldVtConsumerShader })
                    {
                        using (var check = new MaterialScope(shader))
                            Require(check.material.FindPass("MaxTerrainGBuffer") >= 0, "Terrain GBuffer pass missing: " + shader.name);
                        Require(!ShaderUtil.ShaderHasError(shader), "Shader compilation error: " + shader.name);
                    }
                    foreach (Camera camera in new[] { terrain.directCamera, terrain.deltaCamera })
                    {
                        Require(camera, "Missing workflow camera.");
                        Require(camera.clearFlags == CameraClearFlags.Skybox, "Workflow cameras must include MaxSRP's sky.");
                        Require(camera.renderingPath == RenderingPath.UsePlayerSettings, "The SRP must own rendering path selection.");
                        if (camera.isActiveAndEnabled) item.activeCameras++;
                    }
                    Require(terrain.sun && RenderSettings.sun == terrain.sun, "RenderSettings.sun must reference this scene's sun.");
                    item.sunLux = terrain.sun.intensity;
                    Require(item.sunLux >= 10000 && terrain.sun.shadows != LightShadows.None, "The sun needs physical lux and terrain shadow support.");
                    Require(item.missingScripts == 0 && item.missingReferences == 0, "Missing scripts or serialized references.");
                    bool authoring = terrain.workflowMode == MeshTerrainWorkflowMode.Authoring;
                    Require(authoring ? item.activeCameras == 1 :
                        item.roads == 0 && item.decals == 0 && item.activeCameras == 2, "Workflow authoring/published object separation is invalid.");
                }
                report.passed = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); Debug.LogException(exception); }
            Write("inspection.json", report);
            if (exit) EditorApplication.Exit(report.passed ? 0 : 1);
        }

        public static void RunNaturalBatch()
        {
            string requestedPhase = Environment.GetEnvironmentVariable("MAX_TERRAIN_VALIDATION_PHASE");
            bool singlePhase = !string.IsNullOrEmpty(requestedPhase);
            int phase = 0;
            if (singlePhase && (!int.TryParse(requestedPhase, out phase) || phase < 0 || phase > 2))
                throw new ArgumentOutOfRangeException("MAX_TERRAIN_VALIDATION_PHASE", "Expected 0 (authoring), 1 (published), or 2 (exclusive Delta).");
            SessionState.SetBool(RunKey, true);
            SessionState.SetBool(StoppingKey, false);
            SessionState.SetBool(SinglePhaseKey, singlePhase);
            SessionState.SetInt(PhaseKey, phase);
            EditorSceneManager.OpenScene(phase == 0 ? MeshTerrainWorkflow.AuthoringScene : MeshTerrainWorkflow.PublishedScene);
            EditorApplication.EnterPlaymode();
        }

        static void PlayState(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(RunKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode && !SessionState.GetBool(StoppingKey, false)) BeginPhase();
            if (state == PlayModeStateChange.ExitingPlayMode) ReleasePhaseOutput();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(StoppingKey, false)) QueueAdvance();
        }

        static void QueueAdvance()
        {
            if (advancing) return;
            advancing = true;
            EditorApplication.delayCall += AdvanceAfterPlay;
        }

        static void AdvanceAfterPlay()
        {
            advancing = false;
            if (!SessionState.GetBool(RunKey, false) || !SessionState.GetBool(StoppingKey, false) ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                int phase = SessionState.GetInt(PhaseKey, 0);
                MaxRenderBridge.Unregister(probe);
                if (phase < 2)
                {
                    // Consume the completed phase exactly once before starting
                    // the next Play transition. The persistent fields survive
                    // the next domain reload; transient callbacks need not.
                    SessionState.SetInt(PhaseKey, phase + 1);
                    SessionState.SetBool(StoppingKey, false);
                    stopping = false;
                    EditorSceneManager.OpenScene(MeshTerrainWorkflow.PublishedScene);
                    Debug.Log("MaxTerrain migration validation: entering phase " + (phase + 1) + ".");
                    EditorApplication.EnterPlaymode();
                    return;
                }
                CompleteRun();
            }
            catch (Exception exception)
            {
                Write("natural.json", new NaturalReport { passed = false, error = exception.ToString() });
                SessionState.SetBool(RunKey, false);
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        static void CompleteRun()
        {
            int exitCode = 1;
            var report = new NaturalReport();
            try
            {
                report.passed = true;
                foreach (string file in new[] { "authoring-play.json", "published-play.json", "single-delta-play.json" })
                {
                    var item = JsonUtility.FromJson<Phase>(File.ReadAllText(Path.Combine(OutputDirectory, file)));
                    report.phases.Add(item); report.passed &= item.passed;
                }
                Write("natural.json", report);
                exitCode = report.passed ? 0 : 1;
                Debug.Log("MaxTerrain migration validation: completed all three GPU-verified phases, passed=" + report.passed + ".");
            }
            catch (Exception exception)
            {
                report.passed = false; report.error = exception.ToString();
                try { Write("natural.json", report); }
                catch (Exception outputError) { Debug.LogException(outputError); }
                Debug.LogException(exception);
            }
            finally
            {
                SessionState.SetBool(RunKey, false);
                SessionState.SetBool(StoppingKey, false);
                ReleasePhaseOutput();
                EditorApplication.Exit(exitCode);
            }
        }

        static void BeginPhase()
        {
            // InitializeOnLoad and EnteredPlayMode can both observe the same
            // transition. Do not reset a phase or leak its temporary output.
            if (phaseInitialized && phaseOutput && rig) return;
            rig = Object.FindObjectOfType<MeshComparison>();
            if (!rig) return; // Update retries if the scene is not instantiated yet.
            frames = 0; directCallbacksAfterWarmup = 0; errors.Clear(); stopping = false;
            captures.Clear(); captureRequested = false;
            SessionState.SetBool(StoppingKey, false);
            lastFrameTime = EditorApplication.timeSinceStartup;
            try
            {
                Require(rig.directCamera && rig.deltaCamera, "The validation scene is missing its workflow cameras.");
                // A headless Editor need not render a Game View backbuffer.
                // Both cameras borrow ONE output, preserving the published
                // two-camera output group and its existing TAA bypass contract.
                phaseOutput = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "MaxTerrain natural validation shared output", hideFlags = HideFlags.HideAndDontSave };
                Require(phaseOutput.Create(), "Cannot allocate the natural-validation output.");
                previousDirectOutput = rig.directCamera.targetTexture;
                previousDeltaOutput = rig.deltaCamera.targetTexture;
                rig.directCamera.targetTexture = phaseOutput;
                rig.deltaCamera.targetTexture = phaseOutput;
                if (SessionState.GetInt(PhaseKey, 0) == 2)
                {
                    // A sole active Delta camera using the same target exercises
                    // the host's existing shared TAA and motion-vector path.
                    rig.directCamera.enabled = false;
                    rig.deltaCamera.rect = new Rect(0, 0, 1, 1);
                }
                phaseInitialized = true;
                MaxRenderBridge.Register(probe);
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception exception) { FinishPhase(exception.ToString()); }
        }

        static void ReleasePhaseOutput()
        {
            MaxRenderBridge.Unregister(probe);
            if (rig)
            {
                if (rig.directCamera && rig.directCamera.targetTexture == phaseOutput) rig.directCamera.targetTexture = previousDirectOutput;
                if (rig.deltaCamera && rig.deltaCamera.targetTexture == phaseOutput) rig.deltaCamera.targetTexture = previousDeltaOutput;
            }
            if (phaseOutput)
            {
                phaseOutput.Release();
                if (Application.isPlaying) Object.Destroy(phaseOutput);
                else Object.DestroyImmediate(phaseOutput);
            }
            phaseOutput = previousDirectOutput = previousDeltaOutput = null;
            phaseInitialized = false;
        }

        static void Log(string message, string stack, LogType type)
        {
            if (SessionState.GetBool(RunKey, false) && EditorApplication.isPlaying &&
                (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                errors.Add(message + "\n" + stack);
        }

        static void Update()
        {
            if (!SessionState.GetBool(RunKey, false)) return;
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // EnteredEditMode can precede this assembly's InitializeOnLoad
                // subscription. The update fallback resumes from SessionState.
                if (SessionState.GetBool(StoppingKey, false)) QueueAdvance();
                return;
            }
            if (!EditorApplication.isPlaying || stopping || SessionState.GetBool(StoppingKey, false)) return;
            if (!phaseInitialized) { BeginPhase(); return; }
            EditorApplication.QueuePlayerLoopUpdate();
            if (rig && !string.IsNullOrEmpty(rig.Error)) { FinishPhase(rig.Error); return; }
            if (captureRequested)
            {
                bool done = captures.Count > 0;
                foreach (var capture in captures) done &= capture.complete;
                if (done) { FinishPhase(null); return; }
            }
            if (EditorApplication.timeSinceStartup - lastFrameTime > 180)
                FinishPhase("The host SRP did not render the expected terrain camera for 180 seconds.");
        }

        sealed class FrameProbe : IMaxRenderParticipant
        {
            public bool RendersCamera(Camera camera) => rig && (camera == rig.directCamera || camera == rig.deltaCamera);
            public void PrepareCamera(ScriptableRenderContext context, Camera camera) { }
            public void RecordGBuffer(CommandBuffer commands, Camera camera) { }
            public void RecordMotion(CommandBuffer commands, Camera camera) { }
            public void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 view, Matrix4x4 projection, Plane[] planes) { }
            public void EndCamera(ScriptableRenderContext context, Camera camera)
            {
                if (stopping || captureRequested) return;
                // Observe actual host-camera callbacks separately from a
                // renderer's retained "last batch count" diagnostic. Allow the
                // initial camera-list transition, then check steady isolation.
                if (camera == rig.directCamera && frames >= 60) directCallbacksAfterWarmup++;
                Camera targetCamera = SessionState.GetInt(PhaseKey, 0) == 0 ? rig.directCamera : rig.deltaCamera;
                if (camera != targetCamera) return;
                frames++; lastFrameTime = EditorApplication.timeSinceStartup;
                if (SessionState.GetInt(PhaseKey, 0) == 1)
                {
                    if (frames == 240) rig.SetShot(3);
                    if (frames == 480) rig.FocusSinglePage();
                }
                int phase = SessionState.GetInt(PhaseKey, 0);
                if (phase == 2)
                {
                    if (frames >= 180 && frames < 300)
                    {
                        rig.directCamera.transform.position += Vector3.right * .03f;
                        rig.Sync();
                    }
                    if (frames == 240)
                        captures.Add(MaxTerrainValidationGpu.Record(context, camera, "single-delta-moving", OutputDirectory));
                }
                int target = phase == 0 ? 300 : phase == 1 ? 720 : 480;
                if (frames >= target)
                {
                    captureRequested = true;
                    string name = phase == 0 ? "authoring" : phase == 1 ? "published" : "single-delta-final";
                    captures.Add(MaxTerrainValidationGpu.Record(context, camera, name, OutputDirectory));
                }
            }
        }

        static void FinishPhase(string failure)
        {
            if (stopping) return;
            stopping = true;
            SessionState.SetBool(StoppingKey, true);
            var item = new Phase { frames = frames, scene = SceneManager.GetActiveScene().path, error = failure,
                loggedErrors = new List<string>(errors), gpu = new List<MaxTerrainGpuCapture>(captures) };
            try
            {
                Require(rig && rig.IsReady && string.IsNullOrEmpty(rig.Error), rig ? rig.Error ?? "Terrain not ready." : "Missing runtime terrain.");
                object direct = Field(rig, "direct"), delta = Field(rig, "delta");
                item.directDraws = Value<int>(direct, "DrawCount"); item.deltaDraws = Value<int>(delta, "DrawCount");
                item.directCameraEnabled = rig.directCamera.enabled;
                item.directCameraCallbacksAfterWarmup = directCallbacksAfterWarmup;
                if (SessionState.GetInt(PhaseKey, 0) == 2)
                    Require(!item.directCameraEnabled && item.directCameraCallbacksAfterWarmup == 0,
                        "The disabled DirectDelta camera remained active during the exclusive Delta phase.");
                else Require(item.directDraws > 0, "DirectDelta did not submit geometry.");
                if (SessionState.GetInt(PhaseKey, 0) >= 1)
                {
                    var vt = Value<MeshWorldVirtualTexture>(delta, "WorldVt");
                    Require(vt != null, "Delta did not initialize VT.");
                    item.ready = vt.Ready; item.pinnedReady = vt.PinnedReady;
                    item.feedback = vt.FeedbackSubmitted; item.completedFeedback = vt.CompletedFeedback;
                    item.rvt = vt.RvtPagesRendered; item.svt = vt.SvtPagesUploaded;
                    item.invalidKeys = vt.InvalidFeedbackKeys; item.overflowFrames = vt.FeedbackOverflowFrames;
                    // More unique requests than the fixed 4KiB output budget
                    // is expected truncation, retained in the report. A hash /
                    // raw-buffer insertion failure is a separate hard error.
                    item.truncatedFrames = vt.FeedbackTruncatedFrames;
                    item.latestFeedbackUniqueKeys = vt.LatestFeedbackUniqueKeys;
                    item.latestFeedbackFailedSamples = vt.LatestFeedbackFailedSamples;
                    item.feedbackErrors = vt.FeedbackErrors; item.sourceFailures = vt.FailedSourcePages;
                    Require(item.deltaDraws > 0 && item.ready && item.feedback > 0 && item.completedFeedback > 0 && item.rvt > 0 && item.svt > 0,
                        "The deferred terrain/VT loop did not make progress.");
                    Require(item.invalidKeys == 0 && item.overflowFrames == 0 && item.feedbackErrors == 0 && item.sourceFailures == 0 && string.IsNullOrEmpty(vt.Error),
                        "VT feedback, source or publication error: " + vt.Error);
                }
                Require(item.gpu.Count > 0, "GPU evidence was not captured.");
                foreach (var capture in item.gpu)
                    Require(capture.complete && capture.passed, "GPU geometry/material evidence failed: " + capture.name + ": " + capture.error);
                if (SessionState.GetInt(PhaseKey, 0) == 2)
                {
                    bool movingMotion = false;
                    foreach (var capture in item.gpu)
                    {
                        Require(capture.taaActive && capture.taaHistoryValid && capture.motionValid > 100,
                            "The exclusive Delta camera did not have valid TAA/motion data.");
                        if (capture.name == "single-delta-moving") movingMotion = capture.motionMoving > 100;
                    }
                    Require(movingMotion, "Moving terrain did not produce nonzero valid motion.");
                }
                Require(string.IsNullOrEmpty(failure) && errors.Count == 0, failure ?? "Runtime error log is not empty.");
                item.passed = true;
            }
            catch (Exception exception) { item.error = exception.ToString(); }
            int phase = SessionState.GetInt(PhaseKey, 0);
            Write(phase == 0 ? "authoring-play.json" : phase == 1 ? "published-play.json" : "single-delta-play.json", item);
            if (SessionState.GetBool(SinglePhaseKey, false))
            {
                // A fresh Editor process can run exactly one real Play phase.
                // All evidence and GPU callbacks are complete here; no transition
                // back to EditMode or domain-reload callback is needed to finish.
                if (phase == 2) CompleteRun();
                else
                {
                    SessionState.SetBool(RunKey, false);
                    SessionState.SetBool(StoppingKey, false);
                    ReleasePhaseOutput();
                    Debug.Log("MaxTerrain migration validation: completed isolated phase " + phase + ", passed=" + item.passed + ".");
                    EditorApplication.Exit(item.passed ? 0 : 1);
                }
                return;
            }
            if (phase == 2)
            {
                // GPU copies and all assertions are complete already. Aggregate
                // before quitting so a final ExitPlaymode/domain-reload event
                // cannot strand a finished batch or lose its terminal report.
                CompleteRun();
                return;
            }
            EditorApplication.delayCall += EditorApplication.ExitPlaymode;
        }

        static object Field(object owner, string name) => owner?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
        static T Value<T>(object owner, string name) => owner == null ? default : (T)owner.GetType().GetProperty(name).GetValue(owner);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Write(string file, object value)
        { Directory.CreateDirectory(OutputDirectory); File.WriteAllText(Path.Combine(OutputDirectory, file), JsonUtility.ToJson(value, true)); }
        sealed class MaterialScope : IDisposable
        {
            public readonly Material material;
            public MaterialScope(Shader shader) { material = new Material(shader); }
            public void Dispose() => Object.DestroyImmediate(material);
        }
    }
}
