#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace MaxSRP.EditorDiagnostics
{
    /// <summary>Compares full shadow updates and short-term far-cascade reuse on normal Editor renders.</summary>
    [InitializeOnLoad]
    public static class MaxShadowReuseBenchmark
    {
        private const string MenuRoot = "MaxSRP/Shadow Reuse/";
        private static readonly bool[] Modes = { false, true, true, false, true, false, false, true };
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [Serializable] private sealed class Metric
        {
            public int count;
            public double mean, median, p95, standardDeviation, minimum, maximum;
        }

        [Serializable] private sealed class Summary
        {
            public string mode;
            public int block, fallbackCascadeExecutions;
            public Metric totalMs, setupMs, volumeMs, cullMs, drawScheduleMs, submitMs, updatedCascades, reusedCascades;
        }

        [Serializable] private sealed class Report
        {
            public bool success;
            public string startedUtc, finishedUtc, failure, methodology, unityVersion, gpu, graphicsApi;
            public string scene, camera, cameraGlobalId, platform, shadowSettings, outputDirectory;
            public string reuseStatusBefore, reuseStatusAfter;
            public int cameraInstanceId, renderWidth, renderHeight, warmupPerBlock, samplesPerBlock, blockCount;
            public Vector3 cameraPosition, cameraEulerAngles, effectiveSunDirection;
            public Matrix4x4 cameraProjection, cameraWorldToCamera;
            public float nearClip, farClip, verticalFieldOfView, solarHour;
            public bool orthographic;
            public Summary baseline, reused;
            public List<Summary> blocks = new List<Summary>();
            public double meanDifferenceMs, meanDifferencePercent;
        }

        private struct Row
        {
            internal int Block, Sample;
            internal MaxShadowCasterPass.ShadowCpuSample Cpu;
        }

        private static Camera s_camera;
        private static MaxRendererPipelineAsset s_pipeline;
        private static MaxBakedSolarGI s_solar;
        private static Light s_sun;
        private static EditorWindow[] s_gameViews;
        private static Report s_report;
        private static List<Row> s_rows;
        private static bool? s_previousOverride;
        private static bool s_previousDiagnostics, s_previousGeometry;
        private static bool s_running, s_finishPending;
        private static int s_block, s_inBlock;
        private static double s_lastSampleTime;
        private static Quaternion s_sunRotation;
        private static float s_sunIntensity;
        private static MaxRenderPlatform s_platform;
        private static string s_status = "Idle";
        private static MaxBakedSolarGI s_previewSolar;
        private static float s_previewOriginalHour;
        private static double s_previewLastTime;
        private static EditorWindow[] s_previewGameViews;
        private static float s_previewDayMinutes = 30.0f;
        public static bool PreviewRunning => s_previewSolar != null;
        public static float PreviewDayMinutes
        {
            get => s_previewDayMinutes;
            set => s_previewDayMinutes = float.IsNaN(value) || float.IsInfinity(value) ? 30.0f : Mathf.Clamp(value, 1.0f, 1440.0f);
        }

        public static string Status
        {
            get { if (s_running) UpdateStatus(); return s_status; }
        }
        public static bool IsRunning => s_running;
        public static string LastReportDirectory { get; private set; }

        static MaxShadowReuseBenchmark()
        {
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        [MenuItem(MenuRoot + "A - Update All Cascades")]
        public static void UseBaseline() { SetMode(false, "A: all four shadow cascades update every frame; XY culling remains enabled"); }

        [MenuItem(MenuRoot + "B - Reuse Far Cascades")]
        public static void UseReuse() { SetMode(true, "B: near two cascades update every frame; far cascades may reuse cached depth"); }

        [MenuItem(MenuRoot + "Use Configuration")]
        public static void UseConfiguration() { SetMode(null, "Shadow configuration"); }

        [MenuItem(MenuRoot + "A - Update All Cascades", true)]
        private static bool ValidateBaseline() { return ValidateMode(false, "A - Update All Cascades"); }

        [MenuItem(MenuRoot + "B - Reuse Far Cascades", true)]
        private static bool ValidateReuse() { return ValidateMode(true, "B - Reuse Far Cascades"); }

        [MenuItem(MenuRoot + "Use Configuration", true)]
        private static bool ValidateConfiguration() { return ValidateMode(null, "Use Configuration"); }

        private static bool ValidateMode(bool? mode, string item)
        {
            Menu.SetChecked(MenuRoot + item, MaxShadowCasterPass.ReuseOverride == mode);
            return !s_running && !MaxShadowCasterPass.CaptureDiagnostics && !MaxShadowCasterPass.CaptureGeometry;
        }

        [MenuItem(MenuRoot + "Invalidate Cache")]
        public static void InvalidateCache()
        {
            RequireIdle();
            MaxShadowCasterPass.InvalidateCaches();
            RepaintViews();
            Debug.Log(MaxShadowCasterPass.ReuseStatus);
        }

        private static void RequireIdle()
        {
            if (s_running || MaxShadowCasterPass.CaptureDiagnostics || MaxShadowCasterPass.CaptureGeometry)
                throw new InvalidOperationException("Finish the active shadow diagnostic before changing its A/B mode or depth cache.");
        }

        private static void RepaintViews()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            foreach (EditorWindow view in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (view && view.GetType().Name == "GameView") view.Repaint();
        }

        private static void SetMode(bool? mode, string description)
        {
            RequireIdle();
            MaxShadowCasterPass.ReuseOverride = mode;
            MaxShadowCasterPass.InvalidateCaches();
            RepaintViews();
            Debug.Log(description + "; " + MaxShadowCasterPass.ReuseStatus);
        }

        [MenuItem(MenuRoot + "Run CPU A-B (Current Main Camera)")]
        public static void Run() { Debug.Log(Begin()); }

        /// <summary>Does not change the camera, scene, Play mode, or serialized configuration.</summary>
        public static string Begin(int cameraInstanceId = 0, int warmupPerBlock = 20,
            int samplesPerBlock = 80, int blockCount = 8)
        {
            RequireIdle();
            if (PreviewRunning) throw new InvalidOperationException("Stop the temporary TOD preview before a fixed-time CPU comparison.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run the shadow benchmark in a ready Editor in Edit mode.");
            if (warmupPerBlock < 0 || samplesPerBlock < 10 || samplesPerBlock > 10000 || (blockCount != 4 && blockCount != 8))
                throw new ArgumentOutOfRangeException("Use at least 10 samples and 4 or 8 A/B blocks.");
            var camera = cameraInstanceId == 0 ? Camera.main : EditorUtility.InstanceIDToObject(cameraInstanceId) as Camera;
            if (!camera || camera.cameraType != CameraType.Game || !camera.isActiveAndEnabled ||
                !camera.gameObject.scene.IsValid() || camera.gameObject.scene != SceneManager.GetActiveScene())
                throw new InvalidOperationException("The active scene needs an enabled Main Camera (or an explicit enabled Game camera ID).");
            var pipeline = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
            if (!pipeline) throw new InvalidOperationException("The current rendering pipeline is not MaxSRP.");
            var sun = SunLightData.Capture();
            if (!sun.CastsShadows) throw new InvalidOperationException("The active scene sun does not cast shadows.");

            s_camera = camera;
            s_pipeline = pipeline;
            s_platform = pipeline.Platform;
            s_sun = sun.Source;
            s_sunRotation = s_sun.transform.rotation;
            s_sunIntensity = s_sun.intensity;
            s_solar = FindSolar(camera);
            if (s_solar)
            {
                var align = typeof(MaxBakedSolarGI).GetMethod("AlignSun", BindingFlags.Instance | BindingFlags.NonPublic);
                if (align != null) sun = (SunLightData)align.Invoke(s_solar, new object[] { sun });
            }
            var allViews = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var gameViews = new List<EditorWindow>();
            foreach (var view in allViews) if (view && view.GetType().Name == "GameView") gameViews.Add(view);
            s_gameViews = gameViews.ToArray();
            if (s_gameViews.Length == 0)
                throw new InvalidOperationException("Open a Game view so the scene camera can render normally, then start the benchmark.");

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", Invariant);
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/ShadowReuse", stamp));
            Directory.CreateDirectory(directory);
            s_report = new Report
            {
                startedUtc = DateTime.UtcNow.ToString("O", Invariant),
                methodology = "Edit-mode natural Game-camera shadow executions with accepted XY culling always enabled; " +
                    "A updates all four cascades every frame; B permits short-term reuse of the far two cascades. " +
                    "ABBA BAAB blocks (first 4 for a 4-block run), " +
                    "per-block warm-up excluded. Main-thread elapsed time for the shadow pass, including native culling, command scheduling " +
                    "and Submit waits; not GPU time, pure CPU work, whole-frame time, render-thread time, or Player performance. " +
                    "Each block invalidates caches before warm-up. Updated/reused counts describe cascades, not GPU draw counts. " +
                    "TOD remains fixed. Other Editor views may render between samples. " +
                    "No forced GC, Camera.Render, scene save, or pipeline rebuild at A/B switches.",
                unityVersion = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), scene = camera.gameObject.scene.path,
                camera = camera.name, cameraGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(camera).ToString(),
                cameraInstanceId = camera.GetInstanceID(), platform = pipeline.Platform.ToString(),
                shadowSettings = JsonUtility.ToJson(pipeline.CascadeSetting), outputDirectory = directory,
                reuseStatusBefore = MaxShadowCasterPass.ReuseStatus,
                renderWidth = camera.pixelWidth, renderHeight = camera.pixelHeight,
                warmupPerBlock = warmupPerBlock, samplesPerBlock = samplesPerBlock, blockCount = blockCount,
                cameraPosition = camera.transform.position, cameraEulerAngles = camera.transform.eulerAngles,
                cameraProjection = camera.projectionMatrix, cameraWorldToCamera = camera.worldToCameraMatrix,
                effectiveSunDirection = (Vector3)sun.Direction, solarHour = s_solar ? s_solar.Hour : -1f,
                nearClip = camera.nearClipPlane, farClip = camera.farClipPlane,
                verticalFieldOfView = camera.fieldOfView, orthographic = camera.orthographic
            };
            LastReportDirectory = directory;
            s_rows = new List<Row>(samplesPerBlock * blockCount);
            s_previousOverride = MaxShadowCasterPass.ReuseOverride;
            s_previousDiagnostics = MaxShadowCasterPass.CaptureDiagnostics;
            s_previousGeometry = MaxShadowCasterPass.CaptureGeometry;
            s_block = s_inBlock = 0;
            s_finishPending = false;
            s_lastSampleTime = EditorApplication.timeSinceStartup;
            MaxShadowCasterPass.ReuseOverride = Modes[0];
            MaxShadowCasterPass.InvalidateCaches();
            MaxShadowCasterPass.CaptureDiagnostics = true;
            MaxShadowCasterPass.CaptureGeometry = false;
            MaxShadowCasterPass.CpuSampleRecorded += OnSample;
            s_running = true;
            EditorApplication.update += Update;
            UpdateStatus();
            EditorApplication.QueuePlayerLoopUpdate();
            return s_status + "; output=" + directory;
        }

        private static MaxBakedSolarGI FindSolar(Camera camera)
        {
            MaxBakedSolarGI chosen = null;
            foreach (var entry in UnityEngine.Object.FindObjectsOfType<MaxBakedSolarGI>())
                if (entry.isActiveAndEnabled && entry.gameObject.scene == camera.gameObject.scene &&
                    (!chosen || entry.GetInstanceID() < chosen.GetInstanceID())) chosen = entry;
            return chosen;
        }

        public static float PreviewHour => s_previewSolar ? s_previewSolar.Hour : float.NaN;

        /// <summary>Temporarily advances the active scene clock; never saves or marks a scene dirty.</summary>
        public static void StartPreview()
        {
            RequireIdle();
            if (PreviewRunning) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Run the temporary TOD preview in a ready Editor in Edit mode.");
            Scene active = SceneManager.GetActiveScene();
            MaxBakedSolarGI chosen = null;
            foreach (MaxBakedSolarGI solar in UnityEngine.Object.FindObjectsOfType<MaxBakedSolarGI>())
                if (solar && solar.isActiveAndEnabled && solar.gameObject.scene == active &&
                    (!chosen || solar.GetInstanceID() < chosen.GetInstanceID())) chosen = solar;
            if (!chosen) throw new InvalidOperationException("The active scene has no enabled MaxBakedSolarGI clock.");
            if (float.IsNaN(chosen.Hour) || float.IsInfinity(chosen.Hour))
                throw new InvalidOperationException("The active TOD clock needs a finite hour before previewing.");
            s_previewOriginalHour = chosen.Hour;
            s_previewSolar = chosen;
            s_previewLastTime = EditorApplication.timeSinceStartup;
            var views = new List<EditorWindow>();
            foreach (EditorWindow view in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (view && view.GetType().Name == "GameView") views.Add(view);
            s_previewGameViews = views.ToArray();
            EditorApplication.update += TickPreview;
            RepaintViews();
        }

        public static void StopPreview() { StopPreview(true); }

        private static void StopPreview(bool repaint)
        {
            EditorApplication.update -= TickPreview;
            MaxBakedSolarGI solar = s_previewSolar;
            s_previewSolar = null;
            s_previewGameViews = null;
            if (solar) solar.Hour = s_previewOriginalHour;
            if (solar && repaint) RepaintViews();
        }

        private static void TickPreview()
        {
            if (!s_previewSolar || !s_previewSolar.isActiveAndEnabled ||
                s_previewSolar.gameObject.scene != SceneManager.GetActiveScene() ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                StopPreview();
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            double elapsed = Math.Max(0.0, now - s_previewLastTime);
            s_previewLastTime = now;
            // Integrate actual Editor elapsed time; no synthetic frame-rate assumption.
            s_previewSolar.Hour = Mathf.Repeat(s_previewSolar.Hour + (float)(elapsed * 24.0 / (s_previewDayMinutes * 60.0)), 24.0f);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            if (s_previewGameViews != null)
                foreach (EditorWindow view in s_previewGameViews) if (view) view.Repaint();
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            // Restore before serialization so saving cannot persist the temporary preview hour.
            if (s_previewSolar && s_previewSolar.gameObject.scene == scene) StopPreview(false);
        }

        private static void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (s_previewSolar && s_previewSolar.gameObject.scene != next) StopPreview();
        }

        private static void OnSample(MaxShadowCasterPass.ShadowCpuSample sample)
        {
            if (!s_running || s_finishPending || sample.CameraId != s_report.cameraInstanceId) return;
            if (sample.CacheEnabled != Modes[s_block])
            {
                s_report.failure = "Another caller changed the A/B override during measurement.";
                s_finishPending = true;
                return;
            }
            s_lastSampleTime = EditorApplication.timeSinceStartup;
            if (s_inBlock >= s_report.warmupPerBlock)
                s_rows.Add(new Row { Block = s_block, Sample = s_inBlock - s_report.warmupPerBlock, Cpu = sample });
            ++s_inBlock;
            if (s_inBlock >= s_report.warmupPerBlock + s_report.samplesPerBlock)
            {
                ++s_block;
                s_inBlock = 0;
                if (s_block == s_report.blockCount) s_finishPending = true;
                else
                {
                    MaxShadowCasterPass.ReuseOverride = Modes[s_block];
                    MaxShadowCasterPass.InvalidateCaches();
                }
            }
        }

        private static void Update()
        {
            if (!s_running) return;
            try
            {
                if (s_finishPending) { Finish(string.IsNullOrEmpty(s_report.failure), s_report.failure); return; }
                if (!s_camera || !s_camera.isActiveAndEnabled || s_camera.gameObject.scene.path != s_report.scene ||
                    SceneManager.GetActiveScene().path != s_report.scene ||
                    s_camera.transform.position != s_report.cameraPosition ||
                    s_camera.worldToCameraMatrix != s_report.cameraWorldToCamera ||
                    s_camera.projectionMatrix != s_report.cameraProjection ||
                    s_camera.pixelWidth != s_report.renderWidth || s_camera.pixelHeight != s_report.renderHeight ||
                    GraphicsSettings.currentRenderPipeline != s_pipeline || s_pipeline.Platform != s_platform ||
                    !s_sun || RenderSettings.sun != s_sun || s_sun.transform.rotation != s_sunRotation ||
                    s_sun.intensity != s_sunIntensity || (s_solar && s_solar.Hour != s_report.solarHour))
                {
                    Finish(false, "Camera, resolution, pipeline, or TOD changed; results were not accepted.");
                    return;
                }
                if (EditorApplication.timeSinceStartup - s_lastSampleTime > 20.0)
                {
                    Finish(false, "No target-camera shadow execution for 20 seconds. Make the Game view visible and verify the camera/sun.");
                    return;
                }
                EditorApplication.QueuePlayerLoopUpdate();
                foreach (var view in s_gameViews) if (view) view.Repaint();
            }
            catch (Exception exception)
            {
                Finish(false, exception.ToString());
                Debug.LogException(exception);
            }
        }

        private static void UpdateStatus()
        {
            s_status = "Running block " + (s_block + 1) + "/" + s_report.blockCount + " " +
                (Modes[Math.Min(s_block, Modes.Length - 1)] ? "B" : "A") + "; warm-up/sample " + s_inBlock + "/" +
                (s_report.warmupPerBlock + s_report.samplesPerBlock) + "; recorded=" + s_rows.Count;
        }

        [MenuItem(MenuRoot + "Cancel CPU A-B")]
        public static void Cancel() { if (s_running) Finish(false, "Cancelled."); }

        private static void BeforeReload()
        {
            StopPreview(false);
            if (s_running) Finish(false, "Editor quit or assembly reload interrupted the run.");
        }
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            StopPreview();
            if (s_running) Finish(false, "Play mode changed during the Edit-mode benchmark.");
        }

        private static void Finish(bool success, string failure)
        {
            // Restore global overrides before any report I/O can throw.
            s_running = false;
            EditorApplication.update -= Update;
            MaxShadowCasterPass.CpuSampleRecorded -= OnSample;
            MaxShadowCasterPass.ReuseOverride = s_previousOverride;
            MaxShadowCasterPass.InvalidateCaches();
            MaxShadowCasterPass.CaptureDiagnostics = s_previousDiagnostics;
            MaxShadowCasterPass.CaptureGeometry = s_previousGeometry;
            s_report.success = success;
            s_report.failure = failure;
            s_report.finishedUtc = DateTime.UtcNow.ToString("O", Invariant);
            s_report.baseline = Summarize(false, -1);
            s_report.reused = Summarize(true, -1);
            s_report.reuseStatusAfter = MaxShadowCasterPass.ReuseStatus;
            for (int block = 0; block < s_report.blockCount; ++block) s_report.blocks.Add(Summarize(Modes[block], block));
            s_report.meanDifferenceMs = s_report.reused.totalMs.mean - s_report.baseline.totalMs.mean;
            s_report.meanDifferencePercent = s_report.baseline.totalMs.mean > 0
                ? s_report.meanDifferenceMs / s_report.baseline.totalMs.mean * 100.0 : 0.0;
            File.WriteAllText(Path.Combine(s_report.outputDirectory, "report.json"), JsonUtility.ToJson(s_report, true));
            WriteCsv();
            s_status = (success ? "Completed" : "Stopped: " + failure) + "; samples=" + s_rows.Count +
                "; report=" + Path.Combine(s_report.outputDirectory, "report.json");
            if (success)
                Debug.Log("Shadow reuse CPU A/B: A=" + s_report.baseline.totalMs.mean.ToString("F4", Invariant) +
                    " ms; B=" + s_report.reused.totalMs.mean.ToString("F4", Invariant) + " ms; delta=" +
                    s_report.meanDifferencePercent.ToString("F2", Invariant) + "%. " + s_status);
            else Debug.LogWarning(s_status);
            s_camera = null; s_pipeline = null; s_solar = null; s_sun = null; s_gameViews = null;
        }

        private static Summary Summarize(bool reused, int block)
        {
            var selected = new List<MaxShadowCasterPass.ShadowCpuSample>();
            foreach (var row in s_rows)
                if (row.Cpu.CacheEnabled == reused && (block < 0 || row.Block == block)) selected.Add(row.Cpu);
            int fallbacks = 0;
            foreach (var sample in selected) fallbacks += sample.FallbackCascadeCount;
            return new Summary
            {
                mode = reused ? "B - far-cascade reuse" : "A - all cascades every frame", block = block,
                fallbackCascadeExecutions = fallbacks,
                totalMs = SummarizeMetric(selected, s => s.TotalMilliseconds),
                setupMs = SummarizeMetric(selected, s => s.SetupMilliseconds),
                volumeMs = SummarizeMetric(selected, s => s.VolumeMilliseconds),
                cullMs = SummarizeMetric(selected, s => s.CullMilliseconds),
                drawScheduleMs = SummarizeMetric(selected, s => s.DrawMilliseconds),
                submitMs = SummarizeMetric(selected, s => s.SubmitMilliseconds),
                updatedCascades = SummarizeMetric(selected, s => s.UpdatedCascades),
                reusedCascades = SummarizeMetric(selected, s => s.ReusedCascades)
            };
        }

        private static Metric SummarizeMetric(List<MaxShadowCasterPass.ShadowCpuSample> samples,
            Func<MaxShadowCasterPass.ShadowCpuSample, double> select)
        {
            if (samples.Count == 0) return new Metric();
            var values = new double[samples.Count];
            double sum = 0.0;
            for (int i = 0; i < values.Length; ++i) { values[i] = select(samples[i]); sum += values[i]; }
            Array.Sort(values);
            double mean = sum / values.Length, variance = 0.0;
            foreach (double value in values) variance += (value - mean) * (value - mean);
            return new Metric
            {
                count = values.Length, mean = mean,
                median = values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) * 0.5 : values[values.Length / 2],
                p95 = values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)],
                standardDeviation = Math.Sqrt(variance / values.Length), minimum = values[0], maximum = values[values.Length - 1]
            };
        }

        private static void WriteCsv()
        {
            var text = new StringBuilder("block,mode,sample,total_ms,setup_ms,volume_ms,cull_ms,draw_schedule_ms,submit_ms,fallback_cascades,updated_cascades,reused_cascades\n");
            foreach (var row in s_rows)
            {
                var s = row.Cpu;
                text.Append(row.Block).Append(',').Append(s.CacheEnabled ? 'B' : 'A').Append(',').Append(row.Sample).Append(',')
                    .Append(s.TotalMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.SetupMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.VolumeMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.CullMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.DrawMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.SubmitMilliseconds.ToString("R", Invariant)).Append(',')
                    .Append(s.FallbackCascadeCount).Append(',').Append(s.UpdatedCascades).Append(',').Append(s.ReusedCascades).Append('\n');
            }
            File.WriteAllText(Path.Combine(s_report.outputDirectory, "samples.csv"), text.ToString());
        }
    }

    public sealed class MaxShadowReuseControls : EditorWindow
    {
        private string m_message;

        [MenuItem("MaxSRP/Shadow Reuse/Controls")]
        public static void Open()
        {
            GetWindow<MaxShadowReuseControls>(false, "Shadow Reuse", true).Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("阴影短期复用 A/B", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("XY 剔除保持开启");
            var pipeline = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
            bool fromConfiguration = !MaxShadowCasterPass.ReuseOverride.HasValue;
            bool reuse = MaxShadowCasterPass.ReuseOverride ?? (pipeline && pipeline.CascadeSetting.ShortTermReuse);
            string current = (fromConfiguration ? "配置：" : "临时覆盖：") +
                (reuse ? "B：复用远两级" : "A：四级每帧更新");
            if (pipeline) EditorGUILayout.LabelField("配置档位", pipeline.Platform.ToString());
            EditorGUILayout.LabelField("当前选择", current);
            bool busy = MaxShadowReuseBenchmark.IsRunning || MaxShadowCasterPass.CaptureDiagnostics || MaxShadowCasterPass.CaptureGeometry;
            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("A：四级实时")) Invoke(MaxShadowReuseBenchmark.UseBaseline);
                if (GUILayout.Button("B：远级复用")) Invoke(MaxShadowReuseBenchmark.UseReuse);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("恢复配置")) Invoke(MaxShadowReuseBenchmark.UseConfiguration);
                if (GUILayout.Button("使缓存失效")) Invoke(MaxShadowReuseBenchmark.InvalidateCache);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("缓存状态", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(MaxShadowCasterPass.ReuseStatus ?? "尚未渲染阴影。", MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("临时 TOD 预览", EditorStyles.boldLabel);
            MaxShadowReuseBenchmark.PreviewDayMinutes = EditorGUILayout.FloatField("一昼夜的实际分钟数", MaxShadowReuseBenchmark.PreviewDayMinutes);
            bool preview = MaxShadowReuseBenchmark.PreviewRunning;
            if (preview) EditorGUILayout.LabelField("预览时间", MaxShadowReuseBenchmark.PreviewHour.ToString("F2", CultureInfo.InvariantCulture));
            using (new EditorGUI.DisabledScope(busy && !preview))
            {
                if (GUILayout.Button(preview ? "停止并恢复原时间" : "开始预览"))
                    Invoke(preview ? (Action)MaxShadowReuseBenchmark.StopPreview : MaxShadowReuseBenchmark.StartPreview);
            }
            EditorGUILayout.HelpBox("预览不自动启动。停止、关闭窗口、保存场景或重载时恢复原时间。", MessageType.Info);
            if (!string.IsNullOrEmpty(m_message)) EditorGUILayout.HelpBox(m_message, MessageType.Warning);
        }

        private void Invoke(Action action)
        {
            try { action(); m_message = null; }
            catch (Exception exception) { m_message = exception.Message; }
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (!MaxShadowReuseBenchmark.IsRunning) Repaint();
        }

        private void OnDisable() { MaxShadowReuseBenchmark.StopPreview(); }
    }
}
#endif
