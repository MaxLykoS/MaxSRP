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
    /// <summary>Short, separate diagnostic output check; does not change the natural VT regression gates.</summary>
    [InitializeOnLoad]
    public static class MaxTerrainDebugValidation
    {
        const string RunKey = "MaxTerrain.DebugValidation.Run";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly List<string> errors = new List<string>();
        static readonly List<Capture> captures = new List<Capture>();
        static MeshComparison rig;
        static FieldInfo outputHook;
        static Action<CommandBuffer, Camera, RenderTexture, RenderTexture, Vector4> callback;
        static object temporalPass, history;
        static RenderTexture motion;
        static int stage, frames, pending, invalidations;
        static bool attached;
        static double lastFrameTime;

        [Serializable] sealed class Capture
        {
            public string name, error;
            public int surfaceDebug, historyInvalidations, pixels, whitePixels, nonBlackPixels, saturatedPixels;
            public float exposure, toneMapping, dithering;
            public bool sameTemporalPass, sameHistory, sameMotion, complete, notOverexposed;
        }
        [Serializable] sealed class Report
        {
            public bool passed;
            public string error;
            public List<Capture> phases = new List<Capture>();
            public List<string> loggedErrors = new List<string>();
        }

        static MaxTerrainDebugValidation()
        {
            EditorApplication.update += Update;
            Application.logMessageReceived += Log;
        }

        static string OutputDirectory => Path.GetFullPath(
            Environment.GetEnvironmentVariable("MAX_TERRAIN_DEBUG_OUTPUT") ??
            Environment.GetEnvironmentVariable("MAX_TERRAIN_VALIDATION_OUTPUT") ?? "OfflineBaseline/MaxTerrain/Debug01");

        public static void DebugBatch()
        {
            SessionState.SetBool(RunKey, true);
            EditorSceneManager.OpenScene(MeshTerrainWorkflow.AuthoringScene);
            EditorApplication.EnterPlaymode();
        }

        static object Field(object owner, string name) => owner?.GetType().GetField(name, Fields)?.GetValue(owner);
        static object History(Camera camera, out object pass)
        {
            pass = Field(RenderPipelineManager.currentPipeline, "m_temporalAA");
            var histories = Field(pass, "m_histories") as IDictionary;
            return histories != null && histories.Contains(camera.GetInstanceID()) ? histories[camera.GetInstanceID()] : null;
        }

        static void Attach()
        {
            rig = Object.FindObjectOfType<MeshComparison>();
            if (!rig) return;
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if ((type = assembly.GetType("MaxSRP.MaxColorOutputPass", false)) != null) break;
            outputHook = type?.GetField("EditorOutputCapture", BindingFlags.Static | BindingFlags.Public);
            if (outputHook == null) throw new InvalidOperationException("The host color-output capture hook is missing.");
            errors.Clear(); captures.Clear(); stage = frames = pending = invalidations = 0;
            temporalPass = history = null; motion = null;
            rig.surfaceDebug = 0;
            callback = CaptureOutput;
            outputHook.SetValue(null, Delegate.Combine(outputHook.GetValue(null) as Delegate, callback));
            MaxRenderBridge.TemporalHistoryInvalidated += HistoryInvalidated;
            attached = true; lastFrameTime = EditorApplication.timeSinceStartup;
        }

        static void HistoryInvalidated(Camera camera)
        { if (rig && camera == rig.directCamera) invalidations++; }

        static void Update()
        {
            if (!SessionState.GetBool(RunKey, false) || !EditorApplication.isPlaying) return;
            try
            {
                if (!attached) Attach();
                if (stage == 3 && pending == 0) { Finish(null); return; }
                if (attached && EditorApplication.timeSinceStartup - lastFrameTime > 180)
                    Finish("Diagnostic rendering/readback did not complete for 180 seconds.");
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        static void CaptureOutput(CommandBuffer commands, Camera camera, RenderTexture color, RenderTexture depth, Vector4 parameters)
        {
            if (!rig || camera != rig.directCamera || stage >= 3) return;
            lastFrameTime = EditorApplication.timeSinceStartup;
            if (++frames < 24) return;
            string name = stage == 0 ? "lit-before" : stage == 1 ? "albedo-f1" : "lit-restored";
            var currentHistory = History(camera, out object currentPass);
            var currentMotion = Field(currentHistory, "Motion") as RenderTexture;
            if (stage == 0) { temporalPass = currentPass; history = currentHistory; motion = currentMotion; }
            var item = new Capture
            {
                name = name, surfaceDebug = rig.surfaceDebug, exposure = parameters.x,
                toneMapping = parameters.y, dithering = parameters.w, historyInvalidations = invalidations,
                sameTemporalPass = ReferenceEquals(temporalPass, currentPass) && currentPass != null,
                sameHistory = ReferenceEquals(history, currentHistory) && currentHistory != null,
                sameMotion = motion && motion == currentMotion
            };
            captures.Add(item);
            int width = camera.targetTexture ? camera.targetTexture.width : Mathf.Max(Screen.width, Mathf.CeilToInt(camera.pixelRect.xMax));
            int height = camera.targetTexture ? camera.targetTexture.height : Mathf.Max(Screen.height, Mathf.CeilToInt(camera.pixelRect.yMax));
            var screenshot = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "MaxTerrain diagnostic final color evidence" };
            screenshot.Create();
            commands.Blit(camera.targetTexture ? new RenderTargetIdentifier(camera.targetTexture) :
                new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), screenshot);
            pending++;
            commands.RequestAsyncReadback(screenshot, 0, TextureFormat.RGBA32, request =>
            {
                Texture2D image = null;
                try
                {
                    if (request.hasError) item.error = "Final color readback failed.";
                    else
                    {
                        var pixels = request.GetData<Color32>();
                        item.pixels = pixels.Length;
                        for (int i = 0; i < pixels.Length; ++i)
                        {
                            var pixel = pixels[i];
                            if (pixel.r >= 250 && pixel.g >= 250 && pixel.b >= 250) item.whitePixels++;
                            if (pixel.r >= 254 || pixel.g >= 254 || pixel.b >= 254) item.saturatedPixels++;
                            if ((int)pixel.r + pixel.g + pixel.b > 6) item.nonBlackPixels++;
                        }
                        item.notOverexposed = item.whitePixels < item.pixels / 4 && item.nonBlackPixels > item.pixels / 2;
                        image = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                        image.LoadRawTextureData(request.GetData<byte>()); image.Apply(false);
                        Directory.CreateDirectory(OutputDirectory);
                        File.WriteAllBytes(Path.Combine(OutputDirectory, name + ".png"), image.EncodeToPNG());
                    }
                }
                catch (Exception exception) { item.error = exception.ToString(); }
                finally
                {
                    if (image) Object.Destroy(image);
                    screenshot.Release(); Object.Destroy(screenshot); item.complete = true; pending--;
                }
            });
            // Restore the borrowed final-output command buffer's destination;
            // the validation copy must not become a new pipeline output target.
            if (camera.targetTexture) commands.SetRenderTarget(camera.targetTexture);
            else commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth);
            stage++; frames = 0;
            if (stage == 1) rig.surfaceDebug = 1;
            else if (stage == 2) rig.surfaceDebug = 0;
        }

        static void Log(string message, string stack, LogType type)
        {
            if (SessionState.GetBool(RunKey, false) && EditorApplication.isPlaying &&
                (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) errors.Add(message + "\n" + stack);
        }

        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Finish(string failure)
        {
            int exitCode = 1;
            var report = new Report { phases = new List<Capture>(captures), loggedErrors = new List<string>(errors) };
            try
            {
                Require(string.IsNullOrEmpty(failure), failure);
                Require(captures.Count == 3 && pending == 0, "All three diagnostic phases must complete.");
                foreach (var item in captures)
                    Require(item.complete && string.IsNullOrEmpty(item.error) && item.sameTemporalPass && item.sameHistory && item.sameMotion,
                        "The host temporal resources changed or image capture failed: " + item.name + ": " + item.error);
                Require(captures[0].exposure > 1 && captures[0].toneMapping > 0, "Normal lighting did not use the configured HDR output.");
                Require(Mathf.Abs(captures[1].exposure - 1) < 1e-5f && captures[1].toneMapping == 0 && captures[1].dithering == 0,
                    "F1 did not use neutral diagnostic output parameters.");
                Require(captures[1].notOverexposed, "F1 output is mostly white or black.");
                Require(Mathf.Abs(captures[2].exposure - captures[0].exposure) < 1e-4f && captures[2].toneMapping == captures[0].toneMapping,
                    "Restoring lighting did not restore exposure/tone mapping.");
                Require(captures[1].historyInvalidations > captures[0].historyInvalidations &&
                    captures[2].historyInvalidations > captures[1].historyInvalidations,
                    "Diagnostic changes did not invalidate the existing shared temporal history.");
                Require(errors.Count == 0, "Error logs were emitted.");
                report.passed = true; exitCode = 0;
            }
            catch (Exception exception) { report.error = exception.ToString(); }
            finally
            {
                SessionState.SetBool(RunKey, false);
                if (rig) rig.surfaceDebug = 0;
                MaxRenderBridge.TemporalHistoryInvalidated -= HistoryInvalidated;
                if (outputHook != null && callback != null)
                    outputHook.SetValue(null, Delegate.Remove(outputHook.GetValue(null) as Delegate, callback));
                try
                {
                    Directory.CreateDirectory(OutputDirectory);
                    File.WriteAllText(Path.Combine(OutputDirectory, "debug.json"), JsonUtility.ToJson(report, true));
                }
                catch (Exception outputError) { Debug.LogException(outputError); exitCode = 1; }
                finally { EditorApplication.Exit(exitCode); }
            }
        }
    }
}
