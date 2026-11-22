using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // 每个相机独立持有 GBuffer、AO、线性场景颜色和深度。
    internal sealed class MaxRenderTargets : IDisposable
    {
        private static readonly int[] s_gbufferIds = {
            Shader.PropertyToID("_GBuffer0"), Shader.PropertyToID("_GBuffer1"),
            Shader.PropertyToID("_GBuffer2"), Shader.PropertyToID("_GBuffer3") };

        private readonly Dictionary<int, CameraResources> m_cameraResources = new Dictionary<int, CameraResources>();
        private readonly List<int> m_unusedCameras = new List<int>();
        private readonly RenderTextureFormat m_mobileSceneColorFormat;

        public MaxRenderTargets()
        {
            // The packed HDR target also receives blended sky, cloud and gizmo draws.
            m_mobileSceneColorFormat = MaxRenderResources.SupportsPackedHdrTarget(requireBlending: true)
                ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGBHalf;
        }

        // 查询已有的相机资源，查询本身不会创建纹理。
        public CameraResources FindCamera(Camera camera) =>
            m_cameraResources.TryGetValue(camera.GetInstanceID(), out CameraResources resources) ? resources : null;

        // 获取相机专用资源，按视口尺寸分配 GBuffer 和 HDR 缓冲。
        public CameraResources GetCamera(Camera camera, int width, int height, MaxRenderPlatform platform)
        {
            var resources = FindCamera(camera);
            if (resources == null)
            {
                resources = new CameraResources(camera);
                m_cameraResources.Add(camera.GetInstanceID(), resources);
            }

            resources.EnsureSize(width, height);
            RenderTextureFormat colorFormat = platform == MaxRenderPlatform.Mobile && !MaxRenderResources.RequiresSceneAlpha(camera)
                ? m_mobileSceneColorFormat : RenderTextureFormat.ARGBHalf;
            resources.Output.EnsureSize(camera.targetTexture, width, height, colorFormat);
            return resources;
        }

        // 释放已销毁相机持有的渲染资源。
        public void PruneDestroyedCameras()
        {
            m_unusedCameras.Clear();
            foreach (var entry in m_cameraResources)
            {
                // 游戏、场景和预览相机可能分批调用 Render。
                // 仍存活的相机会保留资源，即使本次渲染没有包含它。
                if (entry.Value.Camera == null)
                    m_unusedCameras.Add(entry.Key);
            }

            foreach (int id in m_unusedCameras)
            {
                m_cameraResources[id].Dispose();
                m_cameraResources.Remove(id);
            }
        }

        // 释放全部相机持有的渲染资源。
        public void Dispose()
        {
            foreach (CameraResources resources in m_cameraResources.Values)
                resources.Dispose();
            m_cameraResources.Clear();
        }

        internal sealed class CameraResources : IDisposable
        {
            public readonly Camera Camera;
            public bool IsMoonMain;
            public RenderTexture Depth;
            public readonly RenderTexture[] Buffers = new RenderTexture[4];
            public readonly RenderTargetIdentifier[] BufferIDs = new RenderTargetIdentifier[4];
            public readonly OutputResources Output = new OutputResources();
            private readonly MaxGTAOPass m_gtao = new MaxGTAOPass();
            private readonly MaxSSAOPass m_ssao = new MaxSSAOPass();
            // 保存此资源组所属的相机，纹理按需分配。
            public CameraResources(Camera camera)
            {
                Camera = camera;
            }

            // 当尺寸变化或 GPU 纹理失效时，重建该相机的 GBuffer 附件。
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
                        Buffers[i] = CreateTarget(width, height, 0, formats[i], "GBuffer" + i,
                            readWrite: i == 0 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                        BufferIDs[i] = Buffers[i];
                    }
                }
                catch
                {
                    ReleaseBuffers();
                    throw;
                }
            }

            // 将当前相机的深度和 GBuffer 纹理绑定给后续 Shader 使用。
            public void BindGBuffer(CommandBuffer commands)
            {
                commands.SetGlobalTexture("_GDepth", Depth);
                for (int i = 0; i < Buffers.Length; ++i)
                    commands.SetGlobalTexture(s_gbufferIds[i], Buffers[i]);
            }

            // 使用该相机的 GBuffer 和 TAA 状态，执行当前档位的 AO 通道。
            public RenderTexture RenderAmbientOcclusion(ScriptableRenderContext context, MaxAmbientOcclusion settings,
                RenderTexture sceneColor, MaxTemporalAAPass.CameraHistory temporal)
            {
                // 只保留当前 AO 算法使用的临时纹理。
                if (settings.Platform == AOPlatform.Mobile)
                {
                    m_gtao.Dispose();
                    return m_ssao.Execute(context, Camera, settings, Depth, sceneColor, temporal);
                }

                m_ssao.Dispose();
                return m_gtao.Execute(context, Camera, settings, Depth, Buffers[1], sceneColor, temporal);
            }

            // 解除相机专用纹理的绑定并释放资源，同时清空渲染目标标识。
            private void ReleaseBuffers()
            {
                MaxRenderResources.UnbindTexture("_GDepth", Depth);
                MaxRenderResources.UnbindTexture("_SceneDepthHDR", Depth);
                MaxRenderResources.Destroy(ref Depth);

                for (int i = 0; i < Buffers.Length; ++i)
                {
                    MaxRenderResources.UnbindTexture(s_gbufferIds[i], Buffers[i]);
                    MaxRenderResources.Destroy(ref Buffers[i]);
                    BufferIDs[i] = default;
                }
            }

            // 释放此相机的 AO、GBuffer 和 HDR 输出资源。
            public void Dispose()
            {
                m_gtao.Dispose();
                m_ssao.Dispose();
                ReleaseBuffers();
                Output.Dispose();
            }
        }

        // 分配渲染目标，创建失败时释放对应对象。
        private static RenderTexture CreateTarget(int width, int height, int depth, RenderTextureFormat format,
            string name, int samples = 1, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Linear)
        {
            var target = new RenderTexture(width, height, depth, format, readWrite)
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, antiAliasing = samples };
            if (!target.Create())
            {
                MaxRenderResources.Destroy(ref target);
                throw new InvalidOperationException("Unable to create " + name);
            }
            return target;
        }

        internal sealed class OutputResources : IDisposable
        {
            public RenderTexture Color;
            public RenderTexture OutputDepth;
            private RenderTextureFormat m_colorFormat;

            // 按相机视口配置 HDR 缓冲，按最终目标配置额外的深度附件。
            public void EnsureSize(RenderTexture destination, int width, int height, RenderTextureFormat colorFormat)
            {
                if (Color == null || !Color.IsCreated() ||
                    Color.width != width || Color.height != height || m_colorFormat != colorFormat)
                {
                    Dispose();
                    try
                    {
                        Color = CreateTarget(width, height, 0, colorFormat, "SceneColorHDR");
                        m_colorFormat = colorFormat;
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }

                // 如果输出纹理只有颜色缓冲，就为最终输出另外分配匹配的深度附件。
                if (destination != null && destination.depth == 0)
                {
                    int samples = destination.antiAliasing;
                    if (OutputDepth == null || !OutputDepth.IsCreated() || OutputDepth.antiAliasing != samples ||
                        OutputDepth.width != destination.width || OutputDepth.height != destination.height)
                    {
                        MaxRenderResources.Destroy(ref OutputDepth);
                        OutputDepth = CreateTarget(destination.width, destination.height, 24,
                            RenderTextureFormat.Depth, "CameraOutputDepth", samples);
                    }
                }
                else
                    MaxRenderResources.Destroy(ref OutputDepth);
            }

            // 释放相机的场景缓冲和按需分配的输出深度附件。
            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_SceneColorHDR", Color);

                MaxRenderResources.Destroy(ref Color);
                MaxRenderResources.Destroy(ref OutputDepth);
            }
        }
    }
}
