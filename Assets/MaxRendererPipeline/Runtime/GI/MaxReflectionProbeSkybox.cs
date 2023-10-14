using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [ExecuteAlways]
    public class MaxReflectionProbeSkybox : MaxProbeBase
    {
        private Texture2D m_brdfLUT;

        private ComputeShader m_cs;
        private int m_specKernelID;
        private int m_lutKernelID;

        public MaxReflectionProbeSkybox(Cubemap groundTruthEnvMap, ComputeShader cs) : base(groundTruthEnvMap)
        {
            m_GroundTruthEnvMap = groundTruthEnvMap;
            m_cs = cs;

            m_specKernelID = m_cs.FindKernel("PrecomputeSpec");
            m_lutKernelID = m_cs.FindKernel("PrecomputeBRDFLut");
        }

        public override void Bake()
        {
            Cubemap spec = BakeSpecCubemap(512, 8);
            if (m_BakedEnvMap != null)
            {
                GameObject.DestroyImmediate(m_BakedEnvMap);
                m_BakedEnvMap = null;
            }
            m_BakedEnvMap = spec;
            m_BakedEnvMap.Apply(false, true);

            Texture2D lut = BakeBRDFLUT(512);
            if (m_brdfLUT != null)
            {
                GameObject.DestroyImmediate(m_brdfLUT);
                m_brdfLUT = null;
            }
            m_brdfLUT = lut;
            lut.Apply(false, true);
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
            base.Clear();
        }

        private Cubemap BakeSpecCubemap(int width, int mipCount)
        {
            const int threadsPerGroup = 32;

            int texelCountsPerFace = width * width;

            Cubemap spec = new Cubemap(width, TextureFormat.RGBA32, mipCount);

            int mipWidth = width;
            for (int mip = 0; mip < mipCount; ++mip)
            {
                float roughness = (float)mip / (float)(mipCount - 1);
                int texelCountsPerFaceMip = mipWidth * mipWidth;
                int groupsCount = texelCountsPerFaceMip / threadsPerGroup + 1;

                Color[] faceColors = new Color[texelCountsPerFaceMip];
                ComputeBuffer faceResult = new ComputeBuffer(texelCountsPerFaceMip, sizeof(float) * 4);

                m_cs.SetBuffer(m_specKernelID, "_Result", faceResult);
                m_cs.SetTexture(m_specKernelID, "_RadianceMap", m_GroundTruthEnvMap);
                m_cs.SetInt("_Width", mipWidth);
                m_cs.SetInt("_TexelCountsPerFace", texelCountsPerFaceMip);
                m_cs.SetFloat("_Roughness", roughness);

                // 遍历6个面
                for (int face = 0; face < 6; ++face)
                {
                    m_cs.SetInt("_Face", face);

                    m_cs.Dispatch(m_specKernelID, groupsCount, 1, 1);

                    faceResult.GetData(faceColors);

                    spec.SetPixels(faceColors, (CubemapFace)face, mip);
                }

                // reisze framebuffer according to mip-level size.
                mipWidth /= 2;
                faceResult.Release();
            }

            return spec;
        }

        private Texture2D BakeBRDFLUT(int width)
        {
            const int threadsPerGroup = 32;

            Texture2D lut = new Texture2D(width, width, TextureFormat.RGBA32, false, true);
            lut.wrapMode = TextureWrapMode.Clamp;
            ComputeBuffer lutBuffer = new ComputeBuffer(width * width, sizeof(float) * 4);

            m_cs.SetInt("_Width", width);
            m_cs.SetBuffer(m_lutKernelID, "_BRDFLut", lutBuffer);

            int threadGroupsCount = width * width / threadsPerGroup;
            m_cs.Dispatch(m_lutKernelID, threadGroupsCount, 1, 1);

            Color[] colors = new Color[width * width]; ; ; ;
            lutBuffer.GetData(colors); ;

            lut.SetPixels(colors, 0);

            lutBuffer.Release();

            return lut;
        }
    }
}