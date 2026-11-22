using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public enum ShadowMapResolution { Resolution512 = 512, Resolution1024 = 1024, Resolution2048 = 2048 }

    internal static class MaxShadowShaderProperties
    {
        internal static readonly int Atlas = Shader.PropertyToID("_MainShadowAtlas");
        internal static readonly int Available = Shader.PropertyToID("_MainLightShadowsAvailable");
        internal static readonly int[] ShadingPointNormalBias = Create("_ShadingPointNormalBias");
        internal static readonly int[] DepthNormalBias = Create("_DepthNormalBias");
        internal static readonly int[] AtlasTile = Create("_ShadowAtlasTile");
        internal static readonly int[] Resolution = Create("_ShadowResolution");
        internal static readonly int[] ViewProjection = Create("_ShadowVPMatrix");
        internal static readonly int[] DepthHalfRange = Create("_ShadowDepthHalfRange");
        internal static readonly int[] OrthoWidth = Create("_OrthoWidth");

        private static int[] Create(string prefix)
        {
            var ids = new int[4];
            for (int i = 0; i < ids.Length; ++i) ids[i] = Shader.PropertyToID(prefix + i);
            return ids;
        }
    }

    [System.Serializable]
    public class CascadeSettings
    {
        [Header("Cascade Projection and Filtering")]
        public float ShadowDistance = 50;
        [Range(1.0f, 4.0f), Tooltip("Ratio of consecutive cascade depth-slice lengths (UE-style geometric distribution). 1 is uniform; 2 uses 1:2:4:8. Larger values reserve more detail near the camera.")]
        public float DistributionExponent = 2.0f;
        [Range(0.0f, 0.3f), Tooltip("Fraction of the smaller adjacent depth slice used to blend cascades.")]
        public float CascadeBlendFraction = 0.1f;
        [Range(0.0f, 0.5f), Tooltip("Fraction of the last cascade's depth slice used to fade shadows out.")]
        public float DistanceFadeFraction = 0.1f;
        public ShadowSettings Cascade0 = new ShadowSettings();
        public ShadowSettings Cascade1 = new ShadowSettings { MapResolution = ShadowMapResolution.Resolution2048 };
        public ShadowSettings Cascade2 = new ShadowSettings();
        public ShadowSettings Cascade3 = new ShadowSettings();

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

        public void Set() => Set(null);

        internal void Set(CommandBuffer cmd)
        {
            for (int i = 0; i < 4; ++i)
            {
                ShadowSettings cascade = GetCascade(i);
                if (cmd == null)
                {
                    Shader.SetGlobalFloat(MaxShadowShaderProperties.ShadingPointNormalBias[i], cascade.ShadingPointNormalBias);
                    Shader.SetGlobalFloat(MaxShadowShaderProperties.DepthNormalBias[i], cascade.DepthNormalBias);
                }
                else
                {
                    cmd.SetGlobalFloat(MaxShadowShaderProperties.ShadingPointNormalBias[i], cascade.ShadingPointNormalBias);
                    cmd.SetGlobalFloat(MaxShadowShaderProperties.DepthNormalBias[i], cascade.DepthNormalBias);
                }
            }
        }
    }

    [System.Serializable]
    public class ShadowSettings
    {
        [Tooltip("Tile size including a two-texel border per side. Four 2048 tiles fit a single 4096 atlas; effective shadow resolution is tile size minus four.")]
        public ShadowMapResolution MapResolution = ShadowMapResolution.Resolution2048;
        [Min(0.0f), Tooltip("World-space offset along the geometric receiver normal; normal maps do not displace the shadow query.")]
        public float ShadingPointNormalBias = 0.1f;
        [Min(0.0f), Tooltip("Receiver depth bias in world units; scales up at grazing angles.")]
        public float DepthNormalBias = 0.005f;
    }
}
