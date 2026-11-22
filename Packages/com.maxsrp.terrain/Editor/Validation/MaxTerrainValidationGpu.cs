using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    [Serializable]
    public sealed class MaxTerrainGpuCapture
    {
        public string name, error;
        public bool complete, passed, taaActive, taaHistoryValid;
        public int width, height, outputWidth, outputHeight;
        public uint pixels, geometry, albedo, normals, roughness, motionValid, motionRejected, motionMoving, nonFinite;
        [NonSerialized] internal int pending;

        internal void Fail(string message) { error = string.IsNullOrEmpty(error) ? message : error + "\n" + message; }
        internal void Finished()
        {
            pending--;
            if (pending != 0) return;
            complete = true;
            passed = string.IsNullOrEmpty(error) && geometry > 100 &&
                albedo >= geometry * 0.95 && normals >= geometry * 0.95 && roughness >= geometry * 0.95 && nonFinite == 0;
        }
    }

    internal static class MaxTerrainValidationGpu
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string ProbePath = "Packages/com.maxsrp.terrain/Editor/Validation/MaxTerrainValidationProbe.compute";
        const string ReadbackPath = "Packages/com.maxsrp.terrain/Editor/Validation/MaxTerrainIkiValidationReadback.compute";
        static object Field(object owner, string name) => owner?.GetType().GetField(name, Fields)?.GetValue(owner);

        // Editor evidence only. Unity's implicit readback conversion from the
        // packed 10:10:10:2 normal target to RGBAFloat emits a D3D11 error even
        // when the callback succeeds. Typed shader Load preserves the actual
        // normalized channel values, then readback uses native RGBAFloat.
        internal static RenderTexture CopyColorForReadback(CommandBuffer commands, RenderTexture source, string name)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ReadbackPath);
            if (!shader) throw new InvalidOperationException("Missing Editor-only color readback shader.");
            var result = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = name, enableRandomWrite = true };
            if (!result.Create()) { Object.Destroy(result); throw new InvalidOperationException("Cannot allocate RGBAFloat evidence copy."); }
            int kernel = shader.FindKernel("CopyColor");
            commands.SetComputeIntParams(shader, "_IkiValidationSize", source.width, source.height);
            commands.SetComputeTextureParam(shader, kernel, "_IkiValidationColor", source);
            commands.SetComputeTextureParam(shader, kernel, "_IkiValidationColorCopy", result);
            commands.DispatchCompute(shader, kernel, (source.width + 7) / 8, (source.height + 7) / 8, 1);
            return result;
        }

        public static MaxTerrainGpuCapture Record(ScriptableRenderContext context, Camera camera, string name, string output)
        {
            var result = new MaxTerrainGpuCapture { name = name };
            var commands = new CommandBuffer { name = "MaxTerrain migration GPU evidence" };
            ComputeBuffer counts = null;
            RenderTexture screenshot = null;
            var readableCopies = new List<RenderTexture>();
            try
            {
                object pipeline = RenderPipelineManager.currentPipeline;
                var cameras = Field(Field(pipeline, "m_targets"), "m_cameraResources") as IDictionary;
                object resources = cameras?[camera.GetInstanceID()];
                var buffers = Field(resources, "Buffers") as RenderTexture[];
                var depth = Field(resources, "Depth") as RenderTexture;
                if (buffers == null || buffers.Length != 4 || !depth) throw new InvalidOperationException("Cannot locate this camera's MaxSRP GBuffer resources.");
                result.width = depth.width; result.height = depth.height;
                foreach (var buffer in buffers)
                    if (!buffer || buffer.width != result.width || buffer.height != result.height)
                        throw new InvalidOperationException("Mismatched camera GBuffer dimensions.");
                var histories = Field(Field(Field(pipeline, "m_passes"), "TemporalAA"), "m_histories") as IDictionary;
                object history = histories != null && histories.Contains(camera.GetInstanceID()) ? histories[camera.GetInstanceID()] : null;
                var motion = Field(history, "Motion") as RenderTexture;
                result.taaActive = motion;
                result.taaHistoryValid = history != null && (bool)Field(history, "Valid");
                if (motion && (motion.width != result.width || motion.height != result.height))
                    throw new InvalidOperationException("Motion and GBuffer dimensions differ.");

                var probe = AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbePath);
                if (!probe) throw new InvalidOperationException("Missing GPU validation compute shader.");
                int kernel = probe.FindKernel("InspectGBuffer");
                counts = new ComputeBuffer(10, sizeof(uint));
                counts.SetData(new uint[10]);
                commands.SetComputeIntParams(probe, "_ValidationSize", result.width, result.height);
                commands.SetComputeIntParam(probe, "_ValidationReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
                commands.SetComputeIntParam(probe, "_ValidationHasMotion", motion ? 1 : 0);
                commands.SetComputeTextureParam(probe, kernel, "_ValidationAlbedo", buffers[0]);
                commands.SetComputeTextureParam(probe, kernel, "_ValidationNormal", buffers[1]);
                commands.SetComputeTextureParam(probe, kernel, "_ValidationMaterial", buffers[2]);
                commands.SetComputeTextureParam(probe, kernel, "_ValidationDepth", depth);
                commands.SetComputeTextureParam(probe, kernel, "_ValidationMotion", motion ? motion : buffers[1]);
                commands.SetComputeBufferParam(probe, kernel, "_ValidationCounts", counts);
                commands.DispatchCompute(probe, kernel, (result.width + 7) / 8, (result.height + 7) / 8, 1);

                Directory.CreateDirectory(output);
                result.pending = 5;
                var capturedCounts = counts;
                commands.RequestAsyncReadback(counts, request =>
                {
                    try
                    {
                        if (request.hasError) result.Fail("GBuffer statistics readback failed.");
                        else
                        {
                            var values = request.GetData<uint>();
                            result.pixels = values[0]; result.geometry = values[1]; result.albedo = values[2];
                            result.normals = values[3]; result.roughness = values[4]; result.motionValid = values[5];
                            result.motionRejected = values[6]; result.motionMoving = values[7]; result.nonFinite = values[9];
                        }
                    }
                    catch (Exception exception) { result.Fail(exception.ToString()); }
                    finally { capturedCounts.Dispose(); result.Finished(); }
                });
                for (int index = 0; index < 3; ++index)
                {
                    int channel = index;
                    RenderTexture readable = CopyColorForReadback(commands, buffers[index], "MaxTerrain GBuffer" + index + " evidence copy");
                    readableCopies.Add(readable);
                    commands.RequestAsyncReadback(readable, 0, request =>
                    {
                        Texture2D image = null;
                        try
                        {
                            if (request.hasError) result.Fail("GBuffer" + channel + " image readback failed.");
                            else
                            {
                                var source = request.GetData<Color>();
                                var pixels = new Color32[source.Length];
                                for (int p = 0; p < pixels.Length; ++p)
                                {
                                    Color value = source[p];
                                    // Albedo is linear in the host GBuffer. PNG preview
                                    // encodes sRGB; normal/material previews stay raw.
                                    if (channel == 0) value = value.gamma;
                                    value.a = 1; pixels[p] = value;
                                }
                                image = new Texture2D(result.width, result.height, TextureFormat.RGBA32, false, true);
                                image.SetPixels32(pixels); image.Apply(false);
                                File.WriteAllBytes(Path.Combine(output, name + "-gb" + channel + ".png"), image.EncodeToPNG());
                            }
                        }
                        catch (Exception exception) { result.Fail(exception.ToString()); }
                        finally { if (image) Object.Destroy(image); readable.Release(); Object.Destroy(readable); result.Finished(); }
                    });
                }

                // The current camera has completed final color output. Queue a
                // backbuffer copy in the same SRP context, before Submit, so the
                // screenshot cannot race the deferred terrain's render commands.
                result.outputWidth = camera.targetTexture ? camera.targetTexture.width : Mathf.Max(Screen.width, Mathf.CeilToInt(camera.pixelRect.xMax));
                result.outputHeight = camera.targetTexture ? camera.targetTexture.height : Mathf.Max(Screen.height, Mathf.CeilToInt(camera.pixelRect.yMax));
                screenshot = new RenderTexture(result.outputWidth, result.outputHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "MaxTerrain validation screenshot" };
                screenshot.Create();
                commands.Blit(camera.targetTexture ? new RenderTargetIdentifier(camera.targetTexture) :
                    new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), screenshot);
                var capturedImage = screenshot;
                commands.RequestAsyncReadback(screenshot, 0, TextureFormat.RGBA32, request =>
                {
                    Texture2D image = null;
                    try
                    {
                        if (request.hasError) result.Fail("Final-color screenshot readback failed.");
                        else
                        {
                            image = new Texture2D(result.outputWidth, result.outputHeight, TextureFormat.RGBA32, false, false);
                            image.LoadRawTextureData(request.GetData<byte>()); image.Apply(false);
                            File.WriteAllBytes(Path.Combine(output, name + "-lit.png"), image.EncodeToPNG());
                        }
                    }
                    catch (Exception exception) { result.Fail(exception.ToString()); }
                    finally { if (image) Object.Destroy(image); capturedImage.Release(); Object.Destroy(capturedImage); result.Finished(); }
                });
                context.ExecuteCommandBuffer(commands);
            }
            catch (Exception exception)
            {
                result.Fail(exception.ToString()); result.complete = true; result.passed = false;
                counts?.Dispose(); if (screenshot) { screenshot.Release(); Object.Destroy(screenshot); }
                foreach (var copy in readableCopies) if (copy) { copy.Release(); Object.Destroy(copy); }
            }
            finally { commands.Release(); }
            return result;
        }
    }
}
