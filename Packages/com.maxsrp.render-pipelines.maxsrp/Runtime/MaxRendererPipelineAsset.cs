using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/MaxRendererPipelineAsset")]
    public class MaxRendererPipelineAsset : RenderPipelineAsset
    {
        [Header("Quality")]
        [SerializeField, Tooltip("Controls every module's PC/Mobile profile and existing algorithm variants.")]
        private MaxRenderPlatform m_platform = MaxRenderPlatform.PC;
        public MaxRenderPlatform Platform
        {
            get => m_platform;
            set { if (m_platform != value) { m_platform = value; RefreshConfiguration(); } }
        }

        [Header("Configurations")]
        [SerializeField] private MaxAtmosphereConfig m_atmosphereConfiguration;
        [SerializeField] private MaxTemporalAAConfig m_temporalAAConfiguration;
        [SerializeField] private MaxCloudConfig m_cloudConfiguration;
        [SerializeField] private MaxShadowConfig m_shadowConfiguration;
        [SerializeField] private MaxClusteredLightingConfig m_clusteredLightingConfiguration;
        [SerializeField] private MaxAOConfig m_aoConfiguration;
        [SerializeField] private MaxPostProcessConfig m_postProcessConfiguration;
        public MaxAtmosphereConfig AtmosphereConfiguration => m_atmosphereConfiguration;
        public MaxTemporalAAConfig TemporalAAConfiguration => m_temporalAAConfiguration;
        public MaxCloudConfig CloudConfiguration => m_cloudConfiguration;
        public MaxShadowConfig ShadowConfiguration => m_shadowConfiguration;
        public MaxClusteredLightingConfig ClusteredLightingConfiguration => m_clusteredLightingConfiguration;
        public MaxAOConfig AOConfiguration => m_aoConfiguration;
        public MaxPostProcessConfig PostProcessConfiguration => m_postProcessConfiguration;

        [Header("Core Resources")]
        [SerializeField, Tooltip("Required deferred lighting shader, retained in Player builds.")]
        private Shader m_lightingShader;
        public Shader LightingShader => m_lightingShader;

        // Resolved views preserve pass interfaces. Serialized module parameters
        // and resources live exclusively on their configuration assets.
        public ClusteredLightingSettings ClusteredLighting => m_clusteredLightingConfiguration.GetProfile(Platform);
        public ComputeShader ClusteredLightingCS => m_clusteredLightingConfiguration.Compute;
        public TemporalAASettings TemporalAA => m_temporalAAConfiguration.GetProfile(Platform);
        public Shader TemporalShader => m_temporalAAConfiguration.TemporalShader;
        public CascadeSettings CascadeSetting => m_shadowConfiguration.GetProfile(Platform);
        public SkyIBLSettings SkyIBL => m_atmosphereConfiguration.GetProfile(Platform).SkyIBL;
        public CloudLayerSettings CloudLayer => m_cloudConfiguration.GetProfile(Platform).Layer;
        public SixWayCloudSettings SixWayCloud => m_cloudConfiguration.GetProfile(Platform).SixWay;
        public ComputeShader IBLCS => m_atmosphereConfiguration.IBLCS;
        public ComputeShader AerialPerspectiveCompute => m_atmosphereConfiguration.AerialPerspectiveCompute;
        public MaxProceduralSkyboxPass.AtmosphereSettings AtmosphereScatteringSetting =>
            m_atmosphereConfiguration.GetProfile(Platform).Atmosphere;
        private static readonly ColorOutputSettings DefaultLegacyOutput = new ColorOutputSettings();
        public ColorOutputSettings ColorOutput => m_postProcessConfiguration
            ? m_postProcessConfiguration.LegacyOutput : DefaultLegacyOutput;

        [NonSerialized] private int m_configurationRevision;
        internal int ConfigurationRevision => m_configurationRevision;
        internal void MarkConfigurationDirty() { unchecked { ++m_configurationRevision; } }

        // Call after editing configuration references or profiles in Player code.
        public void RefreshConfiguration()
        {
            MarkConfigurationDirty();
#if UNITY_EDITOR
            MaxRenderQuality.NotifyChanged(this);
#endif
        }

        protected override void OnValidate()
        {
            // Rebuild at a render boundary instead of destroying resources during
            // an Inspector callback. Inactive profile edits retain history.
            RefreshConfiguration();
        }

        internal string ConstructionState()
        {
            // Evaluated only on configuration notifications, never every frame.
            // AO and post-process values retain their own history/update rules.
            return string.Join("|", (int)Platform, Id(m_lightingShader),
                Id(m_atmosphereConfiguration), JsonUtility.ToJson(m_atmosphereConfiguration.GetProfile(Platform)),
                Id(IBLCS), Id(AerialPerspectiveCompute), Id(m_atmosphereConfiguration.ProceduralSkyboxShader),
                Id(m_atmosphereConfiguration.TransmittanceShader), Id(m_atmosphereConfiguration.MultiscatteringShader),
                Id(m_atmosphereConfiguration.SkyviewShader), Id(m_atmosphereConfiguration.AerialPerspectiveLUTShader),
                Id(m_temporalAAConfiguration), JsonUtility.ToJson(TemporalAA), Id(TemporalShader),
                Id(m_cloudConfiguration), JsonUtility.ToJson(m_cloudConfiguration.GetProfile(Platform)),
                Id(m_shadowConfiguration), JsonUtility.ToJson(CascadeSetting), Id(m_shadowConfiguration.TileClearShader),
                Id(m_clusteredLightingConfiguration), JsonUtility.ToJson(ClusteredLighting), Id(ClusteredLightingCS),
                Id(m_postProcessConfiguration ? m_postProcessConfiguration.OutputShader : null));
        }

        private static int Id(UnityEngine.Object value) => value ? value.GetInstanceID() : 0;

        internal void ValidateConfigurations()
        {
            if (!m_atmosphereConfiguration || !m_temporalAAConfiguration || !m_cloudConfiguration ||
                !m_shadowConfiguration || !m_clusteredLightingConfiguration)
                throw new InvalidOperationException("MaxSRP requires Atmosphere, Temporal AA, Cloud, Shadow and Clustered Lighting configuration assets.");
        }

        protected override RenderPipeline CreatePipeline()
        {
            ValidateConfigurations();
            return new MaxRenderPipeline(this);
        }
    }
}
