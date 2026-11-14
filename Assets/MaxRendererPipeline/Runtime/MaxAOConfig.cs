using System;
using UnityEngine;

namespace MaxSRP
{
    public enum AOPlatform { PC = 0, Mobile = 1 }
    public enum GTAOQuality { Low = 0, Medium = 1, High = 2, Ultra = 3 }
    public enum SSAOQuality { VeryLow = 0, Low = 1, Medium = 2, High = 3, VeryHigh = 4 }

    [Serializable]
    public sealed class GTAOProfile
    {
        [Tooltip("Enable ambient occlusion for the PC profile.")]
        public bool Enabled = true;
        [Range(0f, 1f)] public float Strength = 1f;
        [Tooltip("Occlusion radius in world metres.")]
        [Range(0.05f, 3f)] public float Radius = 1f;
        [Tooltip("Full-resolution FP32. Intel budgets: Low 1x2, Medium 2x2, High 3x3, Ultra 9x3 slices/steps per side.")]
        public GTAOQuality Quality = GTAOQuality.Ultra;
        [Tooltip("Intel denoising: 0 disabled, 1 sharp, 2 medium, 3 soft. More passes soften the result, so Ultra defaults to one sharp pass.")]
        [Range(0, 3)] public int DenoisePasses = 1;
    }

    [Serializable]
    public sealed class SSAOProfile
    {
        [Tooltip("Enable ambient occlusion for the Mobile profile.")]
        public bool Enabled = true;
        [Range(0f, 1f)] public float Strength = 0.5f;
        [Tooltip("Use a fixed world radius instead of UE's default depth-scaled screen radius.")]
        public bool RadiusInWorldSpace;
        [Tooltip("Fixed radius in metres, used only in world-space mode.")]
        [Range(0.05f, 3f)] public float Radius = 2f;
        [Tooltip("UE screen-radius parameter. 200 is the UE default; this is not a radius in metres.")]
        [Range(1f, 500f)] public float ScreenRadius = 200f;
        public SSAOQuality Quality = SSAOQuality.Medium;
        [Range(0.1f, 4f)] public float Power = 2f;
        [Tooltip("Bias calibration in metres (UE default parameter: 3 cm). The shader offset scales with view depth.")]
        [Range(0f, 0.2f)] public float Bias = 0.03f;
        [Min(0f)] public float FadeDistance = 80f;
        [Min(0.01f)] public float FadeRadius = 50f;
        [Tooltip("UE 5.8 mobile default is full resolution. Enable for half width/height and reconstruction.")]
        public bool HalfResolution;
        [Tooltip("Half-resolution reconstruction: 0 bilinear, 1 four-tap bilateral, 2 nine-tap bilateral.")]
        [Range(0, 2)] public int UpsampleQuality = 2;
        [Tooltip("Apply spatial smoothing when shared TAA is unavailable, matching UE mobile scheduling.")]
        public bool Blur = true;
    }

    [CreateAssetMenu(menuName = "MaxSRP/Ambient Occlusion Configuration")]
    public sealed class MaxAOConfig : ScriptableObject
    {
        [Tooltip("PC uses Intel XeGTAO. The pipeline asset selects which profile is active.")]
        [SerializeField] private GTAOProfile m_pc = new GTAOProfile();
        [Tooltip("Mobile uses UE 5.8 mobile SSAO. The pipeline asset selects which profile is active.")]
        [SerializeField] private SSAOProfile m_mobile = new SSAOProfile();
        [SerializeField, HideInInspector] private ComputeShader m_compute;
        [SerializeField, HideInInspector] private Shader m_ssaoShader;

        public AOPlatform Platform => (AOPlatform)MaxRenderQuality.Platform;
        public bool Enabled => Platform == AOPlatform.Mobile ? m_mobile.Enabled : m_pc.Enabled;
        public GTAOProfile PCProfile => m_pc;
        public SSAOProfile MobileProfile => m_mobile;
        public ComputeShader Compute => m_compute;
        public Shader SSAOShader => m_ssaoShader;

        internal int HistoryKey
        {
            get
            {
                unchecked
                {
                    AOPlatform platform = Platform;
                    int key = (int)platform;
                    if (platform == AOPlatform.Mobile)
                    {
                        key = key * 31 + (m_mobile.Enabled ? 1 : 0);
                        key = key * 31 + (m_ssaoShader ? m_ssaoShader.GetInstanceID() : 0);
                        key = key * 31 + m_mobile.Strength.GetHashCode();
                        key = key * 31 + m_mobile.Radius.GetHashCode();
                        key = key * 31 + (m_mobile.RadiusInWorldSpace ? 1 : 0);
                        key = key * 31 + m_mobile.ScreenRadius.GetHashCode();
                        key = key * 31 + (int)m_mobile.Quality;
                        key = key * 31 + m_mobile.Power.GetHashCode();
                        key = key * 31 + m_mobile.Bias.GetHashCode();
                        key = key * 31 + m_mobile.FadeDistance.GetHashCode();
                        key = key * 31 + m_mobile.FadeRadius.GetHashCode();
                        key = key * 31 + (m_mobile.HalfResolution ? 1 : 0);
                        key = key * 31 + m_mobile.UpsampleQuality;
                        return key * 31 + (m_mobile.Blur ? 1 : 0);
                    }
                    key = key * 31 + (m_pc.Enabled ? 1 : 0);
                    key = key * 31 + (m_compute ? m_compute.GetInstanceID() : 0);
                    key = key * 31 + m_pc.Strength.GetHashCode();
                    key = key * 31 + m_pc.Radius.GetHashCode();
                    key = key * 31 + (int)m_pc.Quality;
                    return key * 31 + m_pc.DenoisePasses;
                }
            }
        }

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new GTAOProfile();
            if (m_mobile == null) m_mobile = new SSAOProfile();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
