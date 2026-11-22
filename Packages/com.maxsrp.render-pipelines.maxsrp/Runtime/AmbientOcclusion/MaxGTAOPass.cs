using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    internal sealed class MaxGTAOPass : IDisposable
    {
        private const int DepthMipCount = 5;
        private const int HilbertSize = 64;
        private static readonly string[] EvaluateKernels =
            { "EvaluateGTAOLow", "EvaluateGTAOMedium", "EvaluateGTAOHigh", "EvaluateGTAOUltra" };
        private static readonly int DepthId = Shader.PropertyToID("_GDepth");
        private static readonly int[] DepthMipIds =
        {
            Shader.PropertyToID("_GTAODepthMip0"), Shader.PropertyToID("_GTAODepthMip1"),
            Shader.PropertyToID("_GTAODepthMip2"), Shader.PropertyToID("_GTAODepthMip3"),
            Shader.PropertyToID("_GTAODepthMip4")
        };

        private RenderTexture m_viewDepth, m_raw, m_edges, m_visibility, m_denoiseScratch;
        private RenderTexture m_hilbertLut;
        private ComputeShader m_shader;
        private LocalKeyword m_fp32Depths;
        private readonly int[] m_evaluate = new int[EvaluateKernels.Length];
        private int m_buildHilbertLut, m_prefilter, m_denoise, m_denoiseIntermediate;
        private bool m_kernelsSupported;

        internal RenderTexture Execute(ScriptableRenderContext context, Camera camera, MaxAmbientOcclusion settings,
            RenderTexture depth, RenderTexture normals, RenderTexture colorTarget, MaxTemporalAAPass.CameraHistory temporal)
        {
            var cmd = CommandBufferPool.Get("XeGTAO");
            try
            {
                RenderTexture result = Record(cmd, camera, settings, depth, normals, colorTarget, temporal);
                context.ExecuteCommandBuffer(cmd);
                return result;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        // Shared by production rendering and controlled GPU validation. No
        // ScriptableRenderContext is manufactured by the diagnostic caller.
        internal RenderTexture Record(CommandBuffer cmd, Camera camera, MaxAmbientOcclusion settings,
            RenderTexture depth, RenderTexture normals, RenderTexture colorTarget, MaxTemporalAAPass.CameraHistory temporal)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!settings.AOEnabled || !settings.Compute || !(settings.Strength > 0.0f) || !(settings.Radius > 0.0f) ||
                !SystemInfo.supportsComputeShaders || camera.orthographic || depth.width < 16 || depth.height < 16)
            {
                Dispose();
                return null;
            }

            bool use32BitDepth = settings.Use32BitWorkingDepth;
            GraphicsFormat depthFormat = use32BitDepth ? GraphicsFormat.R32_SFloat : GraphicsFormat.R16_SFloat;
            // Unity's Sample query can be false for integer textures that support Load and GatherRed.
            if (!CanUseWorkingTexture(depthFormat) || !CanUseWorkingTexture(GraphicsFormat.R8_UNorm) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R8_UInt, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UInt, FormatUsage.LoadStore))
            {
                Dispose();
                return null;
            }

            var shader = settings.Compute;
            if (shader != m_shader)
            {
                m_shader = shader;
                m_fp32Depths = shader.keywordSpace.FindKeyword("XE_GTAO_FP32_DEPTHS");
                m_kernelsSupported = m_fp32Depths.isValid &&
                    TryGetKernel(shader, "BuildHilbertLut", out m_buildHilbertLut) &&
                    TryGetKernel(shader, "PrefilterDepths", out m_prefilter) &&
                    TryGetKernel(shader, "DenoiseGTAO", out m_denoise) &&
                    TryGetKernel(shader, "DenoiseGTAOIntermediate", out m_denoiseIntermediate);
                for (int i = 0; i < EvaluateKernels.Length && m_kernelsSupported; ++i)
                    m_kernelsSupported = TryGetKernel(shader, EvaluateKernels[i], out m_evaluate[i]);
            }
            if (!m_kernelsSupported) { Dispose(); return null; }
            int width = depth.width, height = depth.height;
            int denoisePasses = Mathf.Clamp(settings.DenoisePasses, 0, 3);
            int evaluate = m_evaluate[Mathf.Clamp(settings.Quality, 0, EvaluateKernels.Length - 1)];
            var projection = temporal == null
                ? GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true)
                : temporal.Projection;
            // Intel's scalar depth unpack requires device Z to depend only on view-space Z.
            if (projection.m20 != 0f || projection.m21 != 0f || projection.m30 != 0f || projection.m31 != 0f ||
                projection.m32 == 0f || projection.m33 != 0f)
            {
                Dispose();
                return null;
            }
            EnsureSize(width, height, depthFormat);
            cmd.SetKeyword(shader, m_fp32Depths, use32BitDepth);
            if (!m_hilbertLut || !m_hilbertLut.IsCreated())
            {
                MaxRenderResources.Destroy(ref m_hilbertLut);
                m_hilbertLut = Create(HilbertSize, HilbertSize, GraphicsFormat.R16_UInt, 1, "GTAO Hilbert LUT");
                // Unity's Texture2D constructor requires filtered Sample support. Build the same integer LUT once via UAV.
                cmd.SetComputeTextureParam(shader, m_buildHilbertLut, "_GTAOHilbertLUTOutput", m_hilbertLut);
                cmd.DispatchCompute(shader, m_buildHilbertLut, Groups(HilbertSize, 8), Groups(HilbertSize, 8), 1);
            }
            cmd.BeginSample("XeGTAO");
            // Detach the GBuffer depth attachment before binding it for compute reads.
            cmd.SetRenderTarget(colorTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cmd.SetComputeVectorParam(shader, "_GTAOViewport", new Vector4(width, height, 1.0f / width, 1.0f / height));
            BindProjection(cmd, shader, projection, camera.worldToCameraMatrix);
            cmd.SetComputeFloatParam(shader, "_GTAORadius", Mathf.Clamp(settings.Radius, 0.05f, 3.0f));
            cmd.SetComputeIntParam(shader, "_GTAONoiseIndex", denoisePasses == 0 || temporal == null ? 0 : temporal.Sample & 63);
            // Intel's disabled mode still runs the final conversion with a
            // dominant centre weight; it must not expose the packed uint AO.
            cmd.SetComputeFloatParam(shader, "_GTAODenoiseBlurBeta", denoisePasses == 0 ? 1e4f : 1.2f);

            cmd.SetComputeTextureParam(shader, m_prefilter, DepthId, depth, 0, RenderTextureSubElement.Depth);
            for (int i = 0; i < DepthMipCount; ++i)
                cmd.SetComputeTextureParam(shader, m_prefilter, DepthMipIds[i], m_viewDepth, i);
            cmd.DispatchCompute(shader, m_prefilter, Groups(width, 16), Groups(height, 16), 1);

            cmd.SetComputeTextureParam(shader, evaluate, "_GBuffer1", normals);
            cmd.SetComputeTextureParam(shader, evaluate, "_GTAOViewDepth", m_viewDepth);
            cmd.SetComputeTextureParam(shader, evaluate, "_GTAOHilbertLUT", m_hilbertLut);
            cmd.SetComputeTextureParam(shader, evaluate, "_GTAORawAO", m_raw);
            cmd.SetComputeTextureParam(shader, evaluate, "_GTAOEdges", m_edges);
            cmd.DispatchCompute(shader, evaluate, Groups(width, 8), Groups(height, 8), 1);

            RenderTexture input = m_raw;
            RenderTexture scratch = m_denoiseScratch;
            int dispatchCount = Mathf.Max(1, denoisePasses);
            for (int i = 0; i < dispatchCount; ++i)
            {
                bool final = i == dispatchCount - 1;
                int kernel = final ? m_denoise : m_denoiseIntermediate;
                cmd.SetComputeTextureParam(shader, kernel, "_GTAORawAOInput", input);
                cmd.SetComputeTextureParam(shader, kernel, "_GTAOEdgesInput", m_edges);
                cmd.SetComputeTextureParam(shader, kernel, "_GTAOOutput", final ? m_visibility : scratch);
                cmd.DispatchCompute(shader, kernel, Groups(width, 16), Groups(height, 8), 1);
                if (!final) (input, scratch) = (scratch, input);
            }
            cmd.EndSample("XeGTAO");
            return m_visibility;
        }

        private static void BindProjection(CommandBuffer cmd, ComputeShader shader, Matrix4x4 projection, Matrix4x4 view)
        {
            // XeGTAO.h GTAOUpdateConstants, adapted to Unity's GPU projection, lens shift and jitter.
            cmd.SetComputeVectorParam(shader, "_GTAODepthUnpack", new Vector4(
                projection.m23 / projection.m32, projection.m22 / projection.m32, 0f, 0f));
            float top = SystemInfo.graphicsUVStartsAtTop ? 1f : -1f;
            Matrix4x4 inverse = projection.inverse;
            Vector2 origin = ViewRay(inverse, -1f, top);
            Vector2 extent = ViewRay(inverse, 1f, -top) - origin;
            Vector2 axisSign = new Vector2(Mathf.Sign(extent.x), -Mathf.Sign(extent.y));
            cmd.SetComputeVectorParam(shader, "_GTAOViewRays", new Vector4(
                extent.x * axisSign.x, extent.y * axisSign.y, origin.x * axisSign.x, origin.y * axisSign.y));
            cmd.SetComputeMatrixParam(shader, "_GTAOView", Matrix4x4.Scale(new Vector3(axisSign.x, axisSign.y, -1f)) * view);
        }

        private static Vector2 ViewRay(Matrix4x4 inverse, float x, float y)
        {
            Vector4 position = inverse * new Vector4(x, y, 0.5f, 1f);
            return new Vector2(position.x / -position.z, position.y / -position.z);
        }

        private static bool CanUseWorkingTexture(GraphicsFormat format) =>
            SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore) &&
            SystemInfo.IsFormatSupported(format, FormatUsage.Sample);

        private static bool TryGetKernel(ComputeShader shader, string name, out int kernel)
        {
            kernel = shader.HasKernel(name) ? shader.FindKernel(name) : -1;
            return kernel >= 0 && shader.IsSupported(kernel);
        }

        private static int Groups(int size, int groupSize) => (size + groupSize - 1) / groupSize;

        private void EnsureSize(int width, int height, GraphicsFormat depthFormat)
        {
            bool valid = m_visibility && m_viewDepth && m_raw && m_edges && m_denoiseScratch &&
                m_visibility.width == width && m_visibility.height == height &&
                m_viewDepth.graphicsFormat == depthFormat &&
                m_visibility.IsCreated() && m_viewDepth.IsCreated() && m_raw.IsCreated() &&
                m_edges.IsCreated() && m_denoiseScratch.IsCreated();
            if (valid) return;

            try
            {
                ReleaseTargets();
                m_viewDepth = Create(width, height, depthFormat, DepthMipCount, "GTAO view depth");
                m_raw = Create(width, height, GraphicsFormat.R8_UInt, 1, "GTAO raw");
                m_edges = Create(width, height, GraphicsFormat.R8_UNorm, 1, "GTAO edges");
                m_visibility = Create(width, height, GraphicsFormat.R8_UInt, 1, "GTAO visibility");
                m_denoiseScratch = Create(width, height, GraphicsFormat.R8_UInt, 1, "GTAO denoise scratch");
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

        private void ReleaseTargets()
        {
            MaxRenderResources.Destroy(ref m_viewDepth);
            MaxRenderResources.Destroy(ref m_raw);
            MaxRenderResources.Destroy(ref m_edges);
            MaxRenderResources.Destroy(ref m_visibility);
            MaxRenderResources.Destroy(ref m_denoiseScratch);
        }

        public void Dispose()
        {
            ReleaseTargets();
            MaxRenderResources.Destroy(ref m_hilbertLut);
        }
    }
}
