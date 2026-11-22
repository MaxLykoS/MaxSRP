using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class SkyIBLSettings
    {
        [Min(1.0f), Tooltip("Global sky environment height above sea level, in world units.")]
        public float ReferenceHeight = 2.0f;
        [Range(32, 256)] public int SpecularResolution = 128;
        [Range(32, 512)] public int SpecularSamples = 128;
    }

    public sealed class MaxIBLGIPass : MaxRenderPass
    {
        private const int DiffuseSHVectorCount = 8;
        // UE 5.8.4 CL 58902052, SkyPassRendering.cpp: GetCaptureFrameSkyEnvMapTextureDesc.
        private const GraphicsFormat SkyFormat = GraphicsFormat.B10G11R11_UFloatPack32;
        private const int BRDFWidth = 128;
        private const int BRDFHeight = 32;
        private static readonly int s_brdfStagingId = Shader.PropertyToID("_MaxIBLBRDFStaging");
        private readonly GraphicsFormat m_brdfFormat;
        private readonly ComputeShader m_cs;
        private readonly int m_skyKernel, m_diffuseKernel, m_specularKernel, m_brdfKernel;
        private RenderTexture m_stage, m_environment, m_brdfLUT;
        private readonly OutputSet[] m_outputs = { new OutputSet(), new OutputSet() };
        private int m_resolution, m_mipCount, m_active, m_environmentRevision = -1;
        private int m_specularSamples;
        private Texture m_source;
        private bool m_brdfValid;
#if UNITY_EDITOR
        private Hash128 m_computeDependencyHash;
#endif

        private sealed class OutputSet : IDisposable
        {
            public ComputeBuffer DiffuseSH;
            public RenderTexture Specular;
            public bool IsCreated => DiffuseSH != null && DiffuseSH.IsValid() && Specular != null && Specular.IsCreated();
            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_IBLSpec", Specular);
                DiffuseSH?.Release();
                DiffuseSH = null;
                MaxRenderResources.Destroy(ref Specular);
            }
        }

        public MaxIBLGIPass(ComputeShader shader)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures ||
                (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) == 0)
                throw new NotSupportedException("Dynamic sky IBL requires compute shaders, texture arrays and GPU copies between texture types.");
            // UE 5.8.4 CL 58902052, SystemTextures.cpp: PreintegratedGF.
            m_brdfFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_UNorm, FormatUsage.Sample) &&
                SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_UNorm, FormatUsage.Linear)
                ? GraphicsFormat.R16G16_UNorm : GraphicsFormat.R8G8_UNorm;
            m_cs = shader;
            m_skyKernel = shader.FindKernel("SkyToEnvironment");
            m_diffuseKernel = shader.FindKernel("ComputeSkyDiffuseSH");
            m_specularKernel = shader.FindKernel("FilterSkySpecular");
            m_brdfKernel = shader.FindKernel("BuildDynamicBRDF");
#if UNITY_EDITOR
            m_computeDependencyHash = UnityEditor.AssetDatabase.GetAssetDependencyHash(UnityEditor.AssetDatabase.GetAssetPath(m_cs));
            UnityEditor.EditorApplication.projectChanged += OnProjectChanged;
#endif
        }

#if UNITY_EDITOR
        private void OnProjectChanged()
        {
            var hash = UnityEditor.AssetDatabase.GetAssetDependencyHash(UnityEditor.AssetDatabase.GetAssetPath(m_cs));
            if (hash.Equals(m_computeDependencyHash)) return;
            m_computeDependencyHash = hash;
            m_environmentRevision = -1;
            m_brdfValid = false;
        }
#endif

        private static RenderTexture CreateTarget(int width, int height, GraphicsFormat format, string name,
            TextureDimension dimension = TextureDimension.Tex2D, bool mipChain = false, bool randomWrite = false)
        {
            var descriptor = new RenderTextureDescriptor(width, height, format, 0)
            {
                dimension = dimension, volumeDepth = dimension == TextureDimension.Tex2DArray ? 6 : 1,
                useMipMap = mipChain, autoGenerateMips = false, enableRandomWrite = randomWrite
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name, filterMode = mipChain ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create() || texture.graphicsFormat != format)
            {
                MaxRenderResources.Destroy(ref texture);
                throw new InvalidOperationException($"Cannot allocate {name} as {format}.");
            }
            return texture;
        }

        private void EnsureTargets(int resolution)
        {
            if (resolution != m_resolution || m_stage == null || !m_stage.IsCreated() ||
                m_environment == null || !m_environment.IsCreated() ||
                !m_outputs[0].IsCreated || !m_outputs[1].IsCreated)
            {
                ReleaseEnvironmentTargets();
                try
                {
                    m_resolution = resolution;
                    m_mipCount = 1;
                    for (int width = resolution; width > 1; width >>= 1) ++m_mipCount;
                    m_stage = CreateTarget(resolution, resolution, SkyFormat, "IBL Compute Staging",
                        TextureDimension.Tex2DArray, randomWrite: true);
                    m_environment = CreateTarget(resolution, resolution, SkyFormat, "Sky Radiance HDR",
                        TextureDimension.Cube, mipChain: true);
                    for (int i = 0; i < m_outputs.Length; ++i)
                    {
                        m_outputs[i].DiffuseSH = new ComputeBuffer(DiffuseSHVectorCount, 4 * sizeof(float)) { name = "Sky Diffuse SH " + i };
                        m_outputs[i].Specular = CreateTarget(resolution, resolution, SkyFormat, "Sky Specular HDR " + i,
                            TextureDimension.Cube, mipChain: true);
                    }
                }
                catch
                {
                    ReleaseEnvironmentTargets();
                    throw;
                }
            }
            if (m_brdfLUT == null || !m_brdfLUT.IsCreated())
            {
                MaxRenderResources.UnbindTexture("_BRDFLUT", m_brdfLUT);
                MaxRenderResources.Destroy(ref m_brdfLUT);
                m_brdfLUT = CreateTarget(BRDFWidth, BRDFHeight, m_brdfFormat, "IBL BRDF LUT");
                m_brdfValid = false;
            }
        }

        private void DispatchFaces(CommandBuffer cmd, int kernel, int width, RenderTexture destination, int mip)
        {
            cmd.SetComputeIntParam(m_cs, "_DynamicWidth", width);
            cmd.SetComputeTextureParam(m_cs, kernel, "_DynamicOutput", m_stage);
            cmd.DispatchCompute(m_cs, kernel, (width + 7) / 8, (width + 7) / 8, 6);
            // Reuse one array UAV; copy each completed face into its cube mip on GPU.
            for (int face = 0; face < 6; ++face)
                cmd.CopyTexture(m_stage, face, 0, 0, 0, width, width, destination, face, mip, 0, 0);
        }

        public void SetLightingBindings(MaterialPropertyBlock properties)
        {
            // The actual deferred draw owns its IBL inputs. Editor utilities and
            // legacy probes can also write the same global property names.
            properties.SetBuffer("_SkyDiffuseSH", m_outputs[m_active].DiffuseSH);
            properties.SetTexture("_IBLSpec", m_outputs[m_active].Specular);
            properties.SetTexture("_BRDFLUT", m_brdfLUT);
            properties.SetInteger("_MaxMipCount", m_mipCount);
        }

        public void Execute(ScriptableRenderContext context, Texture environmentSky, int environmentRevision, SkyIBLSettings settings,
            float planetRadius, float environmentViewHeight, Vector4 skyViewSizeAndInvSize)
        {
            if (environmentSky == null) throw new ArgumentNullException(nameof(environmentSky));
            int resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(settings.SpecularResolution, 32, 256));
            int specularSamples = Mathf.Clamp(settings.SpecularSamples, 32, 512);
            EnsureTargets(resolution);
            bool update = m_environmentRevision != environmentRevision || m_source != environmentSky ||
                specularSamples != m_specularSamples;
            int next = update ? 1 - m_active : m_active;
            using var commands = new PooledCommandBuffer("Dynamic Sky IBL");
            CommandBuffer cmd = commands.Buffer;
            try
            {
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                if (!m_brdfValid)
                {
                    cmd.BeginSample("IBL BRDF LUT");
                    // Convert to the filterable UNORM target independently of typed UAV support.
                    var staging = new RenderTextureDescriptor(BRDFWidth, BRDFHeight, GraphicsFormat.R32G32B32A32_SFloat, 0)
                    { enableRandomWrite = true };
                    cmd.GetTemporaryRT(s_brdfStagingId, staging, FilterMode.Point);
                    cmd.SetComputeIntParam(m_cs, "_DynamicWidth", BRDFWidth);
                    cmd.SetComputeIntParam(m_cs, "_DynamicBRDFHeight", BRDFHeight);
                    cmd.SetComputeTextureParam(m_cs, m_brdfKernel, "_DynamicBRDFLUT", new RenderTargetIdentifier(s_brdfStagingId));
                    cmd.DispatchCompute(m_cs, m_brdfKernel, BRDFWidth / 8, BRDFHeight / 8, 1);
                    cmd.Blit(s_brdfStagingId, m_brdfLUT);
                    cmd.ReleaseTemporaryRT(s_brdfStagingId);
                    cmd.EndSample("IBL BRDF LUT");
                }
                if (update)
                {
                    cmd.BeginSample("Sky Environment");
                    cmd.SetComputeFloatParam(m_cs, "_PlanetRadius", planetRadius);
                    cmd.SetComputeFloatParam(m_cs, "_AtmosphereEnvironmentSkyViewHeight", environmentViewHeight);
                    cmd.SetComputeVectorParam(m_cs, "_AtmosphereSkyViewSizeAndInvSize", skyViewSizeAndInvSize);
                    cmd.SetComputeTextureParam(m_cs, m_skyKernel, "_EnvironmentSky", environmentSky);
                    DispatchFaces(cmd, m_skyKernel, resolution, m_environment, 0);
                    cmd.GenerateMips(m_environment);
                    cmd.EndSample("Sky Environment");

                    cmd.SetComputeFloatParam(m_cs, "_DynamicSourceResolution", resolution);
                    cmd.SetComputeFloatParam(m_cs, "_DynamicSourceMaxMip", m_mipCount - 1);
                    cmd.BeginSample("Sky GGX Prefilter");
                    // Roughness zero is a direct copy, with no Monte Carlo integration.
                    for (int face = 0; face < 6; ++face)
                        cmd.CopyTexture(m_environment, face, 0, m_outputs[next].Specular, face, 0);
                    cmd.SetComputeIntParam(m_cs, "_DynamicSampleCount", specularSamples);
                    cmd.SetComputeTextureParam(m_cs, m_specularKernel, "_DynamicRadianceMap", m_environment);
                    for (int mip = 1; mip < m_mipCount; ++mip)
                    {
                        cmd.SetComputeFloatParam(m_cs, "_DynamicRoughness", (float)mip / (m_mipCount - 1));
                        DispatchFaces(cmd, m_specularKernel, resolution >> mip, m_outputs[next].Specular, mip);
                    }
                    cmd.EndSample("Sky GGX Prefilter");

                    // UE 5.8.4 CL 58902052, ReflectionEnvironmentRealTimeCapture.cpp:
                    // RenderCubeFaces_DiffuseIrradiance reads the convolved sky at its 16x16 face mip.
                    cmd.BeginSample("Sky Diffuse Irradiance SH");
                    cmd.SetComputeIntParam(m_cs, "_SkyDiffuseSourceMip", m_mipCount - 1 - 4);
                    cmd.SetComputeTextureParam(m_cs, m_diffuseKernel, "_SkyDiffuseSource", m_outputs[next].Specular);
                    cmd.SetComputeBufferParam(m_cs, m_diffuseKernel, "_SkyDiffuseSHOutput", m_outputs[next].DiffuseSH);
                    cmd.DispatchCompute(m_cs, m_diffuseKernel, 1, 1, 1);
                    cmd.EndSample("Sky Diffuse Irradiance SH");
                }
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                // Publish diffuse and specular together after the complete update;
                // rebind on cache hits too, since a legacy utility may set globals.
                cmd.SetGlobalTexture("_IBLSpec", m_outputs[next].Specular);
                cmd.SetGlobalTexture("_BRDFLUT", m_brdfLUT);
                cmd.SetGlobalInteger("_MaxMipCount", m_mipCount);
                context.ExecuteCommandBuffer(cmd);
                m_active = next;
                m_brdfValid = true;
                m_environmentRevision = environmentRevision;
                m_source = environmentSky;
                m_specularSamples = specularSamples;
            }
            catch
            {
                m_environmentRevision = -1;
                m_brdfValid = false;
                throw;
            }
        }

        private void ReleaseEnvironmentTargets()
        {
            foreach (OutputSet output in m_outputs) output.Dispose();
            MaxRenderResources.Destroy(ref m_stage);
            MaxRenderResources.Destroy(ref m_environment);
            m_environmentRevision = -1;
            m_source = null;
            m_active = 0;
        }

        protected override void ReleaseResources()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged -= OnProjectChanged;
#endif
            ReleaseEnvironmentTargets();
            MaxRenderResources.UnbindTexture("_BRDFLUT", m_brdfLUT);
            MaxRenderResources.Destroy(ref m_brdfLUT);
            m_brdfValid = false;
        }
    }
}
