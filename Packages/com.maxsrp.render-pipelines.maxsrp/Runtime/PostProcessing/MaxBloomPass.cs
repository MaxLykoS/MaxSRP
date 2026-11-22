using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // UE 5.8 standard bloom: six weighted, separable Gaussian scales on PC.
    // Mobile Epic uses a half-resolution setup followed by six radial downsamples.
    // All textures remain in MaxSRP's fixed scene-HDR scale. Exposure is used
    // only to evaluate the threshold; the final output applies exposure once.
    public sealed class MaxBloomPass : IDisposable
    {
        private const int PCStageCount = 6;
        private const int MobileDownsampleCount = 6;
        private const int MobileStageCount = 1 + MobileDownsampleCount;
        private const int KernelSampleLimit = 32;
        private static readonly float[] StageSizes = { 0.3f, 1f, 2f, 10f, 30f, 64f };
        private static readonly float[] StageTints = { 0.3465f, 0.138f, 0.1176f, 0.066f, 0.066f, 0.061f };
        private static readonly int SourceID = Shader.PropertyToID("_MaxBloomSource");
        private static readonly int LowID = Shader.PropertyToID("_MaxBloomLow");
        private static readonly int SourceTexelID = Shader.PropertyToID("_MaxBloomSourceTexel");
        private static readonly int LowTexelID = Shader.PropertyToID("_MaxBloomLowTexel");
        private static readonly int SourceRectID = Shader.PropertyToID("_MaxBloomSourceRect");
        private static readonly int ParametersID = Shader.PropertyToID("_MaxBloomParameters");
        private static readonly int ManualExposureID = Shader.PropertyToID("_MaxBloomManualExposure");
        private static readonly int KernelID = Shader.PropertyToID("_MaxBloomKernel");
        private static readonly int KernelCountID = Shader.PropertyToID("_MaxBloomKernelCount");
        private static readonly int FilterAxisID = Shader.PropertyToID("_MaxBloomFilterAxis");
        private static readonly int[] s_down = CreateTargetIds("_MaxBloomDown", MobileStageCount);
        private static readonly int[] s_up = CreateTargetIds("_MaxBloomUp", MobileDownsampleCount);
        private static readonly int s_scratch = Shader.PropertyToID("_MaxBloomGaussianX");
        private readonly Vector2Int[] m_sizes = new Vector2Int[MobileStageCount];
        private readonly Vector4[] m_kernel = new Vector4[KernelSampleLimit];
        private readonly bool m_supportsBloomTargets;
        private readonly RenderTextureFormat m_bloomFormat;
        private Material m_material;

        public readonly struct Output
        {
            public readonly Vector2Int Size;
            public bool IsValid => Size.x > 0;
            public RenderTargetIdentifier Target => new RenderTargetIdentifier(s_up[0]);
            internal Output(Vector2Int size) => Size = size;

            // Record this after the final consumer on the same render context.
            public void Release(CommandBuffer cmd)
            {
                if (IsValid) cmd.ReleaseTemporaryRT(s_up[0]);
            }
        }

        public MaxBloomPass(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires the serialized Hidden/MaxSRP/PostProcessBloom shader.");
            // Hardware support is stable for this pass; Unity's enum-validation
            // wrapper allocates when queried for every camera recording.
            bool supportsPacked = MaxRenderResources.SupportsPackedHdrTarget();
            m_bloomFormat = supportsPacked ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGBHalf;
            m_supportsBloomTargets = supportsPacked || SystemInfo.SupportsRenderTextureFormat(m_bloomFormat);
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public Output Execute(ScriptableRenderContext context, RenderTexture sceneHDR,
            Rect viewport, GraphicsBuffer exposure, float manualExposure, BloomSettings settings, PostProcessPlatform platform)
        {
            CommandBuffer cmd = CommandBufferPool.Get(platform == PostProcessPlatform.Mobile ? "Bloom Mobile" : "Bloom PC");
            try
            {
                Output result = Record(cmd, sceneHDR, viewport, exposure, manualExposure, settings, platform);
                context.ExecuteCommandBuffer(cmd);
                return result;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        // The same recording path is used by production rendering and GPU validation.
        public Output Record(CommandBuffer cmd, RenderTexture sceneHDR,
            Rect viewport, GraphicsBuffer exposure, float manualExposure, BloomSettings settings, PostProcessPlatform platform)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (settings == null || !settings.Enabled || !(settings.Intensity > 0f) ||
                sceneHDR == null || viewport.width < 1f || viewport.height < 1f ||
                !m_supportsBloomTargets)
                return default;

            var size = new Vector2Int(Mathf.RoundToInt(viewport.width), Mathf.RoundToInt(viewport.height));
            int count = platform == PostProcessPlatform.Mobile ? MobileStageCount : PCStageCount;
            for (int i = 0; i < count; ++i)
            {
                size = new Vector2Int((size.x + 1) / 2, (size.y + 1) / 2);
                m_sizes[i] = size;
            }
            float intensity = Mathf.Clamp(settings.Intensity, 0f, 64f);
            float sizeScale = float.IsNaN(settings.SizeScale) || float.IsInfinity(settings.SizeScale)
                ? 4f : Mathf.Clamp(settings.SizeScale, 0.1f, 8f);
            float threshold = float.IsNaN(settings.Threshold) ? -1f : Mathf.Max(-1f, settings.Threshold);
            cmd.SetGlobalFloat(ManualExposureID, manualExposure);
            BindSource(cmd, sceneHDR, new Vector2Int(sceneHDR.width, sceneHDR.height));
            cmd.SetGlobalVector(SourceRectID, new Vector4(viewport.width / sceneHDR.width, viewport.height / sceneHDR.height,
                viewport.x / sceneHDR.width, viewport.y / sceneHDR.height));
            cmd.SetGlobalVector(ParametersID, new Vector4(threshold, 0f, 0f, 0f));
            MaxAutoExposurePass.BindForDraw(cmd, exposure);
            Draw(cmd, s_down[0], m_sizes[0], platform == PostProcessPlatform.Mobile ? 4 : 0);
            if (exposure != null) MaxAutoExposurePass.BindForDraw(cmd, null);
            cmd.SetGlobalVector(SourceRectID, new Vector4(1f, 1f, 0f, 0f));
            if (platform == PostProcessPlatform.Mobile)
                ExecuteMobile(cmd, intensity, sizeScale / 4f);
            else
                ExecutePC(cmd, intensity, sizeScale);
            return new Output(m_sizes[0]);
        }

        private void ExecutePC(CommandBuffer cmd, float intensity, float sizeScale)
        {
            for (int i = 1; i < PCStageCount; ++i)
            {
                BindSource(cmd, s_down[i - 1], m_sizes[i - 1]);
                Draw(cmd, s_down[i], m_sizes[i], 1);
            }
            for (int i = PCStageCount - 1; i >= 0; --i)
            {
                // UE GetBlurRadius: width * sizePercent / 100 / 2.
                float radius = m_sizes[i].x * StageSizes[i] * sizeScale * 0.005f;
                int sampleCount = BuildGaussianKernel(radius);
                cmd.SetGlobalVectorArray(KernelID, m_kernel);
                cmd.SetGlobalInt(KernelCountID, sampleCount);
                BindSource(cmd, s_down[i], m_sizes[i]);
                cmd.SetGlobalVector(FilterAxisID, new Vector4(1f, 0f, 0f, 0f));
                Draw(cmd, s_scratch, m_sizes[i], 2);
                cmd.ReleaseTemporaryRT(s_down[i]);
                BindSource(cmd, s_scratch, m_sizes[i]);
                bool hasLow = i + 1 < PCStageCount;
                if (hasLow)
                    BindLow(cmd, s_up[i + 1], m_sizes[i + 1]);
                else
                    BindLow(cmd, Texture2D.blackTexture, Vector2Int.one);
                cmd.SetGlobalVector(FilterAxisID, new Vector4(0f, 1f, 0f, 0f));
                cmd.SetGlobalVector(ParametersID, new Vector4(0f, StageTints[i] * intensity / PCStageCount, 1f, 0f));
                Draw(cmd, s_up[i], m_sizes[i], 3);
                cmd.ReleaseTemporaryRT(s_scratch);
                if (hasLow) cmd.ReleaseTemporaryRT(s_up[i + 1]);
            }
        }

        private void ExecuteMobile(CommandBuffer cmd, float intensity, float sizeScale)
        {
            // UE 5.8.4 AddPostProcessingPasses: Down[0] is setup. Each of the
            // six downsamples feeds the radial upsample chain before SunMerge.
            cmd.SetGlobalVector(ParametersID, new Vector4(2.64f * sizeScale, 0f, 0f, 0f));
            for (int i = 1; i < MobileStageCount; ++i)
            {
                BindSource(cmd, s_down[i - 1], m_sizes[i - 1]);
                Draw(cmd, s_down[i], m_sizes[i], 5);
            }
            for (int i = MobileStageCount - 2; i >= 1; --i)
            {
                bool first = i == MobileStageCount - 2;
                float tintA = (i == 1 ? StageTints[1] * 0.5f : StageTints[i - 1]) * intensity;
                float tintB = first ? StageTints[i] * intensity : 1f;
                int low = first ? s_down[i + 1] : s_up[i + 1];
                BindSource(cmd, s_down[i], m_sizes[i]);
                BindLow(cmd, low, m_sizes[i + 1]);
                cmd.SetGlobalVector(ParametersID, new Vector4(1.32f * sizeScale, tintA, tintB, 0f));
                Draw(cmd, s_up[i], m_sizes[i], 6);
                cmd.ReleaseTemporaryRT(s_down[i]);
                cmd.ReleaseTemporaryRT(low);
            }
            BindSource(cmd, s_down[0], m_sizes[0]);
            BindLow(cmd, s_up[1], m_sizes[1]);
            cmd.SetGlobalVector(ParametersID, new Vector4(0.33f * sizeScale, StageTints[0] * intensity * 0.5f, 1f / 5f, 0f));
            Draw(cmd, s_up[0], m_sizes[0], 7);
            cmd.ReleaseTemporaryRT(s_down[0]);
            cmd.ReleaseTemporaryRT(s_up[1]);
        }

        private int BuildGaussianKernel(float radius)
        {
            // UE Compute1DGaussianFilterKernel / NormalDistributionUnscaled:
            // pair adjacent taps into one bilinear fetch, then normalize.
            float sigma = Mathf.Clamp(radius, 0.0001f, KernelSampleLimit - 1f);
            int integerRadius = Mathf.Clamp(Mathf.CeilToInt(sigma), 1, KernelSampleLimit - 1);
            int count = 0;
            float sum = 0f;
            for (int offset = -integerRadius; offset <= integerRadius; offset += 2)
            {
                float weight0 = Mathf.Exp(-16.7f * (offset / sigma) * (offset / sigma));
                float weight1 = offset != integerRadius
                    ? Mathf.Exp(-16.7f * ((offset + 1) / sigma) * ((offset + 1) / sigma)) : 0f;
                float weight = weight0 + weight1;
                float packedOffset = offset + (weight > 0f ? weight1 / weight : 0f);
                m_kernel[count++] = new Vector4(packedOffset, weight, 0f, 0f);
                sum += weight;
            }
            for (int i = 0; i < count; ++i) m_kernel[i].y /= Mathf.Max(sum, 1e-20f);
            return count;
        }

        private static void BindSource(CommandBuffer cmd, RenderTargetIdentifier source, Vector2Int size)
        {
            cmd.SetGlobalTexture(SourceID, source);
            cmd.SetGlobalVector(SourceTexelID, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
        }

        private static void BindLow(CommandBuffer cmd, RenderTargetIdentifier source, Vector2Int size)
        {
            cmd.SetGlobalTexture(LowID, source);
            cmd.SetGlobalVector(LowTexelID, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
        }

        private void Draw(CommandBuffer cmd, int target, Vector2Int size, int pass)
        {
            cmd.GetTemporaryRT(target, size.x, size.y, 0, FilterMode.Bilinear, m_bloomFormat, RenderTextureReadWrite.Linear);
            cmd.SetRenderTarget(target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewport(new Rect(0f, 0f, size.x, size.y));
            cmd.DrawProcedural(Matrix4x4.identity, m_material, pass, MeshTopology.Triangles, 3);
        }

        private static int[] CreateTargetIds(string prefix, int count)
        {
            var ids = new int[count];
            for (int i = 0; i < count; ++i)
                ids[i] = Shader.PropertyToID(prefix + i);
            return ids;
        }

        public void Dispose() => MaxRenderResources.Destroy(ref m_material);
    }
}
