using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    [ExecuteAlways]
    public class MaxReflectionProbeSkybox : MaxProbeBase
    {
        private Texture2D m_brdfLUT;
        private ComputeShader m_cs;

        public MaxReflectionProbeSkybox(Cubemap groundTruthEnvMap, Texture2D brdfLut, ComputeShader cs) : base(groundTruthEnvMap)
        {
            m_GroundTruthEnvMap = groundTruthEnvMap;
            m_brdfLUT = brdfLut;
            m_cs = cs;
        }

        public override void Bake()
        {

        }

        public override void Submit()
        {
            Shader.SetGlobalTexture("_IBLSpec", m_BakedEnvMap);
            Shader.SetGlobalTexture("_BRDFLUT", m_brdfLUT);
        }

        public override void Clear()
        {
            base.Clear();
        }
    }
}