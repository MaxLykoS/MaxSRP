using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    // One-shot, private GPU resources. Does not change cameras, settings or global textures.
    internal static class MaxSkyHaloDiagnostics
    {
        private const int Size = 512;
        private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/SkyHalo"));
        private static double started;
        [Serializable] private sealed class Digest { public string path, sha256; }
        [Serializable] private sealed class Report
        {
            public string capturedUtc, scene, api, camera, atmosphereJson, colorOutputJson;
            public string rowOrigin = "f32: bottom-left RGBA float32; PNG: top-left display-encoded RGB";
            public string scope = "Sun-centred 40-degree crop at the selected camera altitude. Shared production transmittance/MS LUTs. Raw files exclude the disk. Display images include the same disk, FP16 scene rounding, exposure, tone mapping, dithering and 8-bit quantization. This is not a converged transport reference.";
            public int size = Size;
            public float cameraY, effectiveAltitude, verticalFov = 40;
            public Vector3 forward, right, up;
            public Color solarIrradiance;
            public string[] variants = { "lut256x128", "direct32", "direct128", "quadratic128" };
            public List<Digest> sources = new List<Digest>();
        }
        private static object Field(object instance, string name) => instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(instance);

        [InitializeOnLoadMethod] private static void OnLoad()
        {
            if (File.Exists(Path.Combine(Output, "request.txt"))) Begin();
        }
        [MenuItem("MaxSRP/Diagnostics/Compare Sky Halo Sampling")]
        private static void Begin()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "request.txt"), "Capture on the next initialized editor frame.");
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update -= TryCapture;
            EditorApplication.update += TryCapture;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
        private static void TryCapture()
        {
            if (EditorApplication.isCompiling || EditorApplication.timeSinceStartup - started < 3) return;
            var pipeline = RenderPipelineManager.currentPipeline as MaxRenderPipeline;
            var sky = pipeline != null ? (MaxProceduralSkyboxPass)Field(pipeline, "m_proceduralSkyboxPass") : null;
            if (sky == null || sky.EnvironmentSky == null || !sky.EnvironmentSky.IsCreated())
            {
                if (EditorApplication.timeSinceStartup - started < 90) return;
                EditorApplication.update -= TryCapture;
                File.WriteAllText(Path.Combine(Output, "error.txt"), "No initialized MaxSRP atmosphere after 90 seconds.");
                return;
            }
            EditorApplication.update -= TryCapture;
            try
            {
                Capture(pipeline, sky);
                File.Delete(Path.Combine(Output, "request.txt"));
                Debug.Log("MaxSRP sky halo comparison exported to " + Output);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Output, "error.txt"), error.ToString());
                Debug.LogException(error);
            }
        }
        private static RenderTexture Target(int width, int height, RenderTextureFormat format, bool randomWrite)
        {
            var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { enableRandomWrite = randomWrite, hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (!rt.Create()) { UnityEngine.Object.DestroyImmediate(rt); throw new InvalidOperationException("Halo target allocation failed."); }
            return rt;
        }
        private static void Capture(MaxRenderPipeline pipeline, MaxProceduralSkyboxPass sky)
        {
            var settings = (MaxRendererPipelineAsset)Field(pipeline, "m_setting");
            var sun = (SunLightData)Field(sky, "m_sun");
            Camera camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null || !sun.HasSun) throw new InvalidOperationException("Need an active camera and atmosphere Sun.");
            var asset = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MaxRendererPipeline/Editor/MaxSkyHaloDiagnostics.compute");
            if (asset == null) throw new InvalidOperationException("Sky halo compute shader is not imported yet.");
            var report = new Report { capturedUtc = DateTime.UtcNow.ToString("O"),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                api = SystemInfo.graphicsDeviceType.ToString(), camera = camera.name,
                atmosphereJson = JsonUtility.ToJson(settings.AtmosphereScatteringSetting),
                colorOutputJson = JsonUtility.ToJson(settings.ColorOutput), cameraY = camera.transform.position.y,
                effectiveAltitude = Mathf.Max(1, camera.transform.position.y - settings.AtmosphereScatteringSetting._SeaLevel),
                solarIrradiance = sun.LinearColor };
            report.forward = new Vector3(sun.AtmosphereDirection.x, sun.AtmosphereDirection.y, sun.AtmosphereDirection.z).normalized;
            report.right = Vector3.Cross(Mathf.Abs(report.forward.y) > .99f ? Vector3.forward : Vector3.up, report.forward).normalized;
            report.up = Vector3.Cross(report.forward, report.right).normalized;
            ComputeShader compute = null;
            Material material = null;
            RenderTexture lut = null, raw = null, mapped = null, display = null;
            var previous = RenderTexture.active;
            try
            {
                compute = UnityEngine.Object.Instantiate(asset);
                compute.hideFlags = HideFlags.HideAndDontSave;
                material = new Material(settings.AtmosphereScatteringSetting.SkyviewShader) { hideFlags = HideFlags.HideAndDontSave };
                lut = Target(256, 128, RenderTextureFormat.ARGBFloat, false);
                raw = Target(Size, Size, RenderTextureFormat.ARGBFloat, true);
                mapped = Target(Size, Size, RenderTextureFormat.ARGBFloat, true);
                display = Target(Size, Size, RenderTextureFormat.ARGB32, true);
                var trans = (RenderTexture)Field(sky, "m_transmittanceLUT");
                var multi = (RenderTexture)Field(sky, "m_multiscatteringLUT");
                sky.GetType().GetMethod("SetupAtmosphereParameters", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(sky, new object[] { material });
                material.SetTexture("_TransmittanceLUT", trans);
                material.SetTexture("_MultiscatteringLUT", multi);
                material.SetFloat("_AtmosphereCameraY", report.cameraY);
                Graphics.Blit(null, lut, material);
                SaveFloat(lut, "sky_lut.f32");
                int kernel = compute.FindKernel("SkyHaloComparison");
                foreach (var field in settings.AtmosphereScatteringSetting.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.FieldType == typeof(float)) compute.SetFloat(field.Name, (float)field.GetValue(settings.AtmosphereScatteringSetting));
                compute.SetVector("_SolarIrradiance", sun.LinearColor);
                compute.SetVector("_AtmosphereSunDirection", sun.AtmosphereDirection);
                compute.SetVector("_HaloForward", report.forward); compute.SetVector("_HaloRight", report.right); compute.SetVector("_HaloUp", report.up);
                compute.SetFloat("_HaloCameraY", report.cameraY);
                compute.SetFloat("_HaloTanHalfFov", Mathf.Tan(report.verticalFov * Mathf.Deg2Rad * .5f));
                compute.SetInt("_HaloSize", Size);
                compute.SetVector("_ColorOutputParameters", new Vector4(settings.ColorOutput.GetExposureMultiplier(),
                    (float)settings.ColorOutput.ToneMapping, 0, settings.ColorOutput.Dithering ? 2 : 0));
                compute.SetTexture(kernel, "_SkyviewLUT", lut); compute.SetTexture(kernel, "_TransmittanceLUT", trans);
                compute.SetTexture(kernel, "_MultiscatteringLUT", multi); compute.SetTexture(kernel, "_HaloRaw", raw);
                compute.SetTexture(kernel, "_HaloMapped", mapped); compute.SetTexture(kernel, "_HaloDisplay", display);
                for (int mode = 0; mode < report.variants.Length; ++mode)
                {
                    compute.SetInt("_HaloMode", mode);
                    // Small dispatches limit the duration of any individual GPU workload.
                    for (int row = 0; row < Size; row += 32)
                    { compute.SetInt("_HaloRow", row); compute.Dispatch(kernel, Size / 8, 4, 1); }
                    SaveFloat(raw, report.variants[mode] + "_sky.f32");
                    SaveFloat(mapped, report.variants[mode] + "_mapped.f32");
                    SavePng(display, report.variants[mode] + ".png");
                }
                foreach (string path in new[] { "Assets/MaxRendererPipeline/Editor/MaxSkyHaloDiagnostics.compute",
                    "Assets/MaxRendererPipeline/Editor/MaxSkyHaloDiagnostics.cs",
                    "Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl",
                    "Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl",
                    "Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl",
                    "Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl",
                    "Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl" })
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        report.sources.Add(new Digest { path = path, sha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant() });
                File.WriteAllText(Path.Combine(Output, "capture.json"), JsonUtility.ToJson(report, true));
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (var rt in new[] { lut, raw, mapped, display }) if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (compute != null) UnityEngine.Object.DestroyImmediate(compute);
            }
        }
        private static void SaveFloat(RenderTexture source, string file)
        {
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                pixels.Apply(false, false);
                using (var writer = new BinaryWriter(File.Create(Path.Combine(Output, file))))
                    foreach (Color c in pixels.GetPixels()) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
        }
        private static void SavePng(RenderTexture source, string file)
        {
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                pixels.Apply(false, false);
                File.WriteAllBytes(Path.Combine(Output, file), pixels.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
        }
    }
}
