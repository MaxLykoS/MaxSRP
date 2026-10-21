using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderPipeline : RenderPipeline
    {
        private readonly MaxLightConfigurator m_lightConfigurator = new MaxLightConfigurator();
        private readonly MaxRenderOpaquePass m_opaquePass = new MaxRenderOpaquePass();
        private readonly Dictionary<int, CameraResources> m_cameraResources = new Dictionary<int, CameraResources>();
        private readonly List<int> m_unusedCameras = new List<int>();
        private readonly Plane[] m_cameraCullingPlanes = new Plane[6];
        private readonly Dictionary<long, OutputResources> m_outputResources = new Dictionary<long, OutputResources>();
        private readonly List<long> m_unusedOutputs = new List<long>();
        private readonly Dictionary<long, Vector2Int> m_outputSizes = new Dictionary<long, Vector2Int>();
        private readonly Dictionary<long, int> m_outputCameraCounts = new Dictionary<long, int>();
        private MaxTemporalAAPass m_temporalAA;
        private MaxLightPass m_LightPass;
        private MaxColorOutputPass m_colorOutputPass;
        private MaxIBLGIPass m_iblGIPass;
        private MaxShadowCasterPass m_shadowCastPass;
        private MaxProceduralSkyboxPass m_proceduralSkyboxPass;
        private readonly MaxRendererPipelineAsset m_setting;

        // Call on explicit camera cuts/teleports or discontinuous scene changes.
        public void ResetTemporalHistory(Camera camera) { m_temporalAA?.ResetHistory(camera); }

        public MaxRenderPipeline(MaxRendererPipelineAsset setting)
        {
            GraphicsSettings.useScriptableRenderPipelineBatching = true;
            m_setting = setting;
            try
            {
                m_LightPass = new MaxLightPass(setting.LightingShader);
                m_colorOutputPass = new MaxColorOutputPass(setting.ColorOutput);
                m_temporalAA = new MaxTemporalAAPass(setting.TemporalShader);
                m_shadowCastPass = new MaxShadowCasterPass(setting.CascadeSetting, setting.AtmosphereScatteringSetting);
                m_proceduralSkyboxPass = new MaxProceduralSkyboxPass(setting.AtmosphereScatteringSetting);
                m_iblGIPass = new MaxIBLGIPass(setting.IBLCS);
            }
            catch
            {
                Dispose(true);
                throw;
            }
        }

        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            m_proceduralSkyboxPass.PruneDestroyedCameras();
            m_temporalAA.PruneDestroyedCameras();
            PruneCameraResources(cameras);
            PrepareOutputSizes(cameras);
            PruneOutputResources(cameras);
            SunLightData sun = SunLightData.Capture();
            ShaderBindings.SetPerFrameShaderVariables(context);
            foreach (Camera camera in cameras)
            {
                if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                    continue;
#if UNITY_EDITOR
                if (camera.cameraType == CameraType.SceneView)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif
                try
                {
                    RenderCamera(context, camera, sun);
                }
                catch
                {
                    m_temporalAA.ResetHistory(camera);
                    throw;
                }
                finally
                {
                    // Finish this camera before another camera changes shared material/state.
                    context.Submit();
                }
            }
        }

        private void RenderCamera(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            MaxTemporalAAPass.CameraHistory temporal = m_temporalAA.Begin(camera, m_setting.TemporalAA,
                m_outputCameraCounts[GetOutputKey(camera)] == 1, sun);
            context.SetupCameraProperties(camera);
            if (!camera.TryGetCullingParameters(out var cullingParams))
                return;
            // Culling and CSM use the unjittered frustum. Expand raster culling by one
            // pixel so temporal jitter cannot expose a culled silhouette at the border.
            if (camera.cameraType == CameraType.Game || temporal != null)
            {
                Matrix4x4 projection = CameraViewport.GetProjection(camera);
                Vector2Int cullSize = CameraViewport.GetRenderSize(camera);
                if (temporal != null)
                {
                    projection.SetRow(0, projection.GetRow(0) * (cullSize.x / (cullSize.x + 2f)));
                    projection.SetRow(1, projection.GetRow(1) * (cullSize.y / (cullSize.y + 2f)));
                }
                Matrix4x4 cullingMatrix = projection * camera.worldToCameraMatrix;
                cullingParams.cullingMatrix = cullingMatrix;
                GeometryUtility.CalculateFrustumPlanes(cullingMatrix, m_cameraCullingPlanes);
                cullingParams.cullingPlaneCount = m_cameraCullingPlanes.Length;
                for (int plane = 0; plane < m_cameraCullingPlanes.Length; ++plane)
                    cullingParams.SetCullingPlane(plane, m_cameraCullingPlanes[plane]);
            }
            cullingParams.shadowDistance = Mathf.Min(m_setting.CascadeSetting.ShadowDistance,
                camera.farClipPlane - camera.nearClipPlane);
            CullingResults cullingResults = context.Cull(ref cullingParams);
            m_lightConfigurator.SetupMultiShaderLightingParams(context, ref cullingResults, sun);
            bool hasShadows = m_shadowCastPass.Execute(context, camera, sun);

            context.SetupCameraProperties(camera);
            MaxTemporalAAPass.Bind(context, camera, temporal);
            Rect viewport = CameraViewport.GetViewport(camera);
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            int width = renderSize.x, height = renderSize.y;
            int cameraId = camera.GetInstanceID();
            if (!m_cameraResources.TryGetValue(cameraId, out CameraResources resources))
            {
                resources = new CameraResources(camera);
                m_cameraResources.Add(cameraId, resources);
            }
            resources.EnsureSize(width, height);

            long outputKey = GetOutputKey(camera);
            if (!m_outputResources.TryGetValue(outputKey, out OutputResources output))
            {
                output = new OutputResources();
                m_outputResources.Add(outputKey, output);
            }
            Vector2Int outputSize = m_outputSizes[outputKey];
            int outputWidth = outputSize.x, outputHeight = outputSize.y;
            output.EnsureSize(camera, outputWidth, outputHeight);

            CommandBuffer cmd = CommandBufferPool.Get("GBuffer");
            try
            {
                if (output.NeedsClear)
                {
                    cmd.SetRenderTarget(output.Color, output.Depth);
                    cmd.SetViewport(new Rect(0, 0, outputWidth, outputHeight));
                    cmd.ClearRenderTarget(true, true, Color.clear);
                }
                cmd.SetRenderTarget(resources.BufferIDs, resources.Depth);
                cmd.SetViewport(new Rect(0, 0, width, height));
                cmd.ClearRenderTarget(true, true, Color.clear);
                cmd.SetGlobalTexture("_GDepth", resources.Depth);
                for (int i = 0; i < resources.Buffers.Length; ++i)
                    cmd.SetGlobalTexture("_GBuffer" + i, resources.Buffers[i]);
                context.ExecuteCommandBuffer(cmd);
                output.NeedsClear = false;
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
            m_opaquePass.Execute(context, camera, ref cullingResults);
            m_temporalAA.DrawMotion(context, camera, temporal, resources.Depth, ref cullingResults);
            // Restore the offscreen scene convention before atmosphere and direct lighting.
            context.SetupCameraProperties(camera);
            MaxTemporalAAPass.Bind(context, camera, temporal);

            RenderTargetIdentifier target = camera.targetTexture != null
                ? new RenderTargetIdentifier(camera.targetTexture)
                : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            RenderTargetIdentifier outputDepth = output.OutputDepth != null
                ? new RenderTargetIdentifier(output.OutputDepth) : target;
            // Cameras sharing an output also share linear HDR color/depth. Overlay cameras must
            // never read a previously tone-mapped image as their scene background.
            // Fog LUTs must be generated for this camera before Lighting consumes them.
            m_proceduralSkyboxPass.PrepareAtmosphere(context, camera, sun, m_setting.SkyIBL.ReferenceHeight);
            // LUT generation intentionally uses an unjittered projection; restore the
            // raster matrices before reconstructing depth and drawing the procedural sky.
            MaxTemporalAAPass.Bind(context, camera, temporal);
            m_iblGIPass.Execute(context, m_proceduralSkyboxPass.EnvironmentSky,
                m_proceduralSkyboxPass.EnvironmentRevision, m_setting.SkyIBL);
            m_LightPass.Execute(context, camera, output.Color, output.Depth, viewport, m_iblGIPass, hasShadows);

            if (camera.clearFlags == CameraClearFlags.Skybox)
            {
                cmd = CommandBufferPool.Get("Procedural Skybox");
                try
                {
                    m_proceduralSkyboxPass.DrawSkybox(context, camera, cmd, output.Color, output.Depth, viewport);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }
            m_temporalAA.Resolve(context, camera, temporal, output.Color, resources.Depth, m_setting.TemporalAA);
#if UNITY_EDITOR
            if (Handles.ShouldRenderGizmos())
            {
                context.SetupCameraProperties(camera);
                ShaderBindings.SetPerCameraShaderVariables(context, camera, true);
                cmd = CommandBufferPool.Get("Gizmos After Temporal AA");
                try
                {
                    cmd.SetRenderTarget(output.Color, output.Depth);
                    cmd.SetViewport(viewport);
                    context.ExecuteCommandBuffer(cmd);
                }
                finally { CommandBufferPool.Release(cmd); }
                context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
            }
#endif
            m_colorOutputPass.Execute(context, camera, output.Color, output.Depth, target, outputDepth, m_setting.ColorOutput);
#if UNITY_EDITOR
            if (Handles.ShouldRenderGizmos())
            {
                context.SetupCameraProperties(camera);
                ShaderBindings.SetPerCameraShaderVariables(context, camera, MaxColorOutputPass.IsTextureDestination(camera));
                CommandBuffer gizmoViewport = CommandBufferPool.Get("Gizmo Viewport");
                try
                {
                    gizmoViewport.SetViewport(viewport);
                    context.ExecuteCommandBuffer(gizmoViewport);
                }
                finally
                {
                    CommandBufferPool.Release(gizmoViewport);
                }
                context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
            }
#endif
        }

        private static long GetOutputKey(Camera camera)
        {
            if (camera.targetTexture != null)
                return (1L << 32) | (uint)camera.targetTexture.GetInstanceID();
            // Game cameras on one display can stack. Scene/preview/reflection cameras own separate outputs.
            return camera.cameraType == CameraType.Game
                ? (2L << 32) | (uint)camera.targetDisplay
                : (3L << 32) | (uint)camera.GetInstanceID();
        }

        private static void GetOutputSize(Camera camera, out int width, out int height)
        {
            if (camera.targetTexture != null)
            {
                width = camera.targetTexture.width;
                height = camera.targetTexture.height;
                return;
            }
            // pixelWidth/Height describe a viewport; HDR color/depth cover the entire destination.
            width = Mathf.Max(Mathf.RoundToInt(camera.pixelRect.xMax),
                Mathf.RoundToInt(camera.pixelRect.width / Mathf.Max(camera.rect.width, 0.0001f)));
            height = Mathf.Max(Mathf.RoundToInt(camera.pixelRect.yMax),
                Mathf.RoundToInt(camera.pixelRect.height / Mathf.Max(camera.rect.height, 0.0001f)));
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
        }

        private void PrepareOutputSizes(Camera[] cameras)
        {
            m_outputSizes.Clear();
            m_outputCameraCounts.Clear();
            foreach (Camera camera in cameras)
            {
                if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0) continue;
                long key = GetOutputKey(camera);
                m_outputCameraCounts.TryGetValue(key, out int count);
                m_outputCameraCounts[key] = count + 1;
                GetOutputSize(camera, out int width, out int height);
                if (m_outputSizes.TryGetValue(key, out Vector2Int previous))
                {
                    width = Mathf.Max(width, previous.x);
                    height = Mathf.Max(height, previous.y);
                }
                m_outputSizes[key] = new Vector2Int(width, height);
            }
        }

        private void PruneOutputResources(Camera[] cameras)
        {
            m_unusedOutputs.Clear();
            foreach (var entry in m_outputResources)
            {
                if (!m_outputSizes.ContainsKey(entry.Key) && (entry.Value.LastCamera == null ||
                    GetOutputKey(entry.Value.LastCamera) != entry.Key)) m_unusedOutputs.Add(entry.Key);
            }
            foreach (long key in m_unusedOutputs)
            {
                m_outputResources[key].Dispose();
                m_outputResources.Remove(key);
            }
        }

        private void PruneCameraResources(Camera[] cameras)
        {
            m_unusedCameras.Clear();
            foreach (var entry in m_cameraResources)
            {
                // Render() can be called separately for Game, Scene and previews.
                // Absence from this invocation does not mean a live camera is gone.
                if (entry.Value.Camera == null)
                    m_unusedCameras.Add(entry.Key);
            }
            foreach (int id in m_unusedCameras)
            {
                m_cameraResources[id].Dispose();
                m_cameraResources.Remove(id);
            }
        }

        protected override void Dispose(bool disposing)
        {
            foreach (CameraResources resources in m_cameraResources.Values)
                resources.Dispose();
            m_cameraResources.Clear();
            foreach (OutputResources output in m_outputResources.Values)
                output.Dispose();
            m_outputResources.Clear();
            m_outputSizes.Clear();
            m_colorOutputPass?.Dispose();
            m_temporalAA?.Dispose();
            m_LightPass?.Dispose();
            m_iblGIPass?.Dispose();
            m_shadowCastPass?.Dispose();
            m_proceduralSkyboxPass?.Dispose();
            base.Dispose(disposing);
        }

        private sealed class CameraResources : IDisposable
        {
            public readonly Camera Camera;
            public RenderTexture Depth;
            public readonly RenderTexture[] Buffers = new RenderTexture[4];
            public readonly RenderTargetIdentifier[] BufferIDs = new RenderTargetIdentifier[4];
            public CameraResources(Camera camera)
            {
                Camera = camera;
            }

            public void EnsureSize(int width, int height)
            {
                width = Mathf.Max(1, width);
                height = Mathf.Max(1, height);
                if (Depth != null && Depth.width == width && Depth.height == height && Depth.IsCreated())
                {
                    return;
                }
                ReleaseBuffers();
                try
                {
                    Depth = CreateTarget(width, height, 24, RenderTextureFormat.Depth, "GDepth");
                    RenderTextureFormat[] formats = { RenderTextureFormat.ARGB32, RenderTextureFormat.ARGB2101010,
                        RenderTextureFormat.ARGB64, RenderTextureFormat.ARGBFloat };
                    for (int i = 0; i < Buffers.Length; ++i)
                    {
                        Buffers[i] = CreateTarget(width, height, 0, formats[i], "GBuffer" + i);
                        BufferIDs[i] = Buffers[i];
                    }
                }
                catch
                {
                    ReleaseBuffers();
                    throw;
                }
            }

            private void ReleaseBuffers()
            {
                MaxRenderResources.UnbindTexture("_GDepth", Depth);
                MaxRenderResources.Destroy(ref Depth);
                for (int i = 0; i < Buffers.Length; ++i)
                {
                    MaxRenderResources.UnbindTexture("_GBuffer" + i, Buffers[i]);
                    MaxRenderResources.Destroy(ref Buffers[i]);
                    BufferIDs[i] = default;
                }
            }

            public void Dispose()
            {
                ReleaseBuffers();
            }
        }

        private static RenderTexture CreateTarget(int width, int height, int depth, RenderTextureFormat format,
            string name, int samples = 1)
        {
            var target = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, antiAliasing = samples };
            if (!target.Create())
            {
                MaxRenderResources.Destroy(ref target);
                throw new InvalidOperationException("Unable to create " + name);
            }
            return target;
        }

        private sealed class OutputResources : IDisposable
        {
            public Camera LastCamera;
            public RenderTexture Color;
            public RenderTexture Depth;
            public RenderTexture OutputDepth;
            public bool NeedsClear;

            public void EnsureSize(Camera camera, int width, int height)
            {
                LastCamera = camera;
                if (Color == null || Depth == null || !Color.IsCreated() || !Depth.IsCreated() ||
                    Color.width != width || Color.height != height)
                {
                    Dispose();
                    try
                    {
                        Color = CreateTarget(width, height, 0, RenderTextureFormat.ARGBHalf, "SceneColorHDR");
                        Depth = CreateTarget(width, height, 24, RenderTextureFormat.Depth, "SceneDepthHDR");
                        NeedsClear = true;
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }
                if (camera.targetTexture != null && camera.targetTexture.depth == 0)
                {
                    int samples = camera.targetTexture.antiAliasing;
                    if (OutputDepth == null || !OutputDepth.IsCreated() || OutputDepth.antiAliasing != samples)
                    {
                        MaxRenderResources.Destroy(ref OutputDepth);
                        OutputDepth = CreateTarget(width, height, 24, RenderTextureFormat.Depth, "CameraOutputDepth", samples);
                    }
                }
                else
                    MaxRenderResources.Destroy(ref OutputDepth);
            }

            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_SceneColorHDR", Color);
                MaxRenderResources.UnbindTexture("_SceneDepthHDR", Depth);
                MaxRenderResources.Destroy(ref Color);
                MaxRenderResources.Destroy(ref Depth);
                MaxRenderResources.Destroy(ref OutputDepth);
            }
        }
    }

    internal static class MaxRenderResources
    {
        public static void Destroy<T>(ref T resource) where T : UnityEngine.Object
        {
            T owned = resource;
            resource = null;
            if (owned == null) return;
            if (owned is RenderTexture target) target.Release();
            CoreUtils.Destroy(owned);
        }

        public static void UnbindTexture(string name, Texture owned)
        {
            if (owned != null && Shader.GetGlobalTexture(name) == owned)
                Shader.SetGlobalTexture(name, (Texture)null);
        }
    }
}
