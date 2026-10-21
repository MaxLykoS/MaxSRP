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
        [SerializeField, Tooltip("Required deferred lighting shader. The asset reference keeps it in Player builds.")]
        private Shader m_lightingShader;
        public Shader LightingShader => m_lightingShader;

        [SerializeField]
        private ColorOutputSettings m_colorOutputSettings = new ColorOutputSettings();
        public ColorOutputSettings ColorOutput => m_colorOutputSettings;

        [SerializeField] private TemporalAASettings m_temporalAASettings = new TemporalAASettings();
        public TemporalAASettings TemporalAA => m_temporalAASettings;
        [SerializeField] private Shader m_temporalShader;
        public Shader TemporalShader => m_temporalShader;

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
