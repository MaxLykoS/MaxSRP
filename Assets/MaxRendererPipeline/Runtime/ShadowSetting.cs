using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace MaxSRP
{
    public enum PcssSampleCount { Samples16 = 16, Samples32 = 32, Samples64 = 64 }

    [System.Serializable]
    public class CascadeSettings
    {
        public float ShadowDistance = 50;
        public PcssSampleCount BlockerSamples = PcssSampleCount.Samples16;
        public PcssSampleCount FilterSamples = PcssSampleCount.Samples32;
        [Range(0.0f, 0.3f), Tooltip("Fraction of the smaller adjacent depth slice used to blend cascades.")]
        public float CascadeBlendFraction = 0.1f;
        [Range(0.0f, 0.5f), Tooltip("Fraction of the last cascade's depth slice used to fade shadows out.")]
        public float DistanceFadeFraction = 0.1f;
        public ShadowSettings Cascade0 = new ShadowSettings();
        public ShadowSettings Cascade1 = new ShadowSettings();
        public ShadowSettings Cascade2 = new ShadowSettings();
        public ShadowSettings Cascade3 = new ShadowSettings();

        public Texture2D BlueNoiseTexture;

        public ShadowSettings GetCascade(int cascade)
        {
            switch (cascade)
            {
                case 0: return Cascade0;
                case 1: return Cascade1;
                case 2: return Cascade2;
                case 3: return Cascade3;
                default: throw new System.ArgumentOutOfRangeException(nameof(cascade));
            }
        }

        public void Set()
        {
            // Rebind per camera: an Editor reimport can invalidate a constructor-only global binding.
            Texture2D noise = BlueNoiseTexture != null ? BlueNoiseTexture : Texture2D.grayTexture;
            Shader.SetGlobalTexture("_NoiseTexture", noise);
            // Match the HLSL vector type, including non-square replacement textures.
            // A new name avoids old integer global slots retained across Editor reloads.
            Shader.SetGlobalVector("_ShadowNoiseTextureSize", new Vector4(noise.width, noise.height, 0, 0));
            Shader.SetGlobalInteger("_PcssBlockerSamples", SanitizeSampleCount((int)BlockerSamples));
            Shader.SetGlobalInteger("_PcssFilterSamples", SanitizeSampleCount((int)FilterSamples));
            int i = 0;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade0.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade0.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssMaxRadius" + i, Mathf.Max(Cascade0.MaxPenumbraRadius, 0.0f));

            i = 1;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade1.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade1.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssMaxRadius" + i, Mathf.Max(Cascade1.MaxPenumbraRadius, 0.0f));

            i = 2;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade2.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade2.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssMaxRadius" + i, Mathf.Max(Cascade2.MaxPenumbraRadius, 0.0f));

            i = 3;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade3.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade3.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssMaxRadius" + i, Mathf.Max(Cascade3.MaxPenumbraRadius, 0.0f));
        }

        public static int SanitizeSampleCount(int count)
        {
            return count >= 64 ? 64 : count >= 32 ? 32 : 16;
        }
    }

    [System.Serializable]
    public class ShadowSettings
    {
        public float ShadingPointNormalBias = 0.1f;
        [Min(0.0f), Tooltip("Receiver depth bias in world units; scales up at grazing angles.")]
        public float DepthNormalBias = 0.005f;
        [FormerlySerializedAs("PcssSearchRadius"), Min(0.0f),
            Tooltip("Maximum directional PCSS kernel radius in world units (metres). Caps both blocker search and filtering; actual softness follows the sky solar angular radius. Zero selects a single hardware PCF comparison sample.")]
        public float MaxPenumbraRadius = 1.0f;
    }
}
