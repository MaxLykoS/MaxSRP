using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class SkyIBLSettings
    {
        [Min(1.0f), Tooltip("Global sky environment height above sea level, in world units.")]
        public float ReferenceHeight = 2.0f;
        [Range(32, 256)] public int SpecularResolution = 128;
        [Range(32, 512)] public int DiffuseSamples = 128;
        [Range(32, 512)] public int SpecularSamples = 128;
    }

    public sealed class MaxIBLGIPass : IDisposable
    {
        private const int DiffuseResolution = 32;
        private const int BRDFResolution = 256;
        private readonly ComputeShader m_cs;
        private readonly int m_skyKernel, m_diffuseKernel, m_specularKernel, m_brdfKernel;
        private RenderTexture m_stage, m_environment, m_brdfLUT;
        private readonly OutputSet[] m_outputs = { new OutputSet(), new OutputSet() };
        private int m_resolution, m_mipCount, m_active, m_environmentRevision = -1;
        private int m_diffuseSamples, m_specularSamples;
        private Texture m_source;
        private bool m_brdfValid;
#if UNITY_EDITOR
        private Hash128 m_computeDependencyHash;
#endif

        private sealed class OutputSet : IDisposable
        {
            public RenderTexture Diffuse, Specular;
            public bool IsCreated => Diffuse != null && Diffuse.IsCreated() && Specular != null && Specular.IsCreated();
            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_IBLDiffuse", Diffuse);
                MaxRenderResources.UnbindTexture("_IBLSpec", Specular);
                MaxRenderResources.Destroy(ref Diffuse);
                MaxRenderResources.Destroy(ref Specular);
            }
        }

        public MaxIBLGIPass(ComputeShader shader)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures ||
                (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) == 0)
                throw new NotSupportedException("Dynamic sky IBL requires compute shaders, texture arrays and GPU copies between texture types.");
            m_cs = shader;
            m_skyKernel = shader.FindKernel("SkyToEnvironment");
            m_diffuseKernel = shader.FindKernel("FilterSkyDiffuse");
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

        private static RenderTexture CreateTarget(int size, string name, TextureDimension dimension,
            bool mipChain = false, bool randomWrite = false)
        {
            var texture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name, dimension = dimension, volumeDepth = dimension == TextureDimension.Tex2DArray ? 6 : 1,
                useMipMap = mipChain, autoGenerateMips = false, enableRandomWrite = randomWrite,
                filterMode = mipChain ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create())
            {
                MaxRenderResources.Destroy(ref texture);
                throw new InvalidOperationException("Cannot allocate " + name);
            }
            return texture;
        }

        private void EnsureTargets(int resolution)
        {
            if (resolution != m_resolution || m_stage == null || !m_stage.IsCreated() ||
                m_environment == null || !m_environment.IsCreated() || !m_outputs[0].IsCreated || !m_outputs[1].IsCreated)
            {
                ReleaseEnvironmentTargets();
                try
                {
                    m_resolution = resolution;
                    m_mipCount = 1;
                    for (int width = resolution; width > 1; width >>= 1) ++m_mipCount;
                    m_stage = CreateTarget(resolution, "IBL Compute Staging", TextureDimension.Tex2DArray, false, true);
                    m_environment = CreateTarget(resolution, "Sky Radiance HDR", TextureDimension.Cube, true);
                    for (int i = 0; i < m_outputs.Length; ++i)
                    {
                        m_outputs[i].Diffuse = CreateTarget(DiffuseResolution, "Sky Diffuse HDR " + i, TextureDimension.Cube);
                        m_outputs[i].Specular = CreateTarget(resolution, "Sky Specular HDR " + i, TextureDimension.Cube, true);
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
                m_brdfLUT = CreateTarget(BRDFResolution, "IBL BRDF LUT", TextureDimension.Tex2D, false, true);
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
            properties.SetTexture("_IBLDiffuse", m_outputs[m_active].Diffuse);
            properties.SetTexture("_IBLSpec", m_outputs[m_active].Specular);
            properties.SetTexture("_BRDFLUT", m_brdfLUT);
            properties.SetInteger("_MaxMipCount", m_mipCount);
        }

        public void Execute(ScriptableRenderContext context, Texture environmentSky, int environmentRevision, SkyIBLSettings settings)
        {
            if (environmentSky == null) throw new ArgumentNullException(nameof(environmentSky));
            int resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(settings.SpecularResolution, 32, 256));
            int diffuseSamples = Mathf.Clamp(settings.DiffuseSamples, 32, 512);
            int specularSamples = Mathf.Clamp(settings.SpecularSamples, 32, 512);
            EnsureTargets(resolution);
            bool update = m_environmentRevision != environmentRevision || m_source != environmentSky ||
                diffuseSamples != m_diffuseSamples || specularSamples != m_specularSamples;
            int next = update ? 1 - m_active : m_active;
            CommandBuffer cmd = CommandBufferPool.Get("Dynamic Sky IBL");
            try
            {
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                if (!m_brdfValid)
                {
                    cmd.BeginSample("IBL BRDF LUT");
                    cmd.SetComputeIntParam(m_cs, "_DynamicWidth", BRDFResolution);
                    cmd.SetComputeTextureParam(m_cs, m_brdfKernel, "_DynamicBRDFLUT", m_brdfLUT);
                    cmd.DispatchCompute(m_cs, m_brdfKernel, BRDFResolution / 8, BRDFResolution / 8, 1);
                    cmd.EndSample("IBL BRDF LUT");
                }
                if (update)
                {
                    cmd.BeginSample("Sky Environment");
                    cmd.SetComputeTextureParam(m_cs, m_skyKernel, "_EnvironmentSky", environmentSky);
                    DispatchFaces(cmd, m_skyKernel, resolution, m_environment, 0);
                    cmd.GenerateMips(m_environment);
                    cmd.EndSample("Sky Environment");

                    cmd.SetComputeFloatParam(m_cs, "_DynamicSourceResolution", resolution);
                    cmd.SetComputeFloatParam(m_cs, "_DynamicSourceMaxMip", m_mipCount - 1);
                    cmd.BeginSample("Sky Diffuse Convolution");
                    cmd.SetComputeIntParam(m_cs, "_DynamicSampleCount", diffuseSamples);
                    cmd.SetComputeTextureParam(m_cs, m_diffuseKernel, "_DynamicRadianceMap", m_environment);
                    DispatchFaces(cmd, m_diffuseKernel, DiffuseResolution, m_outputs[next].Diffuse, 0);
                    cmd.EndSample("Sky Diffuse Convolution");

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
                }
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                // Publish diffuse and specular together after the complete update;
                // rebind on cache hits too, since a legacy utility may set globals.
                cmd.SetGlobalTexture("_IBLDiffuse", m_outputs[next].Diffuse);
                cmd.SetGlobalTexture("_IBLSpec", m_outputs[next].Specular);
                cmd.SetGlobalTexture("_BRDFLUT", m_brdfLUT);
                cmd.SetGlobalInteger("_MaxMipCount", m_mipCount);
                context.ExecuteCommandBuffer(cmd);
                m_active = next;
                m_brdfValid = true;
                m_environmentRevision = environmentRevision;
                m_source = environmentSky;
                m_diffuseSamples = diffuseSamples;
                m_specularSamples = specularSamples;
            }
            catch
            {
                m_environmentRevision = -1;
                m_brdfValid = false;
                throw;
            }
            finally
            {
                CommandBufferPool.Release(cmd);
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

        public void Dispose()
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
