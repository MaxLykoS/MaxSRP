using MaxSRP;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    // Editor-only camera-turn evidence. No page scheduling or shading code is changed.
    [InitializeOnLoad]
    public static class MaxTerrainVtTurnValidation
    {
        const string RunKey = "MaxTerrain.VtTurn.Run", StartKey = "MaxTerrain.VtTurn.Start";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Probe probe = new Probe();
        static readonly List<Row> rows = new List<Row>();
        static readonly List<string> errors = new List<string>();
        static MeshComparison rig;
        static Camera camera;
        static RenderTexture output;
        static bool attached, transition, waiting, completed;
        static int phase, phaseFrame, totalFrames, pending;
        static double previousFrameTime, lastFrameTime;

        [Serializable] sealed class Row
        {
            public string phase;
            public int frame, engineFrame, phaseFrame;
            public int feedbackEpoch, previousVtFrame, latestFeedbackFrame;
            public long latestFeedbackSequence;
            public float yaw;
            public double wallMilliseconds;
            public bool ready, pinnedReady;
            public int requested, resident, residentRvt, residentSvt, evictions, publishedThisFrame, pendingReads;
            public int feedbackSubmitted, feedbackCompleted, feedbackErrors, feedbackDropped, truncatedFrames;
            public int uniqueFeedback, overflowFeedback, failedFeedbackSamples, invalidFeedback;
            public int pendingSource, preparedSource, sourceCompleted, sourceConsumed, sourceCancelled, sourceFailed, sourceRetries;
            public long preparedSourceBytes, sourceHeightCacheBytes;
            public int rvtRendered, svtUploaded, recentVisible, adaptiveReclaimed;
            public int allocationCount, allocationCreates, allocationResizes, allocationShrinks, allocationReleases;
            public int allocationFailures, staleFeedback, occupiedTableTexels;
            public int compressionBatches, compressionPages;
            public bool coverageRequested, coverageComplete;
            public int width, height, diagnosticPixels, missingPixels, coarsePixels, exactRvtPixels, exactSvtPixels, invalidPixels;
            public float missingFraction, coarseFraction, exactFraction;
            public string coverageError;
        }
        [Serializable] sealed class Report
        {
            public bool passed, dualCamera;
            public string error, dataset, publication, publicationVersion, recipe, scene, gpu, api, pattern;
            public string heightRecipe, heightDirectory, controlDirectory, materialFingerprint;
            public int residentMaterialCount;
            public string scope = "Editor D3D11 turn/load evidence. Wall times are not GPU timestamps or a performance benchmark. Mode2/3 diagnostics skip missing-page direct fallback shading; compare mode1 separately for normal rendering cost. Coverage uses only GBuffer3 alpha-negative terrain diagnostic pixels, excluding sky.";
            public int worldMode, width = 1280, height = 720, warmFrames, turnFrames, frames, sampleEvery;
            public float x, z, heightAbove, yaw, pitch, fieldOfView, mipBias;
            public int pageBudget, anisotropy;
            public List<Row> rows;
            public List<string> loggedErrors;
        }

        static string Setting(string name, string fallback = null) => Environment.GetEnvironmentVariable("MAX_TERRAIN_VT_TURN_" + name) ?? fallback;
        static int Integer(string name, int fallback) => int.TryParse(Setting(name), out int value) ? value : fallback;
        static float Number(string name, float fallback) => float.TryParse(Setting(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
        static bool Dual => Integer("DUAL", 0) != 0;
        static int Warm => Math.Max(1, Integer("WARM", 360));
        static int Turn => Math.Max(1, Integer("TURN", 120));
        static int SampleEvery => Math.Max(1, Integer("SAMPLE_EVERY", 5));
        static int Width => Mathf.Clamp(Integer("WIDTH", 1280), 64, SystemInfo.maxTextureSize);
        static int Height => Mathf.Clamp(Integer("HEIGHT", 720), 64, SystemInfo.maxTextureSize);
        static int Mode => Mathf.Clamp(Integer("MODE", 2), 1, 3);
        static bool Quadrants => string.Equals(Setting("PATTERN"), "quadrants", StringComparison.OrdinalIgnoreCase);
        static int PhaseCount => Quadrants ? 5 : 4;
        static string ScenePath => string.Equals(Setting("SCENE"), "authoring", StringComparison.OrdinalIgnoreCase)
            ? MeshTerrainWorkflow.AuthoringScene : MeshTerrainWorkflow.PublishedScene;
        static string Output => Path.GetFullPath(Setting("OUTPUT", "OfflineBaseline/MaxTerrain/VtTurn"));
        static string PhaseName => Quadrants
            ? phase == 0 ? "A-warm" : phase == 1 ? "B-quarter" : phase == 2 ? "C-half" : phase == 3 ? "D-three-quarter" : "A-return"
            : phase == 0 ? "A-warm" : phase == 1 ? "B-first" : phase == 2 ? "A-return" : "B-return";
        static object Field(object owner, string name) => owner?.GetType().GetField(name, Fields)?.GetValue(owner);
        static MeshWorldVirtualTexture Vt => rig ? Field(rig, "delta")?.GetType().GetProperty("WorldVt")?.GetValue(Field(rig, "delta")) as MeshWorldVirtualTexture : null;

        static MaxTerrainVtTurnValidation()
        {
            EditorApplication.update += Update;
            Application.logMessageReceived += Log;
        }

        public static void RunBatch()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Start turn validation outside Play.");
                Directory.CreateDirectory(Output);
                rows.Clear(); errors.Clear(); attached = transition = waiting = completed = false; phase = phaseFrame = totalFrames = pending = 0;
                SessionState.SetBool(RunKey, true);
                SessionState.SetString(StartKey, EditorApplication.timeSinceStartup.ToString("R", CultureInfo.InvariantCulture));
                EditorSceneManager.OpenScene(ScenePath);
                var candidate = Object.FindObjectOfType<MeshComparison>();
                if (!candidate) throw new InvalidOperationException("Missing terrain rig.");
                string publicationPath = Setting("PUBLICATION");
                if (!string.IsNullOrEmpty(publicationPath))
                {
                    candidate.publication = AssetDatabase.LoadAssetAtPath<MeshTerrainPublication>(publicationPath);
                    if (!candidate.publication) throw new FileNotFoundException("Turn-test publication not found: " + publicationPath);
                }
                // The template can be authoring, but VT reproduction always reads a frozen publication.
                candidate.workflowMode = MeshTerrainWorkflowMode.Published;
                candidate.worldVirtualTexture = true;
                candidate.worldVtMode = Mode;
                candidate.worldVtMipBias = Number("BIAS", 0);
                candidate.worldVtMaxAnisotropy = (MeshVtMaxAnisotropy)Integer("ANISO", 4);
                candidate.debugMode = candidate.surfaceDebug = 0;
                EditorUtility.SetDirty(candidate);
                EditorSceneManager.MarkSceneDirty(candidate.gameObject.scene);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        static void Attach()
        {
            rig = Object.FindObjectOfType<MeshComparison>();
            if (!rig) return;
            if (!string.IsNullOrEmpty(rig.Error)) throw new InvalidOperationException(rig.Error);
            if (!rig.IsReady) return;
            int budgetOverride = Integer("BUDGET", 0);
            rig.worldVtPageBudget = budgetOverride > 0 ? Mathf.Clamp(budgetOverride,
                MaxTerrainConfig.MinimumPagesPerFrame, MaxTerrainConfig.MaximumPagesPerFrame) : 0;
            string expectedPublication = Setting("PUBLICATION");
            if (!string.IsNullOrEmpty(expectedPublication) && AssetDatabase.GetAssetPath(rig.publication) != expectedPublication)
                throw new InvalidOperationException("The requested turn-test publication did not survive entering Play.");
            rig.directCamera.enabled = Dual;
            rig.deltaCamera.gameObject.SetActive(true); rig.deltaCamera.enabled = true;
            output = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "VT turn validation shared output" };
            if (!output.Create()) throw new InvalidOperationException("Cannot create turn-test output.");
            rig.directCamera.targetTexture = output; rig.deltaCamera.targetTexture = output;
            rig.directCamera.rect = Dual ? new Rect(0, 0, .5f, 1) : new Rect(0, 0, 1, 1);
            rig.deltaCamera.rect = Dual ? new Rect(.5f, 0, .5f, 1) : new Rect(0, 0, 1, 1);
            camera = rig.deltaCamera;
            rig.directCamera.fieldOfView = Number("FOV", 60);
            ApplyPose();
            attached = true;
            previousFrameTime = lastFrameTime = EditorApplication.timeSinceStartup;
            MaxRenderBridge.Register(probe);
        }

        static void ApplyPose()
        {
            float x = Number("X", 5000), z = Number("Z", 5000), yaw = Number("YAW", 0);
            if (Quadrants) yaw += (phase % 4) * 90;
            else if (phase == 1 || phase == 3) yaw += 180;
            rig.directCamera.transform.SetPositionAndRotation(new Vector3(x,
                rig.Source.SampleMeters(x, z, 0) + Number("HEIGHT_ABOVE", 18), z),
                Quaternion.Euler(Number("PITCH", 12), yaw, 0));
            rig.Sync();
            // Ordinary user turning does not clear VT requests or explicitly invalidate feedback.
        }

        static void Update()
        {
            if (!SessionState.GetBool(RunKey, false) || completed) return;
            try
            {
                if (double.TryParse(SessionState.GetString(StartKey, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double start) &&
                    EditorApplication.timeSinceStartup - start > Math.Max(60, Integer("TIMEOUT", 1200)))
                    throw new TimeoutException("VT turn validation exceeded its overall timeout.");
                if (!EditorApplication.isPlaying) return;
                if (!attached) { Attach(); EditorApplication.QueuePlayerLoopUpdate(); return; }
                if (!rig || !rig.IsReady || !string.IsNullOrEmpty(rig.Error)) throw new InvalidOperationException(rig ? rig.Error ?? "Terrain not ready." : "Terrain was destroyed.");
                if (Vt == null || !string.IsNullOrEmpty(Vt.Error)) throw new InvalidOperationException(Vt?.Error ?? "Missing VT.");
                if (transition)
                {
                    transition = false;
                    if (++phase == PhaseCount) waiting = true;
                    else { phaseFrame = 0; ApplyPose(); }
                }
                if (waiting && pending == 0) { Finish(null); return; }
                if (EditorApplication.timeSinceStartup - lastFrameTime > 180) throw new TimeoutException("Turn-test rendering/readback stalled.");
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
                if (completed || waiting || transition || candidate != camera) return;
                try
                {
                    totalFrames++; phaseFrame++;
                    double now = EditorApplication.timeSinceStartup;
                    MeshWorldVirtualTexture vt = Vt;
                    if (vt == null) return;
                    MeshAdaptiveVtTable table = vt.AdaptiveTable;
                    var row = new Row
                    {
                        phase = PhaseName, frame = totalFrames, phaseFrame = phaseFrame, engineFrame = Time.frameCount,
                        feedbackEpoch = (int)(Field(Field(vt, "feedback"), "feedbackEpoch") ?? -1),
                        previousVtFrame = (int)(Field(Field(rig, "delta"), "previousVtFrame") ?? -1),
                        latestFeedbackFrame = vt.LatestFeedbackFrame, latestFeedbackSequence = vt.LatestFeedbackSequence,
                        yaw = camera.transform.eulerAngles.y, wallMilliseconds = (now - previousFrameTime) * 1000,
                        ready = vt.Ready, pinnedReady = vt.PinnedReady, requested = vt.RequestedCount,
                        resident = vt.ResidentCount, residentRvt = vt.ResidentRvtCount, residentSvt = vt.ResidentSvtCount,
                        evictions = vt.Evictions, publishedThisFrame = vt.PagesPublishedThisFrame, pendingReads = vt.PendingReads,
                        feedbackSubmitted = vt.FeedbackSubmitted, feedbackCompleted = vt.CompletedFeedback,
                        feedbackErrors = vt.FeedbackErrors, feedbackDropped = vt.FeedbackDropped,
                        truncatedFrames = vt.FeedbackTruncatedFrames, uniqueFeedback = vt.LatestFeedbackUniqueKeys,
                        overflowFeedback = vt.LatestFeedbackOverflowKeys, failedFeedbackSamples = vt.LatestFeedbackFailedSamples,
                        invalidFeedback = vt.InvalidFeedbackKeys, pendingSource = vt.PendingSourcePages,
                        preparedSource = vt.PreparedSourcePages, sourceCompleted = vt.CompletedSourcePages,
                        sourceConsumed = vt.ConsumedSourcePages, sourceCancelled = vt.CancelledSourcePages,
                        sourceFailed = vt.FailedSourcePages, sourceRetries = vt.PageInputNotReadyRetries,
                        preparedSourceBytes = vt.PreparedSourceBytes, sourceHeightCacheBytes = vt.SourceHeightCacheBytes,
                        rvtRendered = vt.RvtPagesRendered, svtUploaded = vt.SvtPagesUploaded,
                        recentVisible = vt.RecentVisiblePages, adaptiveReclaimed = vt.AdaptivePagesReclaimed,
                        allocationCount = table.ActiveCount, allocationCreates = table.Allocations,
                        allocationResizes = table.Resizes, allocationShrinks = table.Shrinks, allocationReleases = table.Releases,
                        allocationFailures = table.AllocationFailures, staleFeedback = table.StaleFeedback,
                        occupiedTableTexels = table.OccupiedTexels, compressionBatches = vt.Bc3CompressionBatches,
                        compressionPages = vt.Bc3CompressedPages
                    };
                    previousFrameTime = lastFrameTime = now;
                    rows.Add(row);
                    int duration = phase == 0 ? Warm : Turn;
                    bool snapshot = phaseFrame == 1 || phaseFrame == 2 || phaseFrame == 4 || phaseFrame == 8 || phaseFrame == 16 ||
                        phaseFrame == 32 || phaseFrame == 64 || phaseFrame == duration;
                    if ((snapshot || phaseFrame % SampleEvery == 0) && pending < 4)
                        ReadCoverage(context, row, snapshot);
                    if (phaseFrame % 60 == 0) SaveProgress(null);
                    if (phaseFrame >= duration) transition = true;
                }
                catch (Exception exception) { Finish(exception.ToString()); }
            }
        }

        static void ReadCoverage(ScriptableRenderContext context, Row row, bool screenshot)
        {
            var commands = new CommandBuffer { name = "VT turn diagnostic evidence" };
            RenderTexture copy = null;
            try
            {
                if (Mode == 2)
                {
                    var resources = Field(Field(RenderPipelineManager.currentPipeline, "m_targets"), "m_cameraResources") as IDictionary;
                    var buffers = Field(resources?[camera.GetInstanceID()], "Buffers") as RenderTexture[];
                    if (buffers == null || buffers.Length != 4) throw new InvalidOperationException("Cannot locate right-camera GBuffer.");
                    copy = MaxTerrainValidationGpu.CopyColorForReadback(commands, buffers[3], "VT page-source classification");
                    row.coverageRequested = true; row.width = copy.width; row.height = copy.height;
                    pending++;
                    RenderTexture captured = copy;
                    commands.RequestAsyncReadback(copy, 0, request =>
                    {
                        try
                        {
                            if (request.hasError) row.coverageError = "Page-source GPU readback failed.";
                            else
                            {
                                var pixels = request.GetData<Color>();
                                for (int i = 0; i < pixels.Length; ++i)
                                {
                                    Color value = pixels[i];
                                    if (value.a >= 0) continue; // excludes sky and ordinary opaque materials
                                    row.diagnosticPixels++;
                                    if (value.r > .9f && value.g < .1f && value.b < .1f) row.missingPixels++;
                                    else if (value.r > .9f && value.g > .9f && value.b < .1f) row.coarsePixels++;
                                    else if (value.r < .1f && value.g > .9f && value.b < .1f) row.exactRvtPixels++;
                                    else if (value.r < .1f && value.g < .1f && value.b > .9f) row.exactSvtPixels++;
                                    else row.invalidPixels++;
                                }
                                if (row.diagnosticPixels > 0)
                                {
                                    row.missingFraction = (float)row.missingPixels / row.diagnosticPixels;
                                    row.coarseFraction = (float)row.coarsePixels / row.diagnosticPixels;
                                    row.exactFraction = (float)(row.exactRvtPixels + row.exactSvtPixels) / row.diagnosticPixels;
                                }
                            }
                        }
                        catch (Exception exception) { row.coverageError = exception.ToString(); }
                        finally { row.coverageComplete = true; captured.Release(); Object.Destroy(captured); pending--; }
                    });
                    copy = null;
                }
                if (screenshot) ReadScreenshot(commands, row);
                context.ExecuteCommandBuffer(commands);
                commands.Clear();
            }
            finally { commands.Dispose(); if (copy) { copy.Release(); Object.Destroy(copy); } }
        }

        static void ReadScreenshot(CommandBuffer commands, Row row)
        {
            var target = new RenderTexture(output.width, output.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "VT turn screen evidence" };
            if (!target.Create()) throw new InvalidOperationException("Cannot allocate turn screenshot.");
            commands.Blit(output, target);
            pending++;
            commands.RequestAsyncReadback(target, 0, request =>
            {
                Texture2D image = null;
                try
                {
                    if (request.hasError) row.coverageError = "Turn screenshot readback failed.";
                    else
                    {
                        image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
                        image.SetPixelData(request.GetData<byte>(), 0); image.Apply(false);
                        File.WriteAllBytes(Path.Combine(Output, row.phase + "-" + row.phaseFrame.ToString("D4") + ".png"), image.EncodeToPNG());
                    }
                }
                catch (Exception exception) { row.coverageError = exception.ToString(); }
                finally { if (image) Object.Destroy(image); target.Release(); Object.Destroy(target); pending--; }
            });
        }

        static Report MakeReport(string failure) => new Report
        {
            passed = failure == null, error = failure, dataset = Setting("DATASET", "iki"),
            pattern = Quadrants ? "quadrants" : "opposed",
            publication = Setting("PUBLICATION", "scene publication"),
            publicationVersion = rig && rig.publication ? rig.publication.versionId : null,
            recipe = rig && rig.publication ? rig.publication.recipeIdentity : null,
            heightRecipe = rig && rig.Source != null ? rig.Source.Metadata.recipe : null,
            heightDirectory = rig ? rig.datasetRelativeDirectory : null,
            controlDirectory = rig ? rig.EffectiveControlDirectory : null,
            materialFingerprint = rig && rig.EffectiveMaterials ? rig.EffectiveMaterials.sourceFingerprint : null,
            residentMaterialCount = rig && rig.EffectiveMaterials ? rig.EffectiveMaterials.names.Length : 0,
            scene = ScenePath, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
            dualCamera = Dual, worldMode = Mode, warmFrames = Warm, turnFrames = Turn, frames = totalFrames, sampleEvery = SampleEvery,
            width = Width, height = Height,
            x = Number("X", 5000), z = Number("Z", 5000), heightAbove = Number("HEIGHT_ABOVE", 18),
            yaw = Number("YAW", 0), pitch = Number("PITCH", 12), fieldOfView = Number("FOV", 60),
            mipBias = Number("BIAS", 0), pageBudget = rig ? rig.WorldVtPageBudget : 0, anisotropy = Integer("ANISO", 4),
            rows = new List<Row>(rows), loggedErrors = new List<string>(errors)
        };

        static void SaveProgress(string error)
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "turn.json"), JsonUtility.ToJson(MakeReport(error), true));
        }

        static void Log(string message, string stack, LogType type)
        {
            if (SessionState.GetBool(RunKey, false) && EditorApplication.isPlaying &&
                (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) errors.Add(message + "\n" + stack);
        }

        static void Finish(string failure)
        {
            if (completed) return;
            completed = true;
            try
            {
                if (failure == null)
                {
                    if (rows.Count != Warm + Turn * (PhaseCount - 1)) failure = "Unexpected rendered turn-test frame count.";
                    else if (errors.Count != 0) failure = "Turn-test emitted error logs.";
                    else foreach (Row row in rows)
                        if (!string.IsNullOrEmpty(row.coverageError)) { failure = row.coverageError; break; }
                    if (failure == null && Mode == 2)
                    {
                        int diagnosticFrames = 0;
                        foreach (Row row in rows) if (row.coverageComplete && row.diagnosticPixels > 100) diagnosticFrames++;
                        if (diagnosticFrames == 0) failure = "No mode2 terrain diagnostic coverage was recorded.";
                    }
                }
                SaveProgress(failure);
                WriteCsv();
            }
            finally
            {
                SessionState.SetBool(RunKey, false);
                if (attached) MaxRenderBridge.Unregister(probe);
                if (rig)
                {
                    if (rig.directCamera) rig.directCamera.targetTexture = null;
                    if (rig.deltaCamera) rig.deltaCamera.targetTexture = null;
                }
                if (output) { output.Release(); Object.Destroy(output); output = null; }
                EditorApplication.Exit(failure == null ? 0 : 1);
            }
        }

        static void WriteCsv()
        {
            var members = typeof(Row).GetFields(BindingFlags.Public | BindingFlags.Instance);
            var text = new StringBuilder();
            for (int i = 0; i < members.Length; i++) { if (i > 0) text.Append(','); text.Append(members[i].Name); }
            text.AppendLine();
            foreach (Row row in rows)
            {
                for (int i = 0; i < members.Length; i++)
                {
                    if (i > 0) text.Append(',');
                    object value = members[i].GetValue(row);
                    string formatted = value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
                    text.Append('"').Append(formatted.Replace("\"", "\"\"")).Append('"');
                }
                text.AppendLine();
            }
            File.WriteAllText(Path.Combine(Output, "turn.csv"), text.ToString());
        }
    }
}
