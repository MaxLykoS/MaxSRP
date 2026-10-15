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
        private ColorOutputSettings m_colorOutputSettings = new ColorOutputSettings();
        public ColorOutputSettings ColorOutput => m_colorOutputSettings;

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

        [SerializeField] private SkyIBLSettings m_skyIBLSettings = new SkyIBLSettings();
        public SkyIBLSettings SkyIBL => m_skyIBLSettings;

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

        [SerializeField]
        private MaxProceduralSkyboxPass.AtmosphereSettings m_atmosphereScatteringSetting = new MaxProceduralSkyboxPass.AtmosphereSettings();
        public MaxProceduralSkyboxPass.AtmosphereSettings AtmosphereScatteringSetting
        {
            get
            {
                return m_atmosphereScatteringSetting;
            }
        }
    }
}
