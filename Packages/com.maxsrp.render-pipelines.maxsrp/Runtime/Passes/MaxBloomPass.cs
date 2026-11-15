using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // UE 5.8 standard bloom: six weighted, separable Gaussian scales on PC;
    // the mobile Q1 path uses a quarter-resolution setup and four radial scales.
    // All textures remain in MaxSRP's fixed scene-HDR scale. Exposure is used
    // only to evaluate the threshold; the final output applies exposure once.
    public sealed class MaxBloomPass : IDisposable
    {
        private const int PCStageCount = 6;
        private const int MobileStageCount = 5; // setup + four downsample stages
        private const int KernelSampleLimit = 32;
        private static readonly float[] StageSizes = { 0.3f, 1f, 2f, 10f, 30f, 64f };
        private static readonly float[] StageTints = { 0.3465f, 0.138f, 0.1176f, 0.066f, 0.066f, 0.061f };
        private static readonly int SourceID = Shader.PropertyToID("_MaxBloomSource");
        private static readonly int LowID = Shader.PropertyToID("_MaxBloomLow");
        private static readonly int SourceTexelID = Shader.PropertyToID("_MaxBloomSourceTexel");
        private static readonly int LowTexelID = Shader.PropertyToID("_MaxBloomLowTexel");
        private static readonly int SourceRectID = Shader.PropertyToID("_MaxBloomSourceRect");
        private static readonly int ParametersID = Shader.PropertyToID("_MaxBloomParameters");
        private static readonly int ExposureID = Shader.PropertyToID("_MaxBloomExposure");
        private static readonly int ExposureParametersID = Shader.PropertyToID("_MaxBloomExposureParameters");
        private static readonly int KernelID = Shader.PropertyToID("_MaxBloomKernel");
        private static readonly int KernelCountID = Shader.PropertyToID("_MaxBloomKernelCount");
        private static readonly int FilterAxisID = Shader.PropertyToID("_MaxBloomFilterAxis");
        private readonly Dictionary<int, CameraResources> m_resources = new Dictionary<int, CameraResources>();
        private readonly List<int> m_dead = new List<int>();
        private readonly Vector4[] m_kernel = new Vector4[KernelSampleLimit];
        private Material m_material;

        private sealed class CameraResources : IDisposable
        {
            internal readonly Camera Camera;
            internal readonly RenderTexture[] Down = new RenderTexture[PCStageCount];
            internal readonly RenderTexture[] Scratch = new RenderTexture[PCStageCount];
            internal readonly RenderTexture[] Up = new RenderTexture[PCStageCount];
            internal int Width, Height;
            internal PostProcessPlatform Platform;
            internal CameraResources(Camera camera) { Camera = camera; }

            internal void Ensure(int width, int height, PostProcessPlatform platform)
            {
                int count = platform == PostProcessPlatform.Mobile ? MobileStageCount : PCStageCount;
                bool valid = Width == width && Height == height && Platform == platform;
                for (int i = 0; valid && i < count; ++i)
                {
                    valid = Down[i] != null && Down[i].IsCreated();
                    if (platform == PostProcessPlatform.PC)
                        valid &= Scratch[i] != null && Scratch[i].IsCreated() && Up[i] != null && Up[i].IsCreated();
                    else if (i < count - 1)
                        valid &= Up[i] != null && Up[i].IsCreated();
                }
                if (valid) return;
                Dispose();
                Width = width; Height = height; Platform = platform;
                int divisor = platform == PostProcessPlatform.Mobile ? 4 : 2;
                int levelWidth = Mathf.Max(1, (width + divisor - 1) / divisor);
                int levelHeight = Mathf.Max(1, (height + divisor - 1) / divisor);
                try
                {
                    for (int i = 0; i < count; ++i)
                    {
                        Down[i] = Create(levelWidth, levelHeight, "Bloom Down " + i);
                        if (platform == PostProcessPlatform.PC)
                            Scratch[i] = Create(levelWidth, levelHeight, "Bloom Gaussian X " + i);
                        if (platform == PostProcessPlatform.PC || i < count - 1)
                            Up[i] = Create(levelWidth, levelHeight, "Bloom Up " + i);
                        levelWidth = Mathf.Max(1, (levelWidth + 1) / 2);
                        levelHeight = Mathf.Max(1, (levelHeight + 1) / 2);
                    }
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                for (int i = 0; i < PCStageCount; ++i)
                {
                    MaxRenderResources.Destroy(ref Down[i]);
                    MaxRenderResources.Destroy(ref Scratch[i]);
                    MaxRenderResources.Destroy(ref Up[i]);
                }
                Width = Height = 0;
            }
        }

        public MaxBloomPass(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires the serialized Hidden/MaxSRP/PostProcessBloom shader.");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public Texture Execute(ScriptableRenderContext context, Camera camera, RenderTexture sceneHDR,
            Rect viewport, Texture exposure, float manualExposure, BloomSettings settings, PostProcessPlatform platform)
        {
            CommandBuffer cmd = CommandBufferPool.Get(platform == PostProcessPlatform.Mobile ? "Bloom Mobile" : "Bloom PC");
            try
            {
                Texture result = Record(cmd, camera, sceneHDR, viewport, exposure, manualExposure, settings, platform);
                context.ExecuteCommandBuffer(cmd);
                return result;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        // The same recording path is used by production rendering and GPU validation.
        public Texture Record(CommandBuffer cmd, Camera camera, RenderTexture sceneHDR,
            Rect viewport, Texture exposure, float manualExposure, BloomSettings settings, PostProcessPlatform platform)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (camera == null) return Texture2D.blackTexture;
            int id = camera.GetInstanceID();
            if (settings == null || !settings.Enabled || !(settings.Intensity > 0f) ||
                sceneHDR == null || viewport.width < 1f || viewport.height < 1f ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                ReleaseCamera(id);
                return Texture2D.blackTexture;
            }
            if (!m_resources.TryGetValue(id, out CameraResources resources))
                m_resources.Add(id, resources = new CameraResources(camera));
            resources.Ensure(Mathf.Max(1, Mathf.RoundToInt(viewport.width)),
                Mathf.Max(1, Mathf.RoundToInt(viewport.height)), platform);
            float intensity = Mathf.Clamp(settings.Intensity, 0f, 64f);
            float sizeScale = float.IsNaN(settings.SizeScale) || float.IsInfinity(settings.SizeScale)
                ? 4f : Mathf.Clamp(settings.SizeScale, 0.1f, 8f);
            float threshold = float.IsNaN(settings.Threshold) ? -1f : Mathf.Max(-1f, settings.Threshold);
            cmd.SetGlobalTexture(ExposureID, exposure != null ? exposure : Texture2D.whiteTexture);
            cmd.SetGlobalVector(ExposureParametersID, new Vector4(manualExposure, exposure != null ? 1f : 0f, 0f, 0f));
            BindSource(cmd, sceneHDR);
            cmd.SetGlobalVector(SourceRectID, new Vector4(viewport.width / sceneHDR.width, viewport.height / sceneHDR.height,
                viewport.x / sceneHDR.width, viewport.y / sceneHDR.height));
            cmd.SetGlobalVector(ParametersID, new Vector4(threshold, 0f, 0f, 0f));
            Draw(cmd, resources.Down[0], platform == PostProcessPlatform.Mobile ? 4 : 0);
            cmd.SetGlobalVector(SourceRectID, new Vector4(1f, 1f, 0f, 0f));
            if (platform == PostProcessPlatform.Mobile)
                ExecuteMobile(cmd, resources, intensity, sizeScale / 4f);
            else
                ExecutePC(cmd, resources, intensity, sizeScale);
            return resources.Up[0];
        }

        private void ExecutePC(CommandBuffer cmd, CameraResources resources, float intensity, float sizeScale)
        {
            for (int i = 1; i < PCStageCount; ++i)
            {
                BindSource(cmd, resources.Down[i - 1]);
                Draw(cmd, resources.Down[i], 1);
            }
            for (int i = PCStageCount - 1; i >= 0; --i)
            {
                // UE GetBlurRadius: width * sizePercent / 100 / 2.
                float radius = resources.Down[i].width * StageSizes[i] * sizeScale * 0.005f;
                int sampleCount = BuildGaussianKernel(radius);
                cmd.SetGlobalVectorArray(KernelID, m_kernel);
                cmd.SetGlobalInt(KernelCountID, sampleCount);
                BindSource(cmd, resources.Down[i]);
                cmd.SetGlobalVector(FilterAxisID, new Vector4(1f, 0f, 0f, 0f));
                Draw(cmd, resources.Scratch[i], 2);
                BindSource(cmd, resources.Scratch[i]);
                BindLow(cmd, i + 1 < PCStageCount ? (Texture)resources.Up[i + 1] : Texture2D.blackTexture);
                cmd.SetGlobalVector(FilterAxisID, new Vector4(0f, 1f, 0f, 0f));
                cmd.SetGlobalVector(ParametersID, new Vector4(0f, StageTints[i] * intensity / PCStageCount, 1f, 0f));
                Draw(cmd, resources.Up[i], 3);
            }
        }

        private void ExecuteMobile(CommandBuffer cmd, CameraResources resources, float intensity, float sizeScale)
        {
            // UE mobile Q1: quarter-resolution setup, four 2x radial downsamples,
            // three radial upsample/combine passes, then the finest bloom merge.
            cmd.SetGlobalVector(ParametersID, new Vector4(2.64f * sizeScale, 0f, 0f, 0f));
            for (int i = 1; i < MobileStageCount; ++i)
            {
                BindSource(cmd, resources.Down[i - 1]);
                Draw(cmd, resources.Down[i], 5);
            }
            for (int i = MobileStageCount - 2; i >= 1; --i)
            {
                bool first = i == MobileStageCount - 2;
                float tintA = (i == 1 ? StageTints[1] * 0.5f : StageTints[i - 1]) * intensity;
                float tintB = first ? StageTints[i] * intensity : 1f;
                BindSource(cmd, resources.Down[i]);
                BindLow(cmd, first ? resources.Down[i + 1] : resources.Up[i + 1]);
                cmd.SetGlobalVector(ParametersID, new Vector4(1.32f * sizeScale, tintA, tintB, 0f));
                Draw(cmd, resources.Up[i], 6);
            }
            BindSource(cmd, resources.Down[0]);
            BindLow(cmd, resources.Up[1]);
            cmd.SetGlobalVector(ParametersID, new Vector4(0.33f * sizeScale, StageTints[0] * intensity * 0.5f, 1f / 5f, 0f));
            Draw(cmd, resources.Up[0], 7);
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

        private static void BindSource(CommandBuffer cmd, Texture source)
        {
            cmd.SetGlobalTexture(SourceID, source);
            cmd.SetGlobalVector(SourceTexelID, new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
        }

        private static void BindLow(CommandBuffer cmd, Texture source)
        {
            cmd.SetGlobalTexture(LowID, source);
            cmd.SetGlobalVector(LowTexelID, new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
        }

        private void Draw(CommandBuffer cmd, RenderTexture target, int pass)
        {
            cmd.SetRenderTarget(target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewport(new Rect(0f, 0f, target.width, target.height));
            cmd.DrawProcedural(Matrix4x4.identity, m_material, pass, MeshTopology.Triangles, 3);
        }

        private static RenderTexture Create(int width, int height, string label)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "MaxSRP " + label,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1
            };
            if (target.Create()) return target;
            MaxRenderResources.Destroy(ref target);
            throw new InvalidOperationException("Could not allocate MaxSRP bloom texture.");
        }

        public void ReleaseCamera(Camera camera) { if (camera != null) ReleaseCamera(camera.GetInstanceID()); }
        private void ReleaseCamera(int id)
        {
            if (!m_resources.TryGetValue(id, out CameraResources resources)) return;
            resources.Dispose();
            m_resources.Remove(id);
        }

        public void PruneDestroyedCameras()
        {
            m_dead.Clear();
            foreach (var entry in m_resources) if (entry.Value.Camera == null) m_dead.Add(entry.Key);
            foreach (int id in m_dead) ReleaseCamera(id);
        }

        public void Dispose()
        {
            foreach (var entry in m_resources) entry.Value.Dispose();
            m_resources.Clear();
            MaxRenderResources.Destroy(ref m_material);
        }
    }
}
