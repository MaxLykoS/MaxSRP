// This software contains source code provided by NVIDIA Corporation.
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxSRP.DDGI
{
    /// <summary>Unity host for the static, non-bindless RTXGI-DDGI SDK kernels.</summary>
    public sealed class ProbeVolume : IDisposable
    {
        public const int FixedRayCount = 32;
        public const int IrradianceTileSize = 8;
        public const int DistanceTileSize = 16;
        public static readonly Vector4 IdentityRotation = new Vector4(0f, 0f, 0f, 1f);

        public struct Description
        {
            public Vector3 Origin;
            public Vector3Int Counts;
            public Vector3 Spacing;
            public int Rays;
            public float MaxRayDistance, NormalBias, ViewBias, DistanceExponent;
            public float EncodingGamma, IrradianceThreshold, BrightnessThreshold, MinFrontfaceDistance;
            public float RandomBackfaceThreshold, FixedBackfaceThreshold;
            public bool Relocation, Classification;
        }

        private static readonly int VolumeId = Shader.PropertyToID("DDGIVolumes");
        private static readonly int RootId = Shader.PropertyToID("DDGI");
        private static readonly int RayDataId = Shader.PropertyToID("RayData");
        private static readonly int ProbeDataId = Shader.PropertyToID("ProbeData");
        private static readonly int OutputId = Shader.PropertyToID("Output");
        private static readonly int VariabilityId = Shader.PropertyToID("ProbeVariability");
        private const int VolumeStride = 128;
        private const int RootStride = 32;

        // Binary layout of NVIDIA DDGIVolumeDescGPUPacked.
        [StructLayout(LayoutKind.Sequential, Size = VolumeStride)]
        private struct PackedVolume
        {
            internal Vector3 origin;
            internal float hysteresis;
            internal Vector4 rotation;
            internal Vector4 rayRotation;
            internal float maxRayDistance, normalBias, viewBias, distanceExponent;
            internal float gamma, irradianceThreshold, brightnessThreshold, minFrontfaceDistance;
            internal Vector3 spacing;
            internal uint counts, backfaceThresholds, rayAndTileCounts, scrollXY, formatsAndFlags;
        }

        [StructLayout(LayoutKind.Sequential, Size = RootStride)]
        private struct RootConstants
        {
            internal Vector4 first, second;
        }

        private readonly Description description;
        private readonly ComputeShader update;
        private readonly int blendIrradiance, blendDistance, relocate, classify;
        private readonly PackedVolume[] volumeUpload = new PackedVolume[1];
        private readonly ComputeBuffer volume, root;
        private readonly RenderTexture rayData, variability;

        public RenderTexture ProbeData { get; }
        public RenderTexture Irradiance { get; }
        public RenderTexture Distance { get; }

        public ProbeVolume(ComputeShader update, Description description)
        {
            this.update = update;
            this.description = description;
            blendIrradiance = update.FindKernel("BlendIrradiance");
            blendDistance = update.FindKernel("BlendDistance");
            relocate = update.FindKernel("RelocateProbes");
            classify = update.FindKernel("ClassifyProbes");
            try
            {
                volume = new ComputeBuffer(1, VolumeStride, ComputeBufferType.Structured);
                root = new ComputeBuffer(1, RootStride, ComputeBufferType.Constant);
                root.SetData(new RootConstants[1]);
                Vector3Int n = description.Counts;
                rayData = CreateAtlas("DDGI RayData", description.Rays, n.x * n.z, n.y);
                ProbeData = CreateAtlas("DDGI ProbeData", n.x, n.z, n.y);
                Irradiance = CreateAtlas("DDGI Irradiance", n.x * IrradianceTileSize, n.z * IrradianceTileSize, n.y);
                Distance = CreateAtlas("DDGI Distance", n.x * DistanceTileSize, n.z * DistanceTileSize, n.y);
                variability = CreateAtlas("DDGI Variability", Irradiance.width, Irradiance.height, n.y, GraphicsFormat.R32_SFloat);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void UploadConstants(CommandBuffer commands, Vector4 rayRotation, float hysteresis)
        {
            Vector3Int n = description.Counts;
            const uint f32x4 = 6;
            uint formatsAndFlags = (f32x4 << 17) | (f32x4 << 20);
            if (description.Relocation)
                formatsAndFlags |= 1u << 23;
            if (description.Classification)
                formatsAndFlags |= 1u << 24;
            volumeUpload[0] = new PackedVolume
            {
                origin = description.Origin,
                hysteresis = hysteresis,
                rotation = IdentityRotation,
                rayRotation = rayRotation,
                maxRayDistance = description.MaxRayDistance,
                normalBias = description.NormalBias,
                viewBias = description.ViewBias,
                distanceExponent = description.DistanceExponent,
                gamma = description.EncodingGamma,
                irradianceThreshold = description.IrradianceThreshold,
                brightnessThreshold = description.BrightnessThreshold,
                minFrontfaceDistance = description.MinFrontfaceDistance,
                spacing = description.Spacing,
                counts = (uint)(n.x | (n.y << 10) | (n.z << 20)),
                backfaceThresholds = (uint)(description.RandomBackfaceThreshold * 65535) |
                    ((uint)(description.FixedBackfaceThreshold * 65535) << 16),
                rayAndTileCounts = (uint)(description.Rays | ((IrradianceTileSize - 2) << 16) | ((DistanceTileSize - 2) << 24)),
                formatsAndFlags = formatsAndFlags
            };
            commands.SetBufferData(volume, volumeUpload);
        }

        public void BindRayTracingResources(CommandBuffer commands, RayTracingShader trace)
        {
            commands.SetRayTracingBufferParam(trace, VolumeId, volume);
            commands.SetRayTracingTextureParam(trace, RayDataId, AllSlices(rayData));
            commands.SetRayTracingTextureParam(trace, ProbeDataId, AllSlices(ProbeData));
            commands.SetRayTracingTextureParam(trace, "ProbeIrradiance", AllSlices(Irradiance));
            commands.SetRayTracingTextureParam(trace, "ProbeDistance", AllSlices(Distance));
        }

        public void ClearProbeData(CommandBuffer commands) => ClearAtlas(commands, ProbeData);

        public void ClearLighting(CommandBuffer commands)
        {
            ClearAtlas(commands, Irradiance);
            ClearAtlas(commands, Distance);
        }

        public void Blend(CommandBuffer commands)
        {
            DispatchBlend(commands, blendIrradiance, Irradiance);
            DispatchBlend(commands, blendDistance, Distance);
        }

        public void Relocate(CommandBuffer commands) => DispatchLayout(commands, relocate);
        public void Classify(CommandBuffer commands) => DispatchLayout(commands, classify);

        private void DispatchBlend(CommandBuffer commands, int kernel, RenderTexture output)
        {
            BindUpdateResources(commands, kernel);
            commands.SetComputeTextureParam(update, kernel, OutputId, AllSlices(output));
            if (kernel == blendIrradiance)
                commands.SetComputeTextureParam(update, kernel, VariabilityId, AllSlices(variability));
            Vector3Int n = description.Counts;
            commands.DispatchCompute(update, kernel, n.x, n.z, n.y);
        }

        private void DispatchLayout(CommandBuffer commands, int kernel)
        {
            BindUpdateResources(commands, kernel);
            Vector3Int n = description.Counts;
            const int probesPerGroup = 32;
            commands.DispatchCompute(update, kernel, (n.x * n.y * n.z + probesPerGroup - 1) / probesPerGroup, 1, 1);
        }

        private void BindUpdateResources(CommandBuffer commands, int kernel)
        {
            commands.SetComputeConstantBufferParam(update, RootId, root, 0, RootStride);
            commands.SetComputeBufferParam(update, kernel, VolumeId, volume);
            commands.SetComputeTextureParam(update, kernel, RayDataId, AllSlices(rayData));
            commands.SetComputeTextureParam(update, kernel, ProbeDataId, AllSlices(ProbeData));
        }

        private static RenderTexture CreateAtlas(string name, int width, int height, int layers,
            GraphicsFormat format = GraphicsFormat.R32G32B32A32_SFloat)
        {
            var descriptor = new RenderTextureDescriptor(width, height, format, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = layers,
                enableRandomWrite = true,
                msaaSamples = 1
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (texture.Create())
                return texture;
            Object.DestroyImmediate(texture);
            throw new InvalidOperationException("Could not allocate " + name);
        }

        private static void ClearAtlas(CommandBuffer commands, RenderTexture atlas)
        {
            for (int slice = 0; slice < atlas.volumeDepth; slice++)
            {
                commands.SetRenderTarget(atlas, 0, CubemapFace.Unknown, slice);
                commands.ClearRenderTarget(false, true, Color.clear);
            }
        }

        private static RenderTargetIdentifier AllSlices(RenderTexture atlas) =>
            new RenderTargetIdentifier(atlas, 0, CubemapFace.Unknown, RenderTargetIdentifier.AllDepthSlices);

        public void Dispose()
        {
            volume?.Release();
            root?.Release();
            foreach (RenderTexture texture in new[] { rayData, ProbeData, Irradiance, Distance, variability })
                if (texture)
                    Object.DestroyImmediate(texture);
        }
    }
}
