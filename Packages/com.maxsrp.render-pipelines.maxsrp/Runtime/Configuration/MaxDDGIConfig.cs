using UnityEngine;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/DDGI Configuration")]
    public sealed class MaxDDGIConfig : ScriptableObject
    {
        public const float DefaultPCProbeSpacing = 0.25f;
        private const float DefaultMobileDensityRatio = 0.25f;

        [Header("PC")]
        [SerializeField, Range(0.25f, 100f), Tooltip("Default spacing in meters when baking the PC probe grid.")]
        private float m_pcProbeSpacing = DefaultPCProbeSpacing;

        [Header("Mobile")]
        [SerializeField, Range(0f, 1f), Tooltip("Probe samples per meter relative to PC. One quarter reads every fourth probe along each axis on the PC grid.")]
        private float m_mobileDensityRatio = DefaultMobileDensityRatio;

        public float PCProbeSpacing => m_pcProbeSpacing >= 0.25f && m_pcProbeSpacing <= 100f
            ? m_pcProbeSpacing : DefaultPCProbeSpacing;

        internal static float ResolveMobileSamplingSpacing(MaxDDGIConfig configuration)
        {
            float ratio = configuration ? configuration.m_mobileDensityRatio : DefaultMobileDensityRatio;
            if (!(ratio > 0f && ratio <= 1f)) ratio = DefaultMobileDensityRatio;
            return (configuration ? configuration.PCProbeSpacing : DefaultPCProbeSpacing) / ratio;
        }

        private void OnValidate() => MaxRenderQuality.NotifyChanged(this);
    }
}
