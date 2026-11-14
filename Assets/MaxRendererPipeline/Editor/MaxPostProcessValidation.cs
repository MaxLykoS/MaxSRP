#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Runs the production GPU passes against synthetic linear HDR inputs. No
    // rendering context is fabricated, and no project scene is loaded or saved.
    public static class MaxPostProcessValidation
    {
        [Serializable] private sealed class Check
        {
            public string name, expected, actual;
            public bool passed;
        }
        [Serializable] private sealed class Report
        {
            public string unityVersion, gpu, graphicsAPI, utc;
            public bool success;
            public List<Check> checks = new List<Check>();
        }

        private static Report s_report;
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private const float HDRScale = 100000f;
        private const string ConfigurationPath = "Assets/MaxRendererPipeline/DefaultPostProcess.asset";

        public static void RunBatch()
        {
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        [MenuItem("MaxSRP/Validation/Post Processing (GPU)")]
        public static void Run()
        {
            s_report = new Report
            {
                unityVersion = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName,
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O")
            };
            MaxAutoExposurePass exposure = null;
            MaxBloomPass bloom = null;
            try
            {
                Require("gpu.compute", SystemInfo.supportsComputeShaders, "Compute support");
                Require("gpu.float-target", SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat), "RGBA32F target");
                var config = AssetDatabase.LoadAssetAtPath<MaxPostProcessConfig>(ConfigurationPath);
                Require("configuration.asset", config != null, ConfigurationPath);
                Require("configuration.shader-references", config.ExposureCompute != null && config.BloomShader != null && config.OutputShader != null,
                    "Serialized exposure, bloom and output shader references");
                var defaults = Own(ScriptableObject.CreateInstance<MaxPostProcessConfig>());
                var pcDefaults = defaults.GetProfile(MaxRenderPlatform.PC);
                Require("configuration.pc-default", pcDefaults != null &&
                    pcDefaults.Exposure.Mode == ExposureMode.Histogram && !pcDefaults.ColorGrading.Enabled &&
                    pcDefaults.Film.Mode == ToneMappingMode.UEFilm, "New PC defaults: Histogram, neutral grading disabled, UEFilm");
                var serialized = new SerializedObject(defaults);
                Require("configuration.pipeline-platform", serialized.FindProperty("m_platform") == null &&
                    defaults.Platform == (PostProcessPlatform)MaxRenderQuality.Platform,
                    "Platform is selected only by the pipeline asset");
                var mobileDefaults = defaults.GetProfile(MaxRenderPlatform.Mobile);
                Require("configuration.mobile-default", mobileDefaults != null && mobileDefaults.Exposure.Mode == ExposureMode.Basic,
                    "Mobile Basic");
                // Populate initialized nested defaults in an initially reference-only asset.
                string configurationFile = Path.Combine(Application.dataPath, "MaxRendererPipeline/DefaultPostProcess.asset");
                string configurationText = File.ReadAllText(configurationFile);
                if (!configurationText.Contains("  m_pc:") || !configurationText.Contains("  m_mobile:"))
                {
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssetIfDirty(config);
                }

                Require("shader.bloom-import", !ShaderUtil.ShaderHasError(config.BloomShader), "No Bloom import errors");
                Shader outputShader = config.OutputShader;
                Require("shader.output-import", outputShader != null && !ShaderUtil.ShaderHasError(outputShader), "No output import errors");
                exposure = new MaxAutoExposurePass(config.ExposureCompute);
                bloom = new MaxBloomPass(config.BloomShader);
                Camera camera = NewCamera("Post processing validation");
                RenderTexture scene = NewTarget(64, 32, "Validation scene HDR");
                camera.targetTexture = scene;
                TestExposure(exposure, camera, scene);
                TestBloom(bloom, camera, scene);
                TestOutput(outputShader);
                s_report.success = true;
                Debug.Log("MaxSRP post processing GPU validation passed: " + s_report.checks.Count + " checks.");
            }
            finally
            {
                exposure?.Dispose();
                bloom?.Dispose();
                for (int i = s_owned.Count - 1; i >= 0; --i)
                {
                    if (s_owned[i] is RenderTexture target) target.Release();
                    if (s_owned[i] != null) UnityEngine.Object.DestroyImmediate(s_owned[i]);
                }
                s_owned.Clear();
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/PostProcessing/validation.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(s_report, true));
                Debug.Log("Post processing report: " + path);
            }
        }

        private static void TestExposure(MaxAutoExposurePass pass, Camera camera, RenderTexture scene)
        {
            Rect full = new Rect(0, 0, scene.width, scene.height);
            foreach (ExposureMode mode in new[] { ExposureMode.Histogram, ExposureMode.Basic })
            {
                string prefix = "exposure." + mode;
                var settings = new ExposureSettings { Mode = mode };
                Fill(scene, Gray(0.18f / HDRScale));
                float middle = Meter(pass, camera, scene, full, settings, true).r;
                Near(prefix + ".middle-gray", middle, HDRScale, HDRScale * 0.15f);

                Fill(scene, Gray(18f / HDRScale));
                float bright = Meter(pass, camera, scene, full, settings, true).r;
                Near(prefix + ".bright", bright, 1000f, 150f);
                settings.CompensationEV = 1f;
                float compensated = Meter(pass, camera, scene, full, settings, true).r;
                Near(prefix + ".compensation", compensated / bright, 2f, 0.01f);
                settings.CompensationEV = 0f;

                Fill(scene, Gray(0f));
                Color black = Meter(pass, camera, scene, full, settings, true);
                Require(prefix + ".black-recovery", IsFinite(black) && black.r > HDRScale, "Finite exposure that brightens black");

                settings.SpeedUp = settings.SpeedDown = 0f;
                Fill(scene, Gray(0.18f / HDRScale));
                float frozen = Meter(pass, camera, scene, full, settings, true).r;
                Fill(scene, Gray(18f / HDRScale));
                float after = Meter(pass, camera, scene, full, settings, false).r;
                Near(prefix + ".zero-speed", after, frozen, Mathf.Max(1e-3f, frozen * 0.0001f));
                float reset = Meter(pass, camera, scene, full, settings, true).r;
                Near(prefix + ".cut-resets-target", reset, 1000f, 150f);

                settings.SpeedUp = 3f; settings.SpeedDown = 1f;
                Fill(scene, Gray(0.18f / HDRScale));
                float old = Meter(pass, camera, scene, full, settings, true).r;
                Fill(scene, Gray(18f / HDRScale));
                System.Threading.Thread.Sleep(20);
                float moving = Meter(pass, camera, scene, full, settings, false).r;
                Require(prefix + ".adaptation-bounded", moving > 1000f && moving < old,
                    "Current exposure moves toward target without jumping; actual=" + Number(moving));

                Write(scene, (x, y) => Gray((x < scene.width / 2 ? 0.18f : 18f) / HDRScale));
                float unmasked = Meter(pass, camera, scene, full, settings, true).r;
                Texture2D mask = NewTexture(scene.width, scene.height,
                    (x, y) => x < scene.width / 2 ? Color.white : Color.black);
                settings.MeteringMask = mask;
                float masked = Meter(pass, camera, scene, full, settings, true).r;
                Require(prefix + ".mask-weights", masked > unmasked * 3f,
                    "Left-side dark mask raises exposure; masked=" + Number(masked) + ", unmasked=" + Number(unmasked));
                settings.MeteringMask = null;

                Write(scene, (x, y) => Gray((x < scene.width / 2 ? 0.18f : 180000f) / HDRScale));
                float viewport = Meter(pass, camera, scene, new Rect(0, 0, scene.width / 2, scene.height), settings, true).r;
                Near(prefix + ".viewport-isolation", viewport, HDRScale, HDRScale * 0.15f);
            }
            var percentileSettings = new ExposureSettings { Mode = ExposureMode.Histogram, LowPercent = 10f, HighPercent = 85f };
            Write(scene, (x, y) => Gray((x < 58 ? 0.18f : 1800f) / HDRScale));
            float clipped = Meter(pass, camera, scene, full, percentileSettings, true).r;
            Near("exposure.histogram-outlier-rejection", clipped, HDRScale, HDRScale * 0.25f);

            Camera other = NewCamera("Independent exposure camera");
            other.targetTexture = scene;
            var independent = new ExposureSettings { Mode = ExposureMode.Histogram, SpeedUp = 0, SpeedDown = 0 };
            Fill(scene, Gray(0.18f / HDRScale));
            float cameraA = Meter(pass, camera, scene, full, independent, true).r;
            Fill(scene, Gray(18f / HDRScale));
            float cameraB = Meter(pass, other, scene, full, independent, true).r;
            float cameraAAfter = Meter(pass, camera, scene, full, independent, false).r;
            Near("exposure.camera-isolation", cameraAAfter, cameraA, cameraA * 0.0001f);
            Require("exposure.camera-target-difference", cameraA > cameraB * 50f, "Independent targets for distinct cameras");
        }

        private static void TestBloom(MaxBloomPass pass, Camera camera, RenderTexture scene)
        {
            Rect full = new Rect(0, 0, scene.width, scene.height);
            var impulsePreviews = new Color[2][];
            foreach (PostProcessPlatform platform in new[] { PostProcessPlatform.PC, PostProcessPlatform.Mobile })
            {
                string prefix = "bloom." + platform;
                var settings = new BloomSettings { Enabled = true, Threshold = -1f, Intensity = 0.675f, SizeScale = 4f };
                Fill(scene, Gray(4f));
                Color[] basePixels = Bloom(pass, camera, scene, full, settings, platform);
                float mean = Mean(basePixels);
                float energy = platform == PostProcessPlatform.PC ? 0.132516667f : 0.11277f;
                Near(prefix + ".constant-energy", mean, 4f * energy * settings.Intensity, 4f * energy * settings.Intensity * 0.02f);
                AllFinite(prefix + ".constant-finite", basePixels);
                settings.Intensity *= 2f;
                float doubled = Mean(Bloom(pass, camera, scene, full, settings, platform));
                Near(prefix + ".intensity-linear", doubled / mean, 2f, 0.015f);
                settings.Intensity = 0.675f;
                settings.Threshold = 10f;
                float rejected = Mean(Bloom(pass, camera, scene, full, settings, platform));
                Near(prefix + ".threshold-rejects", rejected, 0f, 1e-6f);
                float exposed = Mean(Bloom(pass, camera, scene, full, settings, platform, 4f));
                Near(prefix + ".threshold-exposure-space", exposed, mean, mean * 0.02f);
                settings.Threshold = -1f;

                Write(scene, (x, y) => Gray(Mathf.Abs(x - scene.width / 2) <= 1 && Mathf.Abs(y - scene.height / 2) <= 1 ? 100f : 0f));
                Color[] impulse = Bloom(pass, camera, scene, full, settings, platform);
                impulsePreviews[(int)platform] = impulse;
                AllFinite(prefix + ".impulse-finite", impulse);
                float sum = 0, peak = 0;
                int positive = 0;
                foreach (Color value in impulse)
                {
                    sum += value.r; peak = Mathf.Max(peak, value.r);
                    if (value.r > 1e-7f) ++positive;
                }
                Require(prefix + ".bright-point-spreads", positive > 4 && peak > 0 && sum > peak * 1.01f,
                    "Light spreads beyond one output pixel; positive=" + positive + ", peak=" + Number(peak));

                foreach (float value in new[] { float.MaxValue, float.PositiveInfinity, float.NaN })
                {
                    Fill(scene, new Color(value, value, value, 1));
                    Color[] extreme = Bloom(pass, camera, scene, full, settings, platform);
                    AllFinite(prefix + ".extreme-" + Number(value), extreme);
                    if (!float.IsNaN(value)) Require(prefix + ".extreme-preserves-highlight-" + Number(value),
                        Mean(extreme) > 1f, "Saturated highlight remains bright");
                }

                Write(scene, (x, y) => Gray(x < scene.width / 2 ? 0f : 1000f));
                Color[] isolated = Bloom(pass, camera, scene, new Rect(0, 0, scene.width / 2, scene.height), settings, platform);
                Near(prefix + ".viewport-isolation", Mean(isolated), 0f, 1e-6f);
                settings.Enabled = false;
                Near(prefix + ".disabled-is-black", Mean(Bloom(pass, camera, scene, full, settings, platform)), 0f, 1e-6f);
            }
            // Left: PC; right: Mobile. Both display the same viewport extent.
            const int panelWidth = 256, panelHeight = 128;
            var contact = new Color[panelWidth * 2 * panelHeight];
            for (int panel = 0; panel < 2; ++panel)
            {
                int divisor = panel == 0 ? 2 : 4;
                int width = (scene.width + divisor - 1) / divisor;
                int height = (scene.height + divisor - 1) / divisor;
                for (int y = 0; y < panelHeight; ++y)
                    for (int x = 0; x < panelWidth; ++x)
                        contact[y * panelWidth * 2 + panel * panelWidth + x] =
                            impulsePreviews[panel][(y * height / panelHeight) * width + x * width / panelWidth];
            }
            SavePreview("bloom-impulse-pc-mobile.png", contact, panelWidth * 2, panelHeight);
        }

        private static void TestOutput(Shader shader)
        {
            Material material = Own(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
            RenderTexture target = NewTarget(16, 8, "Validation color output", 24);
            Texture2D source = NewTexture(16, 8, (x, y) => new Color(0.1f, 0.2f, 0.3f, 0.37f));
            Texture2D bloom = NewTexture(1, 1, (x, y) => new Color(0.05f, 0.025f, 0.01f, 0));
            Texture2D exposure = NewTexture(1, 1, (x, y) => Gray(2f));
            Color plain = DrawOutput(material, target, source, 3f, ToneMappingMode.None)[0];
            NearColor("output.manual-once", plain, new Color(0.3f, 0.6f, 0.9f, 0.37f), 1e-5f);
            Color composited = DrawOutput(material, target, source, 7f, ToneMappingMode.None,
                new Vector4(1, 1, 0, 0), exposure, bloom)[0];
            NearColor("output.automatic-and-bloom-once", composited, new Color(0.3f, 0.45f, 0.62f, 0.37f), 1e-5f);
            Color neutral = DrawOutput(material, target, source, 3f, ToneMappingMode.None,
                new Vector4(0, 0, 1, 0))[0];
            NearColor("output.neutral-grading", neutral, plain, 1e-4f);

            Texture2D ramp = NewTexture(64, 8, (x, y) => new Color(x == 0 ? 0f :
                x == 63 ? float.MaxValue : Mathf.Pow(10f, -5f + 10f * x / 63f),
                x == 0 ? 0f : x == 63 ? float.MaxValue : Mathf.Pow(10f, -5f + 10f * x / 63f),
                x == 0 ? 0f : x == 63 ? float.MaxValue : Mathf.Pow(10f, -5f + 10f * x / 63f), 0.73f));
            RenderTexture filmTarget = NewTarget(64, 8, "Validation film ramp", 24);
            Color[] film = DrawOutput(material, filmTarget, ramp, 1f, ToneMappingMode.UEFilm);
            AllFinite("output.film-finite", film);
            Require("output.film-black", film[0].r >= 0 && film[0].r < 1e-4f, "Film black is near zero; actual=" + Number(film[0].r));
            bool monotonic = true;
            float worstDecrease = 0;
            for (int x = 1; x < 64; ++x)
            {
                worstDecrease = Mathf.Max(worstDecrease, film[x - 1].r - film[x].r);
                monotonic &= film[x].r >= film[x - 1].r - 1e-5f;
            }
            Require("output.film-monotonic", monotonic, "Gray ramp never decreases; largest decrease=" + Number(worstDecrease));
            Near("output.film-alpha", film[32].a, 0.73f, 1e-6f);
            // UE's Filmic SDR / LinearWithToneCurve path keeps LUT headroom
            // (GetTonemappingMax=1.05). A neutral asymptote is 1+WhiteClip;
            // this is not a per-channel bound for saturated RGB colors.
            Near("output.film-neutral-highlight", film[63].r, 1f + 0.04f, 1e-3f);

            Texture2D split = NewTexture(16, 8, (x, y) => x < 8 ? new Color(0.2f, 0.05f, 0.1f, 0.37f) :
                new Color(0.05f, 0.4f, 0.1f, 0.73f));
            Texture2D localBloom = NewTexture(8, 8, (x, y) => new Color(0.1f, 0.1f, 0.1f, 0));
            Fill(target, Color.magenta);
            Color[] viewport = DrawOutput(material, target, split, 2f, ToneMappingMode.None,
                new Vector4(0, 1, 0, 0), null, localBloom, new Rect(8, 0, 8, 8),
                new Vector4(0.5f, 1, 0.5f, 0), new Vector4(2, 1, -1, 0));
            NearColor("output.viewport-source-and-bloom", viewport[12], new Color(0.3f, 1f, 0.4f, 0.73f), 1e-5f);
            NearColor("output.viewport-preserves-neighbor", viewport[3], Color.magenta, 1e-5f);

            Texture2D gray = NewTexture(16, 8, (x, y) => new Color(0.5f, 0.5f, 0.5f, 0.37f));
            Color[] dither = DrawOutput(material, target, gray, 1f, ToneMappingMode.None, Vector4.zero,
                null, null, null, null, null, 2f);
            AllFinite("output.dither-finite", dither);
            Near("output.dither-unbiased", Mean(dither), 0.5f, 0.01f);
            Near("output.dither-alpha", dither[0].a, 0.37f, 1e-6f);

            // Actual production Film output, not a CPU recreation: top-to-bottom
            // display bands include neutral gray and saturated lighting colors.
            Color[] chartHues = { new Color(1, 0.2f, 0.04f), Color.magenta, Color.cyan, Color.yellow,
                Color.blue, Color.green, Color.red, Color.white };
            Texture2D chartSource = NewTexture(256, 128, (x, y) =>
            {
                Color hue = chartHues[y / 16];
                float value = x == 0 ? 0f : Mathf.Pow(2f, -12f + x * 20f / 255f);
                return new Color(hue.r * value, hue.g * value, hue.b * value, 1);
            });
            RenderTexture chartTarget = NewTarget(256, 128, "Validation film color chart", 24);
            Color[] chart = DrawOutput(material, chartTarget, chartSource, 1f, ToneMappingMode.UEFilm);
            AllFinite("output.film-color-chart-finite", chart);
            SavePreview("filmic-ramp.png", chart, chartTarget.width, chartTarget.height);
        }

        private static Color Meter(MaxAutoExposurePass pass, Camera camera, RenderTexture scene, Rect viewport,
            ExposureSettings settings, bool reset)
        {
            using (var cmd = new CommandBuffer { name = "Validate production exposure" })
            {
                Texture result = pass.Record(cmd, camera, scene, viewport, settings, reset);
                if (result == null) throw new InvalidOperationException("Exposure returned the unsupported-platform fallback during GPU validation.");
                Graphics.ExecuteCommandBuffer(cmd);
                return Read(result)[0];
            }
        }

        private static Color[] Bloom(MaxBloomPass pass, Camera camera, RenderTexture scene, Rect viewport,
            BloomSettings settings, PostProcessPlatform platform, float exposure = 1f)
        {
            using (var cmd = new CommandBuffer { name = "Validate production bloom" })
            {
                Texture result = pass.Record(cmd, camera, scene, viewport, null, exposure, settings, platform);
                Graphics.ExecuteCommandBuffer(cmd);
                return Read(result);
            }
        }

        private static Color[] DrawOutput(Material material, RenderTexture target, Texture source, float exposure,
            ToneMappingMode mode, Vector4 flags = default, Texture exposureTexture = null, Texture bloom = null,
            Rect? viewport = null, Vector4? sourceRect = null, Vector4? bloomRect = null, float dithering = 0f)
        {
            using (var cmd = new CommandBuffer { name = "Validate production color output" })
            {
                cmd.SetGlobalTexture("_SceneColorHDR", source);
                cmd.SetGlobalTexture("_SceneDepthHDR", Texture2D.blackTexture);
                cmd.SetGlobalVector("_ColorOutputParameters", new Vector4(exposure, (float)mode, 0, dithering));
                cmd.SetGlobalVector("_MaxPostProcessFlags", flags);
                cmd.SetGlobalTexture("_MaxExposureTexture", exposureTexture != null ? exposureTexture : Texture2D.whiteTexture);
                cmd.SetGlobalTexture("_MaxBloomTexture", bloom != null ? bloom : Texture2D.blackTexture);
                cmd.SetGlobalVector("_MaxSourceUVScaleBias", sourceRect ?? new Vector4(1, 1, 0, 0));
                cmd.SetGlobalVector("_MaxBloomUVScaleBias", bloomRect ?? new Vector4(1, 1, 0, 0));
                cmd.SetGlobalVector("_MaxColorSaturation", Vector4.one);
                cmd.SetGlobalVector("_MaxColorContrast", Vector4.one);
                cmd.SetGlobalVector("_MaxColorGamma", Vector4.one);
                cmd.SetGlobalVector("_MaxColorGain", Vector4.one);
                cmd.SetGlobalVector("_MaxColorOffset", Vector4.zero);
                cmd.SetGlobalVector("_MaxWhiteBalance", new Vector4(6500, 0, 0, 0));
                cmd.SetGlobalVector("_MaxFilmParameters", new Vector4(0.88f, 0.55f, 0.26f, 0));
                cmd.SetGlobalFloat("_MaxFilmWhiteClip", 0.04f);
                cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(viewport ?? new Rect(0, 0, target.width, target.height));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(cmd);
                return Read(target);
            }
        }

        private static Camera NewCamera(string name)
        {
            var go = Own(new GameObject(name) { hideFlags = HideFlags.HideAndDontSave });
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            return camera;
        }

        private static RenderTexture NewTarget(int width, int height, string name, int depth = 0)
        {
            var target = Own(new RenderTexture(width, height, depth, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp, useMipMap = false, antiAliasing = 1
            });
            if (!target.Create()) throw new InvalidOperationException("Could not allocate validation target " + name);
            return target;
        }

        private static Texture2D NewTexture(int width, int height, Func<int, int, Color> sample)
        {
            var texture = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            });
            var values = new Color[width * height];
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x) values[y * width + x] = sample(x, y);
            texture.SetPixels(values);
            texture.Apply(false, false);
            return texture;
        }

        private static void Write(RenderTexture target, Func<int, int, Color> sample)
        {
            Texture2D input = NewTexture(target.width, target.height, sample);
            Graphics.CopyTexture(input, target);
        }

        private static void Fill(RenderTexture target, Color color)
        {
            using (var cmd = new CommandBuffer { name = "Clear validation HDR" })
            {
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(false, true, color);
                Graphics.ExecuteCommandBuffer(cmd);
            }
        }

        private static Color[] Read(Texture texture)
        {
            if (texture is Texture2D input) return input.GetPixels();
            var target = texture as RenderTexture;
            if (target == null) throw new InvalidOperationException("Unsupported validation readback texture.");
            var readback = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(readback); }
        }

        private static T Own<T>(T value) where T : UnityEngine.Object { s_owned.Add(value); return value; }
        private static void SavePreview(string filename, Color[] linear, int width, int height)
        {
            // PNG is an SDR preview. Encode sRGB explicitly and clip only this
            // preview; numerical checks retain the original floating-point values.
            var encoded = new Color[linear.Length];
            for (int i = 0; i < linear.Length; ++i)
                encoded[i] = new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(linear[i].r)),
                    Mathf.LinearToGammaSpace(Mathf.Clamp01(linear[i].g)),
                    Mathf.LinearToGammaSpace(Mathf.Clamp01(linear[i].b)), 1);
            var image = new Texture2D(width, height, TextureFormat.RGB24, false, true);
            try
            {
                image.SetPixels(encoded); image.Apply(false, false);
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../OfflineBaseline/PostProcessing/", filename));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }
        private static Color Gray(float value) => new Color(value, value, value, 1f);
        private static string Number(float value) => value.ToString("G9", CultureInfo.InvariantCulture);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFinite(Color color) => IsFinite(color.r) && IsFinite(color.g) && IsFinite(color.b) && IsFinite(color.a);
        private static float Mean(Color[] values)
        {
            double sum = 0;
            foreach (Color value in values) sum += value.r;
            return (float)(sum / values.Length);
        }

        private static void Near(string name, float actual, float expected, float tolerance)
        {
            bool passed = IsFinite(actual) && Mathf.Abs(actual - expected) <= tolerance;
            s_report.checks.Add(new Check { name = name, actual = Number(actual), expected = Number(expected) + " +/- " + Number(tolerance), passed = passed });
            if (!passed) throw new InvalidOperationException(name + ": expected " + Number(expected) + " +/- " + Number(tolerance) + ", actual " + Number(actual));
        }

        private static void Require(string name, bool passed, string expected)
        {
            s_report.checks.Add(new Check { name = name, actual = passed ? "true" : "false", expected = expected, passed = passed });
            if (!passed) throw new InvalidOperationException(name + ": " + expected);
        }

        private static void NearColor(string name, Color actual, Color expected, float tolerance)
        {
            float error = Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g),
                Mathf.Abs(actual.b - expected.b), Mathf.Abs(actual.a - expected.a));
            bool passed = IsFinite(actual) && error <= tolerance;
            s_report.checks.Add(new Check { name = name, actual = actual.ToString("G9"), expected = expected.ToString("G9") + " +/- " + Number(tolerance), passed = passed });
            if (!passed) throw new InvalidOperationException(name + ": expected " + expected.ToString("G9") + ", actual " + actual.ToString("G9") + ", error=" + Number(error));
        }

        private static void AllFinite(string name, Color[] values)
        {
            for (int i = 0; i < values.Length; ++i)
                if (!IsFinite(values[i])) throw new InvalidOperationException(name + ": non-finite pixel " + i + " = " + values[i]);
            Require(name, true, "All " + values.Length + " pixels finite");
        }
    }
}
#endif
