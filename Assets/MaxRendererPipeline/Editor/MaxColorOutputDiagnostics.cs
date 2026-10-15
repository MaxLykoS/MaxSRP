using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    internal static class MaxColorOutputDiagnostics
    {
        [MenuItem("MaxSRP/Diagnostics/Reset Camera Aspect To Automatic")]
        private static void ResetCameraAspect()
        {
            // Repair aspect overrides left on existing cameras by the old shadow pass.
            // This is explicit so intentional Game-camera aspect overrides are preserved.
            if (Camera.main != null) Camera.main.ResetAspect();
            foreach (SceneView view in SceneView.sceneViews)
                if (view.camera != null) view.camera.ResetAspect();
            SceneView.RepaintAll();
            Debug.Log("MaxSRP: main and Scene View camera aspect ratios now follow their viewports.");
        }

        [MenuItem("MaxSRP/Diagnostics/Capture HDR Buffer")]
        private static void CaptureFromMenu()
        {
            if (!Capture()) Debug.LogWarning("No MaxSRP HDR output is available. Render a Game or Scene view frame first.");
        }

        private static bool Capture()
        {
            var pipeline = RenderPipelineManager.currentPipeline;
            Camera camera = Camera.main;
            if (!(pipeline is MaxRenderPipeline) || camera == null) return false;
            var field = typeof(MaxRenderPipeline).GetField("m_outputResources", BindingFlags.Instance | BindingFlags.NonPublic);
            var outputs = field.GetValue(pipeline) as IDictionary;
            long key = camera.targetTexture != null ? (1L << 32) | (uint)camera.targetTexture.GetInstanceID()
                : (2L << 32) | (uint)camera.targetDisplay;
            object output = outputs.Contains(key) ? outputs[key] : null;
            string cameraLabel = camera.name;
            if (output == null)
            {
                foreach (DictionaryEntry entry in outputs) { output = entry.Value; cameraLabel = "Other active output"; break; }
            }
            if (output == null) return false;
            var source = output.GetType().GetField("Color").GetValue(output) as RenderTexture;
            if (source == null || !source.IsCreated()) return false;
            RenderTexture previous = RenderTexture.active;
            RenderTexture small = RenderTexture.GetTemporary(128, 72, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(128, 72, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(source, small);
                RenderTexture.active = small;
                pixels.ReadPixels(new Rect(0, 0, 128, 72), 0, 0);
                pixels.Apply();
                Color[] data = pixels.GetPixels();
                double sum = 0, max = 0, mappedSum = 0, luminanceSum = 0;
                int nonFinite = 0, aboveOne = 0, nearlyWhite = 0;
                var asset = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
                float exposure = asset != null ? asset.ColorOutput.GetExposureMultiplier() : 1;
                foreach (Color c in data)
                {
                    if (float.IsNaN(c.r + c.g + c.b) || float.IsInfinity(c.r + c.g + c.b)) { ++nonFinite; continue; }
                    double value = Math.Max(c.r, Math.Max(c.g, c.b));
                    max = Math.Max(max, value);
                    if (value > 1) ++aboveOne;
                    sum += (c.r + c.g + c.b) / 3.0;
                    luminanceSum += .2126 * c.r + .7152 * c.g + .0722 * c.b;
                    double r = Map(c.r * exposure), g = Map(c.g * exposure), b = Map(c.b * exposure);
                    mappedSum += (r + g + b) / 3;
                    if (r > .9 && g > .9 && b > .9) ++nearlyWhite;
                }
                Matrix4x4 screen = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
                Matrix4x4 texture = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                string report = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "camera={0}\napi={1}\nsize={2}x{3}\nsourceFormat={4}\nsourceMeanRGB={5:G9}\nsourceMax={6:G9}\nnonFinite={7}\naboveOneFraction={8:G9}\nexposureMultiplier={9:G9}\nACESMeanRGB={10:G9}\nnearlyWhiteFraction={11:G9}\nscreenM11={12:G9}\ntextureM11={13:G9}\nuvStartsAtTop={14}\n",
                    cameraLabel, SystemInfo.graphicsDeviceType, source.width, source.height, source.format,
                    sum / data.Length, max, nonFinite, (double)aboveOne / data.Length, exposure,
                    mappedSum / data.Length, (double)nearlyWhite / data.Length, screen.m11, texture.m11, SystemInfo.graphicsUVStartsAtTop);
                report += string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "HDRScale={0:G9}\nsourceMeanLuminanceCdM2={1:G9}\nCameraEV100={2:G9}\nExposureCompensationEV={3:G9}\n",
                    LightPhotometry.HDRScale, luminanceSum / data.Length * LightPhotometry.HDRScale,
                    asset != null ? asset.ColorOutput.CameraEV100 : 0,
                    asset != null ? asset.ColorOutput.ExposureEV : 0);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "MaxSRP-hdr-probe.txt"), report);
                Debug.Log("MaxSRP HDR diagnostics saved to " + Path.Combine(Path.GetTempPath(), "MaxSRP-hdr-probe.txt"));
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(small);
                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }

        private static double Map(double x)
        {
            x = Math.Max(0, x);
            return Math.Min(1, (x * (2.51 * x + .03)) / (x * (2.43 * x + .59) + .14));
        }
    }
}
