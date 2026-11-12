using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    internal sealed class MaxGTAOPass : IDisposable
    {
        private const int DepthMipCount = 5;
        private static readonly int DepthId = Shader.PropertyToID("_GDepth");
        private static readonly int[] DepthMipIds =
        {
            Shader.PropertyToID("_GTAODepthMip0"), Shader.PropertyToID("_GTAODepthMip1"),
            Shader.PropertyToID("_GTAODepthMip2"), Shader.PropertyToID("_GTAODepthMip3"),
            Shader.PropertyToID("_GTAODepthMip4")
        };

        private RenderTexture m_viewDepth, m_raw, m_edges, m_visibility;
        private ComputeShader m_shader;
        private int m_prefilter, m_evaluate, m_denoise;
        private bool m_kernelsSupported;

        internal RenderTexture Execute(ScriptableRenderContext context, Camera camera, MaxGTAOSettings settings,
            RenderTexture depth, RenderTexture normals, RenderTexture colorTarget, MaxTemporalAAPass.CameraHistory temporal)
        {
            if (!settings || !settings.Compute || !(settings.Strength > 0.0f) || !(settings.Radius > 0.0f) ||
                !SystemInfo.supportsComputeShaders || camera.orthographic || depth.width < 16 || depth.height < 16 ||
                !CanWrite(GraphicsFormat.R32_SFloat) || !CanWrite(GraphicsFormat.R32_UInt) ||
                !CanWrite(GraphicsFormat.R8_UNorm))
            {
                Dispose();
                return null;
            }

            var shader = settings.Compute;
            if (shader != m_shader)
            {
                m_shader = shader;
                m_kernelsSupported = TryGetKernel(shader, "PrefilterDepths", out m_prefilter) &&
                    TryGetKernel(shader, "EvaluateGTAO", out m_evaluate) &&
                    TryGetKernel(shader, "DenoiseGTAO", out m_denoise);
            }
            if (!m_kernelsSupported) { Dispose(); return null; }
            int width = depth.width, height = depth.height;
            EnsureSize(width, height);
            var projection = temporal == null
                ? GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true)
                : temporal.Projection;
            var cmd = CommandBufferPool.Get("XeGTAO");
            try
            {
                cmd.BeginSample("XeGTAO");
                // Detach the GBuffer depth attachment before binding it for compute reads.
                cmd.SetRenderTarget(colorTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetComputeVectorParam(shader, "_GTAOViewport", new Vector4(width, height, 1.0f / width, 1.0f / height));
                cmd.SetComputeMatrixParam(shader, "_GTAOInvProjection", projection.inverse);
                cmd.SetComputeMatrixParam(shader, "_GTAOView", camera.worldToCameraMatrix);
                cmd.SetComputeFloatParam(shader, "_GTAORadius", Mathf.Clamp(settings.Radius, 0.05f, 3.0f));
                cmd.SetComputeIntParam(shader, "_GTAONoiseIndex", temporal == null ? 0 : temporal.Sample & 63);
                cmd.SetComputeIntParam(shader, "_GTAOQuality", Mathf.Clamp(settings.Quality, 0, 2));

                cmd.SetComputeTextureParam(shader, m_prefilter, DepthId, depth, 0, RenderTextureSubElement.Depth);
                for (int i = 0; i < DepthMipCount; ++i)
                    cmd.SetComputeTextureParam(shader, m_prefilter, DepthMipIds[i], m_viewDepth, i);
                cmd.DispatchCompute(shader, m_prefilter, Groups(width, 16), Groups(height, 16), 1);

                cmd.SetComputeTextureParam(shader, m_evaluate, DepthId, depth, 0, RenderTextureSubElement.Depth);
                cmd.SetComputeTextureParam(shader, m_evaluate, "_GBuffer1", normals);
                cmd.SetComputeTextureParam(shader, m_evaluate, "_GTAOViewDepth", m_viewDepth);
                cmd.SetComputeTextureParam(shader, m_evaluate, "_GTAORawAO", m_raw);
                cmd.SetComputeTextureParam(shader, m_evaluate, "_GTAOEdges", m_edges);
                cmd.DispatchCompute(shader, m_evaluate, Groups(width, 8), Groups(height, 8), 1);

                cmd.SetComputeTextureParam(shader, m_denoise, DepthId, depth, 0, RenderTextureSubElement.Depth);
                cmd.SetComputeTextureParam(shader, m_denoise, "_GTAORawAOInput", m_raw);
                cmd.SetComputeTextureParam(shader, m_denoise, "_GTAOEdgesInput", m_edges);
                cmd.SetComputeTextureParam(shader, m_denoise, "_GTAOOutput", m_visibility);
                cmd.DispatchCompute(shader, m_denoise, Groups(width, 16), Groups(height, 8), 1);
                cmd.EndSample("XeGTAO");
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            return m_visibility;
        }

        private static bool CanWrite(GraphicsFormat format) =>
            SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore);

        private static bool TryGetKernel(ComputeShader shader, string name, out int kernel)
        {
            kernel = shader.HasKernel(name) ? shader.FindKernel(name) : -1;
            return kernel >= 0 && shader.IsSupported(kernel);
        }

        private static int Groups(int size, int groupSize) => (size + groupSize - 1) / groupSize;

        private void EnsureSize(int width, int height)
        {
            if (m_visibility && m_visibility.width == width && m_visibility.height == height &&
                m_visibility.IsCreated() && m_viewDepth.IsCreated() && m_raw.IsCreated() && m_edges.IsCreated())
                return;

            Dispose();
            try
            {
                m_viewDepth = Create(width, height, GraphicsFormat.R32_SFloat, DepthMipCount, "GTAO view depth");
                m_raw = Create(width, height, GraphicsFormat.R32_UInt, 1, "GTAO raw");
                m_edges = Create(width, height, GraphicsFormat.R8_UNorm, 1, "GTAO edges");
                m_visibility = Create(width, height, GraphicsFormat.R32_SFloat, 1, "GTAO visibility");
            }
            catch { Dispose(); throw; }
        }

        private static RenderTexture Create(int width, int height, GraphicsFormat format, int mips, string name)
        {
            var descriptor = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format,
                depthBufferBits = 0,
                enableRandomWrite = true,
                useMipMap = mips > 1,
                autoGenerateMips = false,
                mipCount = mips
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (texture.Create()) return texture;
            MaxRenderResources.Destroy(ref texture);
            throw new InvalidOperationException("Unable to create " + name);
        }

        public void Dispose()
        {
            MaxRenderResources.Destroy(ref m_viewDepth);
            MaxRenderResources.Destroy(ref m_raw);
            MaxRenderResources.Destroy(ref m_edges);
            MaxRenderResources.Destroy(ref m_visibility);
        }
    }
}
