using UnityEditor.EventSystems;
using UnityEngine;
using UnityEngine.Rendering;
using static SphericalHarmonics;

namespace MaxSRP
{
    [ExecuteAlways]
    public class MaxLightProbeSkybox : MaxProbeBase
    {
        private ComputeShader m_shader;
        private int m_kernelIndex;

        private Vector4[] m_coefficients;

        public MaxLightProbeSkybox(Cubemap groundTruthEnvMap, ComputeShader shader) : base(groundTruthEnvMap)
        {
            m_coefficients = new Vector4[9];

            m_shader = shader;
            m_kernelIndex = m_shader.FindKernel("CSMain");
        }

        public override void Bake()
        {
            //SphericalHarmonics.CPU_Project_Uniform_9Coeff(m_GroundTruthEnvMap, m_coefficients);
            BakeCubemapDiffuse();
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
            Cubemap diffuse = new Cubemap(resultWidth, m_GroundTruthEnvMap.format, 0);

            ComputeBuffer faceResult = new ComputeBuffer(texelCountsPerFace, sizeof(float) * 4);
            Color[] faceColors = new Color[texelCountsPerFace];
            m_shader.SetBuffer(m_kernelIndex, "_Result", faceResult);
            m_shader.SetTexture(m_kernelIndex, "_RadianceMap", m_GroundTruthEnvMap);
            m_shader.SetInt("_Width", resultWidth);
            m_shader.SetInt("_TexelCountsPerFace", texelCountsPerFace);

            // ±éÀú6¸öÃæ
            for (int face = 0; face < 6; ++face)
            {
                m_shader.SetInt("_Face", face);
                int groupsCount = texelCountsPerFace / threadsPerGroup + 1;

                m_shader.Dispatch(m_kernelIndex, groupsCount, 1, 1);

                faceResult.GetData(faceColors);

                diffuse.SetPixels(faceColors, (CubemapFace)face);
            }

            faceResult.Release();

            m_BakedEnvMap = diffuse;
            m_BakedEnvMap.SmoothEdges();
            m_BakedEnvMap.Apply(false, true);

            return diffuse;
        }
    }
}