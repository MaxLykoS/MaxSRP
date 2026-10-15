using UnityEngine;

namespace MaxSRP
{
    public class MaxLightProbeSkybox : MaxProbeBase
    {
        private readonly ComputeShader m_cs;
        private readonly int m_kernelIndex;
        private readonly Vector4[] m_coefficients = new Vector4[9];

        public MaxLightProbeSkybox(Cubemap groundTruthEnvMap, ComputeShader shader) : base(groundTruthEnvMap)
        {
            m_cs = shader;
            m_kernelIndex = m_cs.FindKernel("PrecomputeDiffuse");
        }

        public override void Bake()
        {
            Cubemap diffuse = BakeCubemapDiffuse();
            MaxRenderResources.Destroy(ref m_BakedEnvMap);
            m_BakedEnvMap = diffuse;
            m_BakedEnvMap.SmoothEdges();
            m_BakedEnvMap.Apply(false, true);
        }

        public override void Submit()
        {
            for (int i = 0; i < m_coefficients.Length; ++i)
                Shader.SetGlobalVector("c" + i, m_coefficients[i]);
            if (m_BakedEnvMap != null)
                Shader.SetGlobalTexture("_IBLDiffuse", m_BakedEnvMap);
        }

        public override void Clear()
        {
            MaxRenderResources.UnbindTexture("_IBLDiffuse", m_BakedEnvMap);
            base.Clear();
        }

        public Cubemap BakeCubemapDiffuse(int resultWidth = 32)
        {
            const int threadsPerGroup = 32;
            int count = resultWidth * resultWidth;
            int groups = (count + threadsPerGroup - 1) / threadsPerGroup;
            Cubemap diffuse = new Cubemap(resultWidth, m_GroundTruthEnvMap.format, 0);
            try
            {
                using (ComputeBuffer result = new ComputeBuffer(count, sizeof(float) * 4))
                {
                    Color[] colors = new Color[count];
                    m_cs.SetBuffer(m_kernelIndex, "_Result", result);
                    m_cs.SetTexture(m_kernelIndex, "_RadianceMap", m_GroundTruthEnvMap);
                    m_cs.SetInt("_Width", resultWidth);
                    m_cs.SetInt("_TexelCountsPerFace", count);
                    for (int face = 0; face < 6; ++face)
                    {
                        m_cs.SetInt("_Face", face);
                        m_cs.Dispatch(m_kernelIndex, groups, 1, 1);
                        result.GetData(colors);
                        diffuse.SetPixels(colors, (CubemapFace)face);
                    }
                }
                return diffuse;
            }
            catch
            {
                MaxRenderResources.Destroy(ref diffuse);
                throw;
            }
        }
    }
}