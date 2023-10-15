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
        private ShadowSetting m_shadowSetting = new ShadowSetting();

        public ShadowSetting ShadowSetting
        {
            get
            {
                return m_shadowSetting;
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
    }
}
