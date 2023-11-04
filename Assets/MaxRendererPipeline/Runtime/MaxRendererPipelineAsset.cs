using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/MaxRendererPipelineAsset")]
    public class MaxRendererPipelineAsset : RenderPipelineAsset
    {
        [SerializeField]
        private CascadeSettings m_cascadeSettings = new CascadeSettings();

        public CascadeSettings CascadeSetting
        {
            get
            {
                return m_cascadeSettings;
            }
        }

        protected override RenderPipeline CreatePipeline()
        {
            return new MaxRenderPipeline(this);
        }

        [SerializeField]
        private Cubemap m_envMap;
        public Cubemap ENVMap
        {
            get 
            {
                return m_envMap;
            }
        }

        [SerializeField]
        private ComputeShader m_iblCS;
        public ComputeShader IBLCS
        {
            get
            {
                return m_iblCS;
            }
        }

        [SerializeField]
        private Shader m_ssShadowMapShader;
        public Shader SSShadowMapShader
        {
            get
            {
                return m_ssShadowMapShader;
            }
        }
    }
}
