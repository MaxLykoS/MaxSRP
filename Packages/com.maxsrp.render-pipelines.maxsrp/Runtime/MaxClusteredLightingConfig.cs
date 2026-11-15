using UnityEngine;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/Clustered Lighting Configuration")]
    public sealed class MaxClusteredLightingConfig : ScriptableObject
    {
        [SerializeField] private ClusteredLightingSettings m_pc = new ClusteredLightingSettings();
        [SerializeField] private ClusteredLightingSettings m_mobile = new ClusteredLightingSettings();
        [SerializeField, HideInInspector] private ComputeShader m_compute;

        public ClusteredLightingSettings GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;
        public ComputeShader Compute => m_compute;

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new ClusteredLightingSettings();
            if (m_mobile == null) m_mobile = new ClusteredLightingSettings();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
