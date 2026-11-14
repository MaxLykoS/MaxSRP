#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    /// <summary>Exercises real cache invalidation between normal Game-camera renders.</summary>
    [InitializeOnLoad]
    public static class MaxShadowReuseLifecycleValidation
    {
        [Serializable] private sealed class Check
        {
            public string name;
            public bool passed;
            public int updated, reused;
            public bool atlasCreated;
        }
        [Serializable] private sealed class Report
        {
            public string utc, scene, camera, unityVersion, failure, reportPath;
            public bool success, restored, sceneWasDirty, sceneIsDirtyAfter;
            public Vector3 originalPosition;
            public Quaternion originalRotation;
            public float originalHour;
            public List<Check> checks = new List<Check>();
        }
        private enum Step { InitialReuse, HourChanged, HourRestored, CameraMoved, CameraRestored, Invalidated, AtlasReleased }
        private static Camera s_camera;
        private static MaxBakedSolarGI s_solar;
        private static EditorWindow s_gameView;
        private static Scene s_scene;
        private static Report s_report;
        private static Step s_step;
        private static bool s_running, s_ready, s_waitForReuse;
        private static bool? s_oldOverride;
        private static bool s_oldDiagnostics, s_oldGeometry;
        private static int s_waitFrames, s_width, s_height;
        private static double s_stageStarted;
        private static Vector3 s_expectedPosition;
        private static float s_expectedHour;
        private static Matrix4x4 s_projection;
        private static MaxShadowCasterPass.ShadowCpuSample s_sample;
        private static RenderTexture s_atlas;
        private static string s_status = "Not started.";
        public static bool IsRunning => s_running;
        public static string Status => s_status;

        static MaxShadowReuseLifecycleValidation()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
        }

        [MenuItem("MaxSRP/Validation/Shadow Reuse/Cache Lifecycle")]
        public static void RunMenu() => Begin();

        public static void Begin(int cameraInstanceId = 0)
        {
            if (s_running || MaxShadowCasterPass.CaptureDiagnostics || MaxShadowCasterPass.CaptureGeometry)
                throw new InvalidOperationException("Finish the active shadow diagnostic first.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run cache lifecycle validation in a ready Editor in Edit Mode.");
            var camera = cameraInstanceId == 0 ? Camera.main : EditorUtility.InstanceIDToObject(cameraInstanceId) as Camera;
            if (!camera || camera.cameraType != CameraType.Game || !camera.isActiveAndEnabled || !camera.gameObject.scene.IsValid())
                throw new InvalidOperationException("An enabled scene Game camera is required.");
            MaxBakedSolarGI solar = null;
            foreach (var entry in UnityEngine.Object.FindObjectsOfType<MaxBakedSolarGI>())
                if (entry.isActiveAndEnabled && entry.gameObject.scene == camera.gameObject.scene &&
                    entry.Sun == RenderSettings.sun && (!solar || entry.GetInstanceID() < solar.GetInstanceID())) solar = entry;
            if (!solar) throw new InvalidOperationException("The target camera needs an active solar TOD component for this test.");
            EditorWindow gameView = null;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window && window.GetType().Name == "GameView") { gameView = window; break; }
            if (!gameView) throw new InvalidOperationException("Show a Game view before running the test.");

            s_camera = camera; s_solar = solar; s_scene = camera.gameObject.scene; s_gameView = gameView;
            s_width = camera.pixelWidth; s_height = camera.pixelHeight; s_projection = camera.projectionMatrix;
            s_expectedPosition = camera.transform.position; s_expectedHour = solar.Hour;
            s_report = new Report { utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                scene = s_scene.path, camera = camera.name, originalPosition = s_expectedPosition,
                originalRotation = camera.transform.rotation, originalHour = s_expectedHour, sceneWasDirty = s_scene.isDirty };
            s_oldOverride = MaxShadowCasterPass.ReuseOverride;
            s_oldDiagnostics = MaxShadowCasterPass.CaptureDiagnostics; s_oldGeometry = MaxShadowCasterPass.CaptureGeometry;
            s_running = true;
            MaxShadowCasterPass.ReuseOverride = true;
            MaxShadowCasterPass.CaptureDiagnostics = true; MaxShadowCasterPass.CaptureGeometry = false;
            MaxShadowCasterPass.CpuSampleRecorded += OnSample;
            EditorApplication.update += Update;
            MaxShadowCasterPass.InvalidateCaches();
            Arm(Step.InitialReuse, true);
        }

        [MenuItem("MaxSRP/Validation/Shadow Reuse/Cancel Cache Lifecycle")]
        public static void Cancel() { if (s_running) Finish(false, "Cancelled, interrupted by reload, or Editor quitting."); }

        // Capture only the first qualifying target frame after an action. Never
        // mutate scene objects from this callback, which precedes the camera's GBuffer.
        private static void OnSample(MaxShadowCasterPass.ShadowCpuSample sample)
        {
            if (!s_running || !s_camera || sample.CameraId != s_camera.GetInstanceID() || s_ready) return;
            ++s_waitFrames;
            if (s_waitForReuse && sample.ReusedCascades <= 0) return;
            s_sample = sample; s_atlas = MaxShadowCasterPass.DiagnosticAtlas; s_ready = true;
        }

        private static void Arm(Step step, bool waitForReuse = false)
        {
            s_step = step; s_waitForReuse = waitForReuse; s_waitFrames = 0; s_ready = false;
            s_stageStarted = EditorApplication.timeSinceStartup; s_status = "Waiting: " + step;
            EditorApplication.QueuePlayerLoopUpdate();
            if (s_gameView) s_gameView.Repaint();
        }

        private static void Update()
        {
            if (!s_running) return;
            try
            {
                if (!s_camera || !s_solar || EditorApplication.isPlayingOrWillChangePlaymode ||
                    s_camera.gameObject.scene != s_scene || SceneManager.GetActiveScene() != s_scene ||
                    s_camera.transform.position != s_expectedPosition || s_camera.transform.rotation != s_report.originalRotation ||
                    s_solar.Hour != s_expectedHour || s_camera.projectionMatrix != s_projection ||
                    s_camera.pixelWidth != s_width || s_camera.pixelHeight != s_height)
                    throw new InvalidOperationException("Camera, scene, output, or TOD changed outside the test.");
                if (EditorApplication.timeSinceStartup - s_stageStarted > 20 || s_waitFrames > 128)
                    throw new TimeoutException("No qualifying target shadow frame at " + s_step);
                if (s_ready) Advance();
                if (s_running)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    if (s_gameView) s_gameView.Repaint();
                }
            }
            catch (Exception exception) { Finish(false, exception.ToString()); }
        }

        private static void Advance()
        {
            bool created = s_atlas && s_atlas.IsCreated();
            bool passed = s_sample.CacheEnabled && created && (s_waitForReuse
                ? s_sample.ReusedCascades > 0 : s_sample.UpdatedCascades == 4 && s_sample.ReusedCascades == 0);
            s_report.checks.Add(new Check { name = s_step.ToString(), passed = passed,
                updated = s_sample.UpdatedCascades, reused = s_sample.ReusedCascades, atlasCreated = created });
            if (!passed) throw new InvalidOperationException(s_step + " did not produce the required cache update/reuse.");
            switch (s_step)
            {
                case Step.InitialReuse:
                    s_expectedHour = Mathf.Repeat(s_report.originalHour + 1f, 24f); s_solar.Hour = s_expectedHour;
                    Arm(Step.HourChanged); break;
                case Step.HourChanged:
                    s_expectedHour = s_report.originalHour; s_solar.Hour = s_expectedHour;
                    Arm(Step.HourRestored, true); break;
                case Step.HourRestored:
                    s_expectedPosition = s_report.originalPosition + new Vector3(100f, 0f, 0f);
                    s_camera.transform.position = s_expectedPosition; Arm(Step.CameraMoved); break;
                case Step.CameraMoved:
                    s_expectedPosition = s_report.originalPosition; s_camera.transform.position = s_expectedPosition;
                    Arm(Step.CameraRestored); break;
                case Step.CameraRestored:
                    MaxShadowCasterPass.InvalidateCaches(); Arm(Step.Invalidated); break;
                case Step.Invalidated:
                    s_atlas.Release(); Arm(Step.AtlasReleased); break;
                case Step.AtlasReleased:
                    Finish(true, null); break;
            }
        }

        private static void Finish(bool success, string failure)
        {
            s_running = false;
            EditorApplication.update -= Update;
            MaxShadowCasterPass.CpuSampleRecorded -= OnSample;
            try
            {
                if (s_solar) s_solar.Hour = s_report.originalHour;
                if (s_camera) s_camera.transform.SetPositionAndRotation(s_report.originalPosition, s_report.originalRotation);
            }
            catch (Exception exception) { success = false; failure = (failure ?? "") + " Restore: " + exception; }
            finally
            {
                MaxShadowCasterPass.ReuseOverride = s_oldOverride;
                MaxShadowCasterPass.CaptureDiagnostics = s_oldDiagnostics; MaxShadowCasterPass.CaptureGeometry = s_oldGeometry;
                MaxShadowCasterPass.InvalidateCaches();
                var pipeline = RenderPipelineManager.currentPipeline as MaxRenderPipeline;
                if (pipeline != null && s_camera) pipeline.ResetTemporalHistory(s_camera);
            }
            s_report.restored = s_camera && s_solar && s_solar.Hour == s_report.originalHour &&
                s_camera.transform.position == s_report.originalPosition && s_camera.transform.rotation == s_report.originalRotation;
            s_report.sceneIsDirtyAfter = s_scene.IsValid() && s_scene.isDirty;
            // Do not save the scene or clear its dirty flag; concurrent user edits must survive.
            s_report.success = success && s_report.restored; s_report.failure = failure;
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../OfflineBaseline/ShadowReuse/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-lifecycle"));
            Directory.CreateDirectory(directory); s_report.reportPath = Path.Combine(directory, "lifecycle.json");
            File.WriteAllText(s_report.reportPath, JsonUtility.ToJson(s_report, true));
            s_status = (s_report.success ? "Passed" : "Failed: " + failure) + "; restored=" + s_report.restored + "; report=" + s_report.reportPath;
            if (s_report.success) Debug.Log("Shadow reuse lifecycle: " + s_status); else Debug.LogWarning(s_status);
            s_camera = null; s_solar = null; s_gameView = null; s_atlas = null;
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
#endif
