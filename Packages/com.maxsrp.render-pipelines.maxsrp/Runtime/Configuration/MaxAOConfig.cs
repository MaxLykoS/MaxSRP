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
        [Tooltip("Full resolution. Intel budgets: Low 1x2, Medium 2x2, High 3x3, Ultra 9x3 slices/steps per side.")]
        public GTAOQuality Quality = GTAOQuality.Ultra;
        [Tooltip("Intel denoising: 0 disabled, 1 sharp, 2 medium, 3 soft. More passes soften the result, so Ultra defaults to one sharp pass.")]
        [Range(0, 3)] public int DenoisePasses = 1;
        [Tooltip("Use R32 Float working depth and FP32 arithmetic. The default uses R16 Float working depth and Intel's min16float path.")]
        public bool Use32BitWorkingDepth;
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

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new GTAOProfile();
            if (m_mobile == null) m_mobile = new SSAOProfile();
            MaxRenderQuality.NotifyChanged(this);
        }
    }

    // The per-camera AO inputs combine the active pipeline profile with the scene toggle.
    public readonly struct MaxAmbientOcclusion
    {
        public readonly MaxAOConfig Configuration;
        public readonly bool AOOnly;
        private readonly GTAOProfile m_pc;
        internal readonly SSAOProfile MobileProfile;

        internal MaxAmbientOcclusion(MaxAOConfig configuration, MaxRenderPlatform platform, bool aoOnly)
        {
            Configuration = configuration;
            AOOnly = aoOnly;
            bool configured = configuration != null;
            Platform = configured ? (AOPlatform)platform : AOPlatform.PC;
            m_pc = configured ? configuration.PCProfile : null;
            MobileProfile = configured ? configuration.MobileProfile : null;
            Compute = configured ? configuration.Compute : null;
            SSAOShader = configured ? configuration.SSAOShader : null;
        }

        internal bool HasProfile => m_pc != null;
        public bool AOEnabled => Platform == AOPlatform.Mobile ? MobileProfile.Enabled : m_pc?.Enabled ?? false;
        public AOPlatform Platform { get; }
        public ComputeShader Compute { get; }
        public Shader SSAOShader { get; }
        public float Strength => Platform == AOPlatform.Mobile ? MobileProfile.Strength : m_pc.Strength;
        public float Radius => m_pc.Radius;
        public int Quality => (int)m_pc.Quality;
        public int DenoisePasses => m_pc.DenoisePasses;
        public bool Use32BitWorkingDepth => m_pc.Use32BitWorkingDepth;
    }
}
