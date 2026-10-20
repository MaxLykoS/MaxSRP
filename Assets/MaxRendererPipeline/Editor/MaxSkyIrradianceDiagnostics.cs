using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    // One-shot readback of the actual owned GPU inputs, independent of global bindings.
    // A request is checked once after domain load, never polled during normal rendering.
    internal static class MaxSkyIrradianceDiagnostics
    {
        private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath,
            "../OfflineBaseline/SkyboxScene/irradiance_comparison"));
        private static double started;

        [Serializable]
        private sealed class TextureInfo
        {
            public string name, file, format, dimension;
            public int width, height, face;
        }

        [Serializable]
        private sealed class Snapshot
        {
            public string capturedUtc, scene, api, atmosphereJson, iblSettingsJson;
            public string rowOrigin = "ReadPixels bottom-left; RGBA float32 little-endian";
            public float hdrScale = LightPhotometry.HDRScale;
            public float sunIntensity;
            public Vector4 sunDirection;
            public Color solarIrradiance;
            public bool activeDiffuseMatchesGlobal;
            public List<TextureInfo> textures = new List<TextureInfo>();
        }

        [InitializeOnLoadMethod]
        private static void OnLoad()
        {
            if (File.Exists(Path.Combine(Output, "request.txt"))) Begin();
        }

        [MenuItem("MaxSRP/Diagnostics/Capture Sky Irradiance Inputs")]
        private static void Begin()
        {
            Directory.CreateDirectory(Output);
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update -= TryCapture;
            EditorApplication.update += TryCapture;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private static object Field(object instance, string name)
        {
            return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).GetValue(instance);
        }

        private static void TryCapture()
        {
            if (EditorApplication.isCompiling || EditorApplication.timeSinceStartup - started < 3) return;
            var pipeline = RenderPipelineManager.currentPipeline as MaxRenderPipeline;
            if (pipeline == null || !Ready(pipeline))
            {
                if (EditorApplication.timeSinceStartup - started > 90)
                {
                    File.WriteAllText(Path.Combine(Output, "error.txt"), "No initialized MaxSRP sky/IBL resources after 90 seconds.");
                    EditorApplication.update -= TryCapture;
                }
                return;
            }
            EditorApplication.update -= TryCapture;
            try
            {
                Capture(pipeline);
                File.Delete(Path.Combine(Output, "request.txt"));
                Debug.Log("MaxSRP sky irradiance inputs exported to " + Output);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Output, "error.txt"), error.ToString());
                Debug.LogException(error);
            }
        }

        private static bool Ready(MaxRenderPipeline pipeline)
        {
            var sky = (MaxProceduralSkyboxPass)Field(pipeline, "m_proceduralSkyboxPass");
            var ibl = (MaxIBLGIPass)Field(pipeline, "m_iblGIPass");
            return sky != null && ibl != null && sky.EnvironmentSky != null && sky.EnvironmentSky.IsCreated() &&
                (int)Field(ibl, "m_environmentRevision") >= 0 && (bool)Field(ibl, "m_brdfValid");
        }

        private static void Capture(MaxRenderPipeline pipeline)
        {
            var sky = (MaxProceduralSkyboxPass)Field(pipeline, "m_proceduralSkyboxPass");
            var ibl = (MaxIBLGIPass)Field(pipeline, "m_iblGIPass");
            var settings = (MaxRendererPipelineAsset)Field(pipeline, "m_setting");
            var outputs = (Array)Field(ibl, "m_outputs");
            var diffuse = (RenderTexture)Field(outputs.GetValue((int)Field(ibl, "m_active")), "Diffuse");
            var sun = SunLightData.Capture();
            var report = new Snapshot
            {
                capturedUtc = DateTime.UtcNow.ToString("O"),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                api = SystemInfo.graphicsDeviceType.ToString(),
                atmosphereJson = JsonUtility.ToJson(settings.AtmosphereScatteringSetting),
                iblSettingsJson = JsonUtility.ToJson(settings.SkyIBL),
                sunIntensity = sun.HasSun ? sun.Source.intensity : 0,
                sunDirection = sun.Direction,
                solarIrradiance = sun.LinearColor,
                activeDiffuseMatchesGlobal = Shader.GetGlobalTexture("_IBLDiffuse") == diffuse
            };
            ReadTexture(sky.EnvironmentSky, "unity_skyview.f32", -1, report);
            ReadTexture((RenderTexture)Field(sky, "m_transmittanceLUT"), "unity_transmittance.f32", -1, report);
            ReadTexture((RenderTexture)Field(sky, "m_multiscatteringLUT"), "unity_multiscattering.f32", -1, report);
            var environment = (RenderTexture)Field(ibl, "m_environment");
            for (int face = 0; face < 6; face++)
            {
                ReadTexture(environment, "unity_environment_" + face + ".f32", face, report);
                ReadTexture(diffuse, "unity_diffuse_" + face + ".f32", face, report);
            }
            // Publish completion only after every readback and file write succeeds.
            File.WriteAllText(Path.Combine(Output, "unity_snapshot.json"), JsonUtility.ToJson(report, true));
            if (File.Exists(Path.Combine(Output, "atmosphere_reference_request.txt")))
                CaptureReferenceProbes(sky, settings, report);
        }

        [Serializable] private sealed class ReferenceProbe
        {
            public string name, sunLabel, viewLabel;
            public float height, sunElevation;
            public Vector3 sunDirection, viewDirection;
            public Vector4 fullSky, singleSky, solarTransmittance;
        }

        [Serializable] private sealed class SourceDigest { public string path, sha256; }
        [Serializable] private sealed class ReferenceReport
        {
            public string capturedUtc;
            public string note = "GPU execution of production atmosphere functions, same owned LUTs. Single-scatter dispatch uses a private zero multi-scattering texture. No scene or pipeline changes.";
            public List<ReferenceProbe> probes = new List<ReferenceProbe>();
            public List<SourceDigest> sources = new List<SourceDigest>();
        }

        private static void CaptureReferenceProbes(MaxProceduralSkyboxPass sky, MaxRendererPipelineAsset settings, Snapshot snapshot)
        {
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/AtmosphereReference"));
            Directory.CreateDirectory(directory);
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MaxRendererPipeline/Editor/MaxAtmosphereReferenceProbes.compute");
            if (compute == null) throw new InvalidOperationException("Atmosphere reference compute shader is not ready.");
            var report = new ReferenceReport { capturedUtc = DateTime.UtcNow.ToString("O") };
            var current = new Vector3(snapshot.sunDirection.x, snapshot.sunDirection.y, snapshot.sunDirection.z).normalized;
            var horizontal = new Vector3(current.x, 0, current.z).normalized;
            if (horizontal.sqrMagnitude < .1f) horizontal = Vector3.forward;
            var side = Vector3.Cross(Vector3.up, horizontal);
            void AddViews(float height, Vector3 sun, string label, bool allViews)
            {
                float elevation = Mathf.Asin(Mathf.Clamp(sun.y, -1, 1)) * Mathf.Rad2Deg;
                Vector3 View(float angle, Vector3 azimuth) => Mathf.Cos(angle * Mathf.Deg2Rad) * azimuth + Mathf.Sin(angle * Mathf.Deg2Rad) * Vector3.up;
                var views = new[] { Vector3.up, View(45, horizontal), View(45, -horizontal), View(10, side),
                    View(1, horizontal), View(1, -horizontal), elevation > 0 ? sun : View(5, horizontal) };
                var names = new[] { "zenith", "toward45", "away45", "side10", "toward1", "away1", "sunward" };
                for (int i = 0; i < views.Length; i++)
                {
                    if (!allViews && i != 0 && i != 4 && i != 6) continue;
                    report.probes.Add(new ReferenceProbe { name = label + "_h" + height.ToString("F0") + "_" + names[i],
                        sunLabel = label, viewLabel = names[i], height = height, sunElevation = elevation,
                        sunDirection = sun, viewDirection = views[i].normalized });
                }
            }
            AddViews(2, current, "current", true);
            foreach (float elevation in new[] { 90f, 45f, 10f, 0f, -3f })
                AddViews(2, Mathf.Cos(elevation * Mathf.Deg2Rad) * horizontal + Mathf.Sin(elevation * Mathf.Deg2Rad) * Vector3.up,
                    "sun" + elevation.ToString("F0"), true);
            AddViews(1000, current, "current", false); AddViews(10000, current, "current", false);
            int count = report.probes.Count, kernel = compute.FindKernel("AtmosphereReferenceProbes");
            var viewsBuffer = new ComputeBuffer(count, 16); var sunsBuffer = new ComputeBuffer(count, 16);
            var skyBuffer = new ComputeBuffer(count, 16); var transBuffer = new ComputeBuffer(count, 16);
            var zero = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                zero.SetPixel(0, 0, Color.clear); zero.Apply(false, false);
                var views = new Vector4[count]; var suns = new Vector4[count];
                for (int i = 0; i < count; i++) { var p = report.probes[i];
                    views[i] = new Vector4(p.viewDirection.x, p.viewDirection.y, p.viewDirection.z, p.height);
                    suns[i] = new Vector4(p.sunDirection.x, p.sunDirection.y, p.sunDirection.z, 0); }
                viewsBuffer.SetData(views); sunsBuffer.SetData(suns);
                foreach (var f in settings.AtmosphereScatteringSetting.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(float)) compute.SetFloat(f.Name, (float)f.GetValue(settings.AtmosphereScatteringSetting));
                compute.SetVector("_SolarIrradiance", snapshot.solarIrradiance);
                compute.SetInt("_ProbeCount", count); compute.SetBuffer(kernel, "_ProbeViews", viewsBuffer);
                compute.SetBuffer(kernel, "_ProbeSuns", sunsBuffer); compute.SetBuffer(kernel, "_ProbeSky", skyBuffer);
                compute.SetBuffer(kernel, "_ProbeTransmittance", transBuffer);
                compute.SetTexture(kernel, "_TransmittanceLUT", (RenderTexture)Field(sky, "m_transmittanceLUT"));
                compute.SetTexture(kernel, "_MultiscatteringLUT", (RenderTexture)Field(sky, "m_multiscatteringLUT"));
                compute.Dispatch(kernel, (count + 63) / 64, 1, 1);
                var values = new Vector4[count]; var transmission = new Vector4[count];
                skyBuffer.GetData(values); transBuffer.GetData(transmission);
                for (int i = 0; i < count; i++) { report.probes[i].fullSky = values[i]; report.probes[i].solarTransmittance = transmission[i]; }
                compute.SetTexture(kernel, "_MultiscatteringLUT", zero);
                compute.Dispatch(kernel, (count + 63) / 64, 1, 1); skyBuffer.GetData(values);
                for (int i = 0; i < count; i++) report.probes[i].singleSky = values[i];
                foreach (string name in new[] { "Helper.hlsl", "Scattering.hlsl", "RayMarching.hlsl" })
                {
                    string path = "Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/" + name;
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        report.sources.Add(new SourceDigest { path = path, sha256 = BitConverter.ToString(hash.ComputeHash(
                            File.ReadAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "..", path))))).Replace("-", "").ToLowerInvariant() });
                }
                foreach (string name in new[] { "unity_skyview.f32", "unity_transmittance.f32", "unity_multiscattering.f32" })
                    File.Copy(Path.Combine(Output, name), Path.Combine(directory, name), true);
                File.WriteAllText(Path.Combine(directory, "unity_snapshot.json"), JsonUtility.ToJson(snapshot, true));
                File.WriteAllText(Path.Combine(directory, "unity_probes.json"), JsonUtility.ToJson(report, true));
                File.Delete(Path.Combine(Output, "atmosphere_reference_request.txt"));
            }
            finally
            {
                viewsBuffer.Release(); sunsBuffer.Release(); skyBuffer.Release(); transBuffer.Release();
                UnityEngine.Object.DestroyImmediate(zero);
            }
        }

        private static void ReadTexture(RenderTexture source, string filename, int face, Snapshot report)
        {
            var previous = RenderTexture.active;
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                if (face < 0) RenderTexture.active = source;
                else Graphics.SetRenderTarget(source, 0, (CubemapFace)face);
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                pixels.Apply(false, false);
                using (var writer = new BinaryWriter(File.Create(Path.Combine(Output, filename))))
                    foreach (Color color in pixels.GetPixels())
                    {
                        writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
                    }
                report.textures.Add(new TextureInfo { name = source.name, file = filename,
                    width = source.width, height = source.height, face = face,
                    format = source.format.ToString(), dimension = source.dimension.ToString() });
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }
    }
}
