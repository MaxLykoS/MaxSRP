using System;
using UnityEngine;

namespace MaxSRP
{
    [Serializable]
    public sealed class CloudProfile
    {
        public CloudLayerSettings Layer = new CloudLayerSettings();
        public SixWayCloudSettings SixWay = new SixWayCloudSettings();
    }

    [CreateAssetMenu(menuName = "MaxSRP/Cloud Configuration")]
    public sealed class MaxCloudConfig : ScriptableObject
    {
        [SerializeField] private CloudProfile m_pc = new CloudProfile();
        [SerializeField] private CloudProfile m_mobile = new CloudProfile();
        // Top-level script defaults work for newly created assets; nested
        // overrides remain serialized on each profile and are never replaced.
        [SerializeField, HideInInspector] private Shader m_layerLightingShader;
        [SerializeField, HideInInspector] private Shader m_sixWayShader;

        public CloudProfile GetProfile(MaxRenderPlatform platform)
        {
            CloudProfile profile = platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;
            EnsureProfile(profile);
            return profile;
        }

        private void EnsureProfile(CloudProfile profile)
        {
            if (profile.Layer == null) profile.Layer = new CloudLayerSettings();
            if (profile.SixWay == null) profile.SixWay = new SixWayCloudSettings();
            if (!profile.Layer.LightingShader) profile.Layer.LightingShader = m_layerLightingShader;
            if (!profile.SixWay.Shader) profile.SixWay.Shader = m_sixWayShader;
        }

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new CloudProfile();
            if (m_mobile == null) m_mobile = new CloudProfile();
            EnsureProfile(m_pc);
            EnsureProfile(m_mobile);
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
