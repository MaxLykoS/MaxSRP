using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace MaxSRP
{
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("MaxSRP/Ambient Occlusion")]
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

        public bool AOEnabled => Configuration && Configuration.Enabled;
        public ComputeShader Compute => Configuration ? Configuration.Compute : null;
        public Shader SSAOShader => Configuration ? Configuration.SSAOShader : null;
        public AOPlatform Platform => (AOPlatform)MaxRenderQuality.Platform;
        public float Strength => !Configuration ? 0f : Platform == AOPlatform.Mobile
            ? Configuration.MobileProfile.Strength : Configuration.PCProfile.Strength;
        public float Radius => !Configuration ? 1f : Platform == AOPlatform.Mobile
            ? Configuration.MobileProfile.Radius : Configuration.PCProfile.Radius;
        public int Quality => !Configuration ? 3 : Platform == AOPlatform.Mobile
            ? (int)Configuration.MobileProfile.Quality : (int)Configuration.PCProfile.Quality;
        public int DenoisePasses => Configuration ? Configuration.PCProfile.DenoisePasses : 1;

        private static readonly List<MaxAOSettings> Active = new List<MaxAOSettings>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        internal static MaxAOSettings FindForCamera(Camera camera)
        {
            if (camera.stereoEnabled ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
                return null;
            Scene scene = camera.cameraType == CameraType.SceneView
                ? SceneManager.GetActiveScene() : camera.gameObject.scene;
            MaxAOSettings chosen = null;
            for (int i = Active.Count - 1; i >= 0; --i)
            {
                var entry = Active[i];
                if (!entry) { Active.RemoveAt(i); continue; }
                if (!entry.isActiveAndEnabled || entry.gameObject.scene != scene) continue;
                if (!chosen || entry.GetInstanceID() < chosen.GetInstanceID()) chosen = entry;
            }
            return chosen;
        }

        internal int HistoryKey
        {
            get
            {
                unchecked
                {
                    int key = GetInstanceID();
                    key = key * 31 + (Configuration ? Configuration.GetInstanceID() : 0);
                    key = key * 31 + (Configuration ? Configuration.HistoryKey : 0);
                    return key * 31 + (AOOnly ? 1 : 0);
                }
            }
        }
    }
}
