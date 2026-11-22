using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace MaxSRP
{
    // Deserializes the AO toggle from archived scenes.
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class MaxAOSettings : MonoBehaviour
    {
        [FormerlySerializedAs("Configuration"), SerializeField, HideInInspector]
        private MaxAOConfig m_legacyConfiguration;

        // The pipeline owns the active configuration. Keep old scene references
        // as a fallback for scenes/diagnostics without a MaxSRP pipeline.
        public MaxAOConfig Configuration
        {
            get
            {
                var pipeline = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
                return pipeline ? pipeline.AOConfiguration : m_legacyConfiguration;
            }
            set => m_legacyConfiguration = value;
        }
        [Tooltip("Show linear ambient visibility as grayscale.")]
        public bool AOOnly;

        private void OnEnable() => MaxSceneComponents<MaxAOSettings>.Register(this);
        private void OnDisable() => MaxSceneComponents<MaxAOSettings>.Unregister(this);
    }
}
