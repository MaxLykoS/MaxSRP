using System;
using UnityEngine;

namespace MaxSRP
{
    public enum PostProcessPlatform { PC = 0, Mobile = 1 }
    public enum ExposureMode { Manual = 0, Histogram = 1, Basic = 2 }

    [Serializable]
    public sealed class ExposureSettings
    {
        public ExposureMode Mode = ExposureMode.Histogram;
        [Range(-10f, 24f)] public float CameraEV100 = 15f;
        [Range(-16f, 16f)] public float CompensationEV;
        [Tooltip("Exposure limits in EV100, using UE's default lens attenuation (Lmax = 1 at EV100 = 0).")]
        public float MinEV100 = -10f;
        public float MaxEV100 = 20f;
        [Min(0f)] public float SpeedUp = 3f;
        [Min(0f)] public float SpeedDown = 1f;
        [Range(0f, 100f)] public float LowPercent = 10f;
        [Range(0f, 100f)] public float HighPercent = 90f;
        public float HistogramMinEV100 = -10f;
        public float HistogramMaxEV100 = 20f;
        [Tooltip("Linear mask: white contributes fully, black reduces metering influence.")]
        public Texture2D MeteringMask;
        [Tooltip("Additional compensation in stops. X is measured scene EV100; Y is compensation.")]
        public AnimationCurve CompensationCurve = AnimationCurve.Linear(-10f, 0f, 20f, 0f);

        public float GetManualMultiplier() => LightPhotometry.HDRScale *
            Mathf.Pow(2f, Mathf.Clamp(CompensationEV, -16f, 16f) - Mathf.Clamp(CameraEV100, -10f, 24f));
    }

    [Serializable]
    public sealed class BloomSettings
    {
        public bool Enabled = true;
        [Min(0f)] public float Intensity = 0.675f;
        [Tooltip("UE threshold in exposed linear color; -1 includes all light without thresholding.")]
        [Min(-1f)] public float Threshold = -1f;
        [Range(0.1f, 8f)] public float SizeScale = 4f;
    }

    [Serializable]
    public sealed class ColorGradingSettings
    {
        [Tooltip("Disabled by default. All parameter defaults are neutral, including white balance.")]
        public bool Enabled;
        [Range(1500f, 15000f)] public float Temperature = 6500f;
        [Range(-1f, 1f)] public float Tint;
        [Tooltip("RGB controls individual channels; W is the master multiplier (UE global grading).")]
        public Vector4 Saturation = Vector4.one;
        public Vector4 Contrast = Vector4.one;
        public Vector4 Gamma = Vector4.one;
        public Vector4 Gain = Vector4.one;
        public Vector4 Offset = Vector4.zero;
    }

    [Serializable]
    public sealed class FilmSettings
    {
        public ToneMappingMode Mode = ToneMappingMode.UEFilm;
        [Range(0.01f, 1f)] public float Slope = 0.88f;
        [Range(0f, 1f)] public float Toe = 0.55f;
        [Range(0f, 1f)] public float Shoulder = 0.26f;
        [Range(0f, 0.5f)] public float BlackClip;
        [Range(0f, 0.5f)] public float WhiteClip = 0.04f;
    }

    [Serializable]
    public sealed class PostProcessProfile
    {
        public ExposureSettings Exposure = new ExposureSettings();
        public BloomSettings Bloom = new BloomSettings();
        public ColorGradingSettings ColorGrading = new ColorGradingSettings();
        public FilmSettings Film = new FilmSettings();
        public bool Dithering = true;
    }

    [CreateAssetMenu(menuName = "MaxSRP/Post Process Configuration")]
    public sealed class MaxPostProcessConfig : ScriptableObject
    {
        [SerializeField] private PostProcessProfile m_pc = new PostProcessProfile();
        [SerializeField] private PostProcessProfile m_mobile = new PostProcessProfile
        {
            Exposure = new ExposureSettings { Mode = ExposureMode.Basic }
        };
        [SerializeField, HideInInspector] private ComputeShader m_exposureCompute;
        [SerializeField, HideInInspector] private Shader m_bloomShader;
        [SerializeField, HideInInspector] private Shader m_outputShader;
        // Preserve the pre-configuration output path used by legacy scene exposure
        // and diagnostics. Its serialized settings live with the output module.
        [SerializeField, HideInInspector] private ColorOutputSettings m_legacyColorOutput = new ColorOutputSettings();

        public PostProcessPlatform Platform => (PostProcessPlatform)MaxRenderQuality.Platform;
        public PostProcessProfile Profile => GetProfile(MaxRenderQuality.Platform);
        public PostProcessProfile GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;
        public ComputeShader ExposureCompute => m_exposureCompute;
        public Shader BloomShader => m_bloomShader;
        public Shader OutputShader => m_outputShader;
        public ColorOutputSettings LegacyOutput => m_legacyColorOutput;

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new PostProcessProfile();
            if (m_mobile == null) m_mobile = new PostProcessProfile { Exposure = new ExposureSettings { Mode = ExposureMode.Basic } };
            if (m_legacyColorOutput == null) m_legacyColorOutput = new ColorOutputSettings();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
