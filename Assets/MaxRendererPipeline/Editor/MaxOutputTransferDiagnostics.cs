using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    // Synthetic colors through the actual output shader and hardware sRGB conversion.
    internal static class MaxOutputTransferDiagnostics
    {
        private const int Width = 1024, Height = 2048;
        [Serializable] private sealed class Report
        {
            public string capturedUtc, api, shaderSha256;
            public int width = Width, height = Height;
            public Vector3 startCodes = new Vector3(55.125f, 75.375f, 125.625f);
            public float rangeCodes = 16;
            public string scope = "Synthetic display-encoded color ramp decoded on CPU, exposure=1, tone mapping=off. fp32/fp16/direct use frozen pre-fix output; quantized uses current production ColorOutput through FP16 then hardware sRGB8. No camera/lighting/settings changes. Raw files: bottom-left RGBA float32; PNGs: native sRGB8 readback.";
        }
        [Serializable] private sealed class Dimensions { public int width = 0, height = 0; }
        [Serializable] private sealed class CapturedCamera { public string file = null; public Dimensions source = null; public Vector4 parameters = default; }
        [Serializable] private sealed class CapturedFrame { public string capturedUtc = null; public CapturedCamera[] cameras = null; }
        [Serializable] private sealed class ReplayReport
        {
            public string capturedUtc, sourceCaptureUtc, sourceFile, sourceSha256, shaderSha256;
            public int width, height;
            public Vector4 parameters;
            public string scope = "Same previously captured real camera HDR and exposure/ACES parameters; frozen pre-fix vs current production output, both FP16 -> hardware sRGB8. No camera rerender, scene changes or monitor capture.";
        }

        [MenuItem("MaxSRP/Diagnostics/Compare Captured Sky Output")]
        private static void ReplaySky()
        {
            string output = Path.GetFullPath("OfflineBaseline/OutputTransfer/SkyReplay");
            string sourceDirectory = Path.GetFullPath("OfflineBaseline/FinalOutput");
            Directory.CreateDirectory(output);
            var capture = JsonUtility.FromJson<CapturedFrame>(File.ReadAllText(Path.Combine(sourceDirectory, "capture.json")));
            CapturedCamera camera = null;
            foreach (var candidate in capture.cameras) if (candidate.file == "sceneview") { camera = candidate; break; }
            if (camera == null) throw new InvalidOperationException("No SceneView HDR capture to replay.");
            var targets = new List<RenderTexture>();
            var previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            Texture2D source = null;
            Material material = null, legacy = null;
            try
            {
                int width = camera.source.width, height = camera.source.height;
                string sourceFile = Path.Combine(sourceDirectory, camera.file + "_hdr.f32");
                byte[] bytes = File.ReadAllBytes(sourceFile);
                if (bytes.Length != width * height * 16) throw new InvalidOperationException("HDR capture size mismatch.");
                source = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                source.LoadRawTextureData(bytes); source.Apply(false, false);
                material = new Material(Shader.Find("Hidden/MaxSRP/ColorOutput"));
                legacy = new Material(Shader.Find("Hidden/MaxSRP/OutputTransferProbe"));
                material.SetTexture("_SceneDepthHDR", Texture2D.whiteTexture);
                legacy.SetTexture("_SceneDepthHDR", Texture2D.whiteTexture);
                for (int variant = 0; variant < 2; variant++)
                {
                    string name = variant == 0 ? "legacy" : "quantized";
                    var linear = Target(RenderTextureFormat.ARGBHalf, false, targets, width, height);
                    Draw(variant == 0 ? legacy : material, source, linear, 2, camera.parameters.x, camera.parameters.y);
                    Save(linear, output, name, false);
                    var encoded = Target(RenderTextureFormat.ARGB32, true, targets, width, height);
                    Draw(material, linear, encoded, 0);
                    Save(encoded, output, name + "_srgb8", true);
                }
                var report = new ReplayReport { capturedUtc = DateTime.UtcNow.ToString("O"), sourceCaptureUtc = capture.capturedUtc,
                    sourceFile = sourceFile, width = width, height = height, parameters = camera.parameters };
                using (var hash = System.Security.Cryptography.SHA256.Create())
                {
                    report.sourceSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    report.shaderSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes("Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl"))).Replace("-", "").ToLowerInvariant();
                }
                File.WriteAllText(Path.Combine(output, "capture.json"), JsonUtility.ToJson(report, true));
                Debug.Log("MaxSRP captured sky output replay exported to " + output);
            }
            finally
            {
                GL.sRGBWrite = previousSrgb; RenderTexture.active = previous;
                foreach (var rt in targets) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (legacy != null) UnityEngine.Object.DestroyImmediate(legacy);
            }
        }

        [MenuItem("MaxSRP/Diagnostics/Compare Output Transfer")]
        private static void Capture()
        {
            string output = Path.GetFullPath("OfflineBaseline/OutputTransfer");
            Directory.CreateDirectory(output);
            var targets = new List<RenderTexture>();
            var previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            Texture2D source = null;
            Material material = null;
            Material legacy = null;
            try
            {
                var report = new Report { capturedUtc = DateTime.UtcNow.ToString("O"), api = SystemInfo.graphicsDeviceType.ToString() };
                source = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var colors = new Color[Width * Height];
                for (int x = 0; x < Width; x++)
                {
                    Vector3 c = (report.startCodes + Vector3.one * (report.rangeCodes * (x + .5f) / Width)) / 255;
                    var color = new Color(Decode(c.x), Decode(c.y), Decode(c.z), 1);
                    for (int y = 0; y < Height; y++) colors[y * Width + x] = color;
                }
                source.SetPixels(colors); source.Apply(false, false);
                material = new Material(Shader.Find("Hidden/MaxSRP/ColorOutput"));
                material.SetTexture("_SceneColorHDR", source);
                material.SetTexture("_SceneDepthHDR", Texture2D.whiteTexture);
                legacy = new Material(Shader.Find("Hidden/MaxSRP/OutputTransferProbe"));
                legacy.SetTexture("_SceneDepthHDR", Texture2D.whiteTexture);
                var stable = Target(RenderTextureFormat.ARGBHalf, false, targets);
                Draw(material, source, stable, 2);
                var stableEncoded = Target(RenderTextureFormat.ARGB32, true, targets);
                Draw(material, stable, stableEncoded, 0);
                Save(stableEncoded, output, "quantized_srgb8", true);
                var direct = Target(RenderTextureFormat.ARGB32, true, targets);
                Draw(legacy, source, direct, 2);
                Save(direct, output, "direct_srgb8", true);
                foreach (bool half in new[] { false, true })
                {
                    string name = half ? "fp16" : "fp32";
                    var linear = Target(half ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat, false, targets);
                    Draw(legacy, source, linear, 2);
                    Save(linear, output, name, false);
                    var encoded = Target(RenderTextureFormat.ARGB32, true, targets);
                    // Reuse the float4 output shader as a pass-through. Unity's built-in
                    // Blit copy shader can return half and would confound this FP32 test.
                    Draw(material, linear, encoded, 0);
                    Save(encoded, output, name + "_srgb8", true);
                }
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    report.shaderSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes("Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl"))).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(output, "capture.json"), JsonUtility.ToJson(report, true));
                Debug.Log("MaxSRP output transfer diagnostic exported to " + output);
            }
            finally
            {
                GL.sRGBWrite = previousSrgb; RenderTexture.active = previous;
                foreach (var rt in targets) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (legacy != null) UnityEngine.Object.DestroyImmediate(legacy);
            }
        }
        private static void Draw(Material material, Texture source, RenderTexture target, float dither, float exposure = 1, float tone = 0)
        {
            material.SetTexture("_SceneColorHDR", source);
            material.SetVector("_ColorOutputParameters", new Vector4(exposure, tone, 0, dither));
            var cmd = new CommandBuffer { name = "Output transfer diagnostic" };
            try
            {
                cmd.SetRenderTarget(target); cmd.SetViewport(new Rect(0, 0, target.width, target.height));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                GL.sRGBWrite = target.sRGB; Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { cmd.Release(); }
        }
        private static float Decode(float c) => c <= .04045f ? c / 12.92f : Mathf.Pow((c + .055f) / 1.055f, 2.4f);
        private static RenderTexture Target(RenderTextureFormat format, bool srgb, List<RenderTexture> targets, int width = Width, int height = Height)
        {
            var rt = new RenderTexture(width, height, 0, format, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            targets.Add(rt);
            if (!rt.Create()) throw new InvalidOperationException("Output transfer target allocation failed.");
            return rt;
        }
        private static void Save(RenderTexture rt, string directory, string name, bool png)
        {
            var pixels = new Texture2D(rt.width, rt.height, png ? TextureFormat.RGBA32 : TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = rt;
                pixels.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false); pixels.Apply(false, false);
                File.WriteAllBytes(Path.Combine(directory, name + (png ? ".png" : ".f32")), png ? pixels.EncodeToPNG() : pixels.GetRawTextureData());
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
        }
    }
}
