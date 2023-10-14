using UnityEditor.EventSystems;
using UnityEngine;
using UnityEngine.Rendering;
using static SphericalHarmonics;

namespace MaxSRP
{
    [ExecuteAlways]
    public class MaxLightProbeSkybox : MaxProbeBase
    {
        private ComputeShader m_cs;
        private int m_kernelIndex;

        private Vector4[] m_coefficients;

        public MaxLightProbeSkybox(Cubemap groundTruthEnvMap, ComputeShader shader) : base(groundTruthEnvMap)
        {
            m_coefficients = new Vector4[9];

            m_cs = shader;
            m_kernelIndex = m_cs.FindKernel("PrecomputeDiffuse");
        }

        public override void Bake()
        {
            //SphericalHarmonics.CPU_Project_Uniform_9Coeff(m_GroundTruthEnvMap, m_coefficients);
            Cubemap diffuse = BakeCubemapDiffuse();
            if (m_BakedEnvMap != null)
            {
                GameObject.DestroyImmediate(m_BakedEnvMap);
                m_BakedEnvMap = null;
            }
            m_BakedEnvMap = diffuse;
            m_BakedEnvMap.SmoothEdges();
            m_BakedEnvMap.Apply(false, true);
        }

        public override void Submit()
        {
            base.Submit();

            for (int i = 0; i < 9; ++i)
            {
                Shader.SetGlobalVector("c" + i.ToString(), m_coefficients[i]);
            }

            if (m_BakedEnvMap != null)
            {
                Shader.SetGlobalTexture("_IBLDiffuse", m_BakedEnvMap);
            }
        }

        public override void Clear()
        {
            base.Clear();
        }

        public Cubemap BakeCubemapDiffuse(int resultWidth = 32)
        {
            const int threadsPerGroup = 32;
            int texelCountsPerFace = resultWidth * resultWidth;
            int groupsCount = texelCountsPerFace / threadsPerGroup + 1;
            Cubemap diffuse = new Cubemap(resultWidth, m_GroundTruthEnvMap.format, 0);

            ComputeBuffer faceResult = new ComputeBuffer(texelCountsPerFace, sizeof(float) * 4);
            Color[] faceColors = new Color[texelCountsPerFace];
            m_cs.SetBuffer(m_kernelIndex, "_Result", faceResult);
            m_cs.SetTexture(m_kernelIndex, "_RadianceMap", m_GroundTruthEnvMap);
            m_cs.SetInt("_Width", resultWidth);
            m_cs.SetInt("_TexelCountsPerFace", texelCountsPerFace);

            // ±éÀú6¸öÃæ
            for (int face = 0; face < 6; ++face)
            {
                m_cs.SetInt("_Face", face);

                m_cs.Dispatch(m_kernelIndex, groupsCount, 1, 1);

                faceResult.GetData(faceColors);

                diffuse.SetPixels(faceColors, (CubemapFace)face);
            }

            faceResult.Release();

            return diffuse;
        }
    }
}