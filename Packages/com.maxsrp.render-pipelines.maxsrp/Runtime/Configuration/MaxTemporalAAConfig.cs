using UnityEngine;

namespace MaxSRP
{
    public enum TemporalAADebugView { Off, HistoryWeight, RejectedPixels }

    [CreateAssetMenu(menuName = "MaxSRP/Temporal AA Configuration")]
    public sealed class MaxTemporalAAConfig : ScriptableObject
    {
        [SerializeField] private TemporalAASettings m_pc = new TemporalAASettings();
        [SerializeField] private TemporalAASettings m_mobile = new TemporalAASettings();
        [SerializeField, Tooltip("Inspect the active profile after its normal resolve. History Weight shows the applied blend weight; Rejected Pixels shows pixels with zero history weight in white.")]
        private TemporalAADebugView m_debugView;
        [SerializeField, HideInInspector] private Shader m_temporalShader;

        public TemporalAASettings GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;
        public TemporalAADebugView DebugView => m_debugView;
        public Shader TemporalShader => m_temporalShader;

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new TemporalAASettings();
            if (m_mobile == null) m_mobile = new TemporalAASettings();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
