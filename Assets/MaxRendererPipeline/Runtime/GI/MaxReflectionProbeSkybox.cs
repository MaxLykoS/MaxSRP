using UnityEngine;

namespace MaxSRP
{
    public class MaxReflectionProbeSkybox : MaxProbeBase
    {
        private Texture2D m_brdfLUT;
        private readonly ComputeShader m_cs;
        private readonly int m_specKernelID;
        private readonly int m_lutKernelID;

        public MaxReflectionProbeSkybox(Cubemap groundTruthEnvMap, ComputeShader cs) : base(groundTruthEnvMap)
        {
            m_cs = cs;
            m_specKernelID = m_cs.FindKernel("PrecomputeSpec");
            m_lutKernelID = m_cs.FindKernel("PrecomputeBRDFLut");
        }

        public override void Bake()
        {
            Cubemap spec = BakeSpecCubemap(512, 8);
            MaxRenderResources.Destroy(ref m_BakedEnvMap);
            m_BakedEnvMap = spec;
            m_BakedEnvMap.Apply(false, true);
            Texture2D lut = BakeBRDFLUT(512);
            MaxRenderResources.Destroy(ref m_brdfLUT);
            m_brdfLUT = lut;
            m_brdfLUT.Apply(false, true);
        }

        public override void Submit()
        {
            if (m_BakedEnvMap != null)
            {
                Shader.SetGlobalTexture("_IBLSpec", m_BakedEnvMap);
                Shader.SetGlobalInteger("_MaxMipCount", m_BakedEnvMap.mipmapCount);
            }
            if (m_brdfLUT != null)
                Shader.SetGlobalTexture("_BRDFLUT", m_brdfLUT);
        }

        public override void Clear()
        {
            MaxRenderResources.UnbindTexture("_IBLSpec", m_BakedEnvMap);
            MaxRenderResources.UnbindTexture("_BRDFLUT", m_brdfLUT);
            base.Clear();
            MaxRenderResources.Destroy(ref m_brdfLUT);
        }

        private Cubemap BakeSpecCubemap(int width, int mipCount)
        {
            const int threadsPerGroup = 32;
            Cubemap spec = new Cubemap(width, TextureFormat.RGBA32, mipCount);
            try
            {
                int mipWidth = width;
                for (int mip = 0; mip < mipCount; ++mip)
                {
                    int count = mipWidth * mipWidth;
                    int groups = (count + threadsPerGroup - 1) / threadsPerGroup;
                    using (ComputeBuffer result = new ComputeBuffer(count, sizeof(float) * 4))
                    {
                        Color[] colors = new Color[count];
                        m_cs.SetBuffer(m_specKernelID, "_Result", result);
                        m_cs.SetTexture(m_specKernelID, "_RadianceMap", m_GroundTruthEnvMap);
                        m_cs.SetInt("_Width", mipWidth);
                        m_cs.SetInt("_TexelCountsPerFace", count);
                        m_cs.SetFloat("_Roughness", (float)mip / (mipCount - 1));
                        for (int face = 0; face < 6; ++face)
                        {
                            m_cs.SetInt("_Face", face);
                            m_cs.Dispatch(m_specKernelID, groups, 1, 1);
                            result.GetData(colors);
                            spec.SetPixels(colors, (CubemapFace)face, mip);
                        }
                    }
                    mipWidth /= 2;
                }
                return spec;
            }
            catch
            {
                MaxRenderResources.Destroy(ref spec);
                throw;
            }
        }

        private Texture2D BakeBRDFLUT(int width)
        {
            const int threadsPerGroup = 32;
            Texture2D lut = new Texture2D(width, width, TextureFormat.RGBA32, false, true)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            try
            {
                int count = width * width;
                using (ComputeBuffer result = new ComputeBuffer(count, sizeof(float) * 4))
                {
                    m_cs.SetInt("_Width", width);
                    m_cs.SetBuffer(m_lutKernelID, "_BRDFLut", result);
                    m_cs.Dispatch(m_lutKernelID, (count + threadsPerGroup - 1) / threadsPerGroup, 1, 1);
                    Color[] colors = new Color[count];
                    result.GetData(colors);
                    lut.SetPixels(colors, 0);
                }
                return lut;
            }
            catch
            {
                MaxRenderResources.Destroy(ref lut);
                throw;
            }
        }
    }
}