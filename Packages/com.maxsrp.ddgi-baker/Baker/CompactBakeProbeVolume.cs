// This software contains source code provided by NVIDIA Corporation.
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using SdkProbeVolume = MaxSRP.DDGI.ProbeVolume;

namespace MaxSRP.DDGIBaker.Editor
{
    // Host for the SDK kernels using X/Y texture planes and half-float lighting atlases.
    internal sealed class CompactBakeProbeVolume : IDisposable
    {
        private const int IrradianceTileSize = SdkProbeVolume.IrradianceTileSize;
        private const int DistanceTileSize = SdkProbeVolume.DistanceTileSize;

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

        private readonly SdkProbeVolume.Description description;
        private readonly ComputeShader update;
        private readonly int blendIrradiance, blendDistance, relocate, classify;
        private readonly PackedVolume[] volumeUpload = new PackedVolume[1];
        private readonly ComputeBuffer volume, root;
        private readonly RenderTexture rayData, variability;

        internal RenderTexture ProbeData { get; }
        internal RenderTexture Irradiance { get; }
        internal RenderTexture Distance { get; }

        internal CompactBakeProbeVolume(ComputeShader update, SdkProbeVolume.Description description)
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
                rayData = CreateAtlas("DDGI RayData", description.Rays, n.x * n.y, n.z);
                ProbeData = CreateAtlas("DDGI ProbeData", n.x, n.y, n.z);
                Irradiance = CreateAtlas("DDGI Irradiance", n.x * IrradianceTileSize, n.y * IrradianceTileSize, n.z, GraphicsFormat.R16G16B16A16_SFloat);
                Distance = CreateAtlas("DDGI Distance", n.x * DistanceTileSize, n.y * DistanceTileSize, n.z, GraphicsFormat.R16G16_SFloat);
                variability = CreateAtlas("DDGI Variability", Irradiance.width, Irradiance.height, n.z, GraphicsFormat.R32_SFloat);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void UploadConstants(CommandBuffer commands, Vector4 rayRotation, float hysteresis)
        {
            Vector3Int n = description.Counts;
            const uint f32x4 = 6;
            const uint f16x4 = 3;
            uint formatsAndFlags = (f32x4 << 17) | (f16x4 << 20);
            if (description.Relocation)
                formatsAndFlags |= 1u << 23;
            if (description.Classification)
                formatsAndFlags |= 1u << 24;
            volumeUpload[0] = new PackedVolume
            {
                origin = description.Origin,
                hysteresis = hysteresis,
                rotation = SdkProbeVolume.IdentityRotation,
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

        internal void BindRayTracingResources(CommandBuffer commands, RayTracingShader trace)
        {
            commands.SetRayTracingBufferParam(trace, VolumeId, volume);
            commands.SetRayTracingTextureParam(trace, RayDataId, AllSlices(rayData));
            commands.SetRayTracingTextureParam(trace, ProbeDataId, AllSlices(ProbeData));
            commands.SetRayTracingTextureParam(trace, "ProbeIrradiance", AllSlices(Irradiance));
            commands.SetRayTracingTextureParam(trace, "ProbeDistance", AllSlices(Distance));
        }

        internal void ClearProbeData(CommandBuffer commands) => ClearAtlas(commands, ProbeData);

        internal void ClearLighting(CommandBuffer commands)
        {
            ClearAtlas(commands, Irradiance);
            ClearAtlas(commands, Distance);
        }

        internal void Blend(CommandBuffer commands)
        {
            DispatchBlend(commands, blendIrradiance, Irradiance);
            DispatchBlend(commands, blendDistance, Distance);
        }

        internal void Relocate(CommandBuffer commands) => DispatchLayout(commands, relocate);
        internal void Classify(CommandBuffer commands) => DispatchLayout(commands, classify);

        private void DispatchBlend(CommandBuffer commands, int kernel, RenderTexture output)
        {
            BindUpdateResources(commands, kernel);
            commands.SetComputeTextureParam(update, kernel, OutputId, AllSlices(output));
            if (kernel == blendIrradiance)
                commands.SetComputeTextureParam(update, kernel, VariabilityId, AllSlices(variability));
            Vector3Int n = description.Counts;
            commands.DispatchCompute(update, kernel, n.x, n.y, n.z);
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
