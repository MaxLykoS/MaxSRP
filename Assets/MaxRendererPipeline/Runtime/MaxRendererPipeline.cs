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
        private MaxLightPass m_LightPass;
        private MaxColorOutputPass m_colorOutputPass;
        private MaxIBLGIPass m_iblGIPass;
        private MaxShadowCasterPass m_shadowCastPass;
        private MaxProceduralSkyboxPass m_proceduralSkyboxPass;
        private readonly MaxRendererPipelineAsset m_setting;

        public MaxRenderPipeline(MaxRendererPipelineAsset setting)
        {
            GraphicsSettings.useScriptableRenderPipelineBatching = true;
            m_setting = setting;
            try
            {
                m_LightPass = new MaxLightPass();
                m_colorOutputPass = new MaxColorOutputPass(setting.ColorOutput);
                m_shadowCastPass = new MaxShadowCasterPass(setting.CascadeSetting);
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
                finally
                {
                    // Finish this camera before another camera changes shared material/state.
                    context.Submit();
                }
            }
        }

        private void RenderCamera(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            context.SetupCameraProperties(camera);
            if (!camera.TryGetCullingParameters(out var cullingParams))
                return;
            // Culling must use the same viewport-dependent projection as rasterization.
            if (camera.cameraType == CameraType.Game)
            {
                Matrix4x4 cullingMatrix = CameraViewport.GetProjection(camera) * camera.worldToCameraMatrix;
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
            ShaderBindings.SetPerCameraShaderVariables(context, camera, true);
            Rect viewport = CameraViewport.GetViewport(camera);
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            int width = renderSize.x, height = renderSize.y;
            int cameraId = camera.GetInstanceID();
            if (!m_cameraResources.TryGetValue(cameraId, out CameraResources resources))
            {
                resources = new CameraResources(camera, m_setting.SSShadowMapShader);
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
            resources.Shadows.Execute(context, camera, hasShadows);
            // Utility Blits may alter built-in camera matrices. Restore the offscreen scene convention.
            context.SetupCameraProperties(camera);
            ShaderBindings.SetPerCameraShaderVariables(context, camera, true);

            RenderTargetIdentifier target = camera.targetTexture != null
                ? new RenderTargetIdentifier(camera.targetTexture)
                : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            RenderTargetIdentifier outputDepth = output.OutputDepth != null
                ? new RenderTargetIdentifier(output.OutputDepth) : target;
            // Cameras sharing an output also share linear HDR color/depth. Overlay cameras must
            // never read a previously tone-mapped image as their scene background.
            // Fog LUTs must be generated for this camera before Lighting consumes them.
            m_proceduralSkyboxPass.PrepareAtmosphere(context, camera, sun, m_setting.SkyIBL.ReferenceHeight);
            m_iblGIPass.Execute(context, m_proceduralSkyboxPass.EnvironmentSky,
                m_proceduralSkyboxPass.EnvironmentRevision, m_setting.SkyIBL);
            m_LightPass.Execute(context, camera, output.Color, output.Depth, viewport, m_iblGIPass);

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
#if UNITY_EDITOR
            if (Handles.ShouldRenderGizmos())
            {
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
            foreach (Camera camera in cameras)
            {
                if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0) continue;
                long key = GetOutputKey(camera);
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
                if (!m_outputSizes.ContainsKey(entry.Key)) m_unusedOutputs.Add(entry.Key);
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
                bool active = false;
                foreach (Camera camera in cameras)
                    if (camera != null && camera == entry.Value.Camera) { active = true; break; }
                if (!active)
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
            public readonly MaxScreenSpaceShadowMapPass Shadows;

            public CameraResources(Camera camera, Shader shadowShader)
            {
                Camera = camera;
                Shadows = new MaxScreenSpaceShadowMapPass(shadowShader);
            }

            public void EnsureSize(int width, int height)
            {
                width = Mathf.Max(1, width);
                height = Mathf.Max(1, height);
                if (Depth != null && Depth.width == width && Depth.height == height && Depth.IsCreated())
                {
                    Shadows.EnsureSize(width, height);
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
                    Shadows.EnsureSize(width, height);
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
                Shadows.Dispose();
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
            public RenderTexture Color;
            public RenderTexture Depth;
            public RenderTexture OutputDepth;
            public bool NeedsClear;

            public void EnsureSize(Camera camera, int width, int height)
            {
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
