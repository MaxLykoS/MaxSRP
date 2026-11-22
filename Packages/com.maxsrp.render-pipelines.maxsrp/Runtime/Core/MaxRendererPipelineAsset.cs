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
        // 选择运行时配置档位，切换时标记配置已变更。
        public MaxRenderPlatform Platform
        {
            get => m_platform;
            set
            {
                if (m_platform != value)
                {
                    m_platform = value;
                    RefreshConfiguration();
                }
            }
        }

        [Header("Configurations")]
        [SerializeField] private MaxAtmosphereConfig m_atmosphereConfiguration;
        [SerializeField] private MaxTemporalAAConfig m_temporalAAConfiguration;
        [SerializeField] private MaxCloudConfig m_cloudConfiguration;
        [SerializeField] private MaxShadowConfig m_shadowConfiguration;
        [SerializeField] private MaxClusteredLightingConfig m_clusteredLightingConfiguration;
        [SerializeField] private MaxAOConfig m_aoConfiguration;
        [SerializeField] private MaxDDGIConfig m_ddgiConfiguration;
        [SerializeField] private MaxPostProcessConfig m_postProcessConfiguration;
        public MaxAtmosphereConfig AtmosphereConfiguration => m_atmosphereConfiguration;
        public MaxTemporalAAConfig TemporalAAConfiguration => m_temporalAAConfiguration;
        public MaxCloudConfig CloudConfiguration => m_cloudConfiguration;
        public MaxShadowConfig ShadowConfiguration => m_shadowConfiguration;
        public MaxClusteredLightingConfig ClusteredLightingConfiguration => m_clusteredLightingConfiguration;
        public MaxAOConfig AOConfiguration => m_aoConfiguration;
        public MaxDDGIConfig DDGIConfiguration => m_ddgiConfiguration;
        public MaxPostProcessConfig PostProcessConfiguration => m_postProcessConfiguration;

        [Header("Core Resources")]
        [SerializeField, Tooltip("Required deferred lighting shader, retained in Player builds.")]
        private Shader m_lightingShader;
        public Shader LightingShader => m_lightingShader;

        // 这些属性为渲染通道提供已经选定档位的参数。
        // 模块的序列化参数和资源统一保存在各自的配置资产中。
        public ClusteredLightingSettings ClusteredLighting => m_clusteredLightingConfiguration.GetProfile(Platform);
        public ComputeShader ClusteredLightingCS => m_clusteredLightingConfiguration.Compute;
        public float DDGIPCProbeSpacing => m_ddgiConfiguration
            ? m_ddgiConfiguration.PCProbeSpacing : MaxDDGIConfig.DefaultPCProbeSpacing;
        internal float DDGISamplingSpacing => Platform == MaxRenderPlatform.Mobile
            ? MaxDDGIConfig.ResolveMobileSamplingSpacing(m_ddgiConfiguration) : 0f;
        public TemporalAASettings TemporalAA => m_temporalAAConfiguration.GetProfile(Platform);
        public Shader TemporalShader => m_temporalAAConfiguration.TemporalShader;
        public CascadeSettings CascadeSetting => m_shadowConfiguration.GetProfile(Platform);
        public SkyIBLSettings SkyIBL => m_atmosphereConfiguration.GetProfile(Platform).SkyIBL;
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
        // 递增配置版本号，由管线在下一次渲染批次开始时检查。
        internal void MarkConfigurationDirty() { unchecked { ++m_configurationRevision; } }

        // 标记运行时配置修改，并在编辑器中安排更新通知。
        public void RefreshConfiguration()
        {
            MarkConfigurationDirty();
#if UNITY_EDITOR
            MaxRenderQuality.NotifyChanged(this);
#endif
        }

        // 在检视面板修改或资产校验后，请求管线重新检查配置。
        protected override void OnValidate()
        {
            // 将重建推迟到渲染批次开始，以免在检视面板回调内释放资源。
            // 修改非当前档位的配置会保留现有历史。
            RefreshConfiguration();
        }

        // 在构造管线之前检查必需的模块配置资产是否齐全。
        internal void ValidateConfigurations()
        {
            if (!m_atmosphereConfiguration || !m_temporalAAConfiguration || !m_cloudConfiguration ||
                !m_shadowConfiguration || !m_clusteredLightingConfiguration)
                throw new InvalidOperationException("MaxSRP requires Atmosphere, Temporal AA, Cloud, Shadow and Clustered Lighting configuration assets.");
        }

        // 为 Unity 创建与此资产对应的渲染管线实例。
        protected override RenderPipeline CreatePipeline() => new MaxRenderPipeline(this);
    }
}
