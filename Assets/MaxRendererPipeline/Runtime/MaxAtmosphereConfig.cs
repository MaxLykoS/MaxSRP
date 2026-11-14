using System;
using UnityEngine;

namespace MaxSRP
{
    // Kept as the atmosphere algorithm's vocabulary. The pipeline selects it.
    public enum AtmospherePlatform { PC = 0, Mobile = 1 }

    [Serializable]
    public sealed class AtmosphereProfile
    {
        public MaxProceduralSkyboxPass.AtmosphereSettings Atmosphere = new MaxProceduralSkyboxPass.AtmosphereSettings();
        public SkyIBLSettings SkyIBL = new SkyIBLSettings();
        [ColorUsage(false, false)]
        [Tooltip("Average planetary ground reflectance. The colour picker is sRGB; converted to linear reflectance for scattering. Black disables ground bounce. UE default is sRGB 170/255, about 0.4 linear.")]
        public Color GroundAlbedo = new Color(170f / 255f, 170f / 255f, 170f / 255f, 1f);
    }

    [CreateAssetMenu(menuName = "MaxSRP/Atmosphere Configuration")]
    public sealed class MaxAtmosphereConfig : ScriptableObject
    {
        [SerializeField, Tooltip("PC parameters. The pipeline selects this profile together with CS phase, analytical feedback, infinite sum, 8-32 sky steps and 32 Multi directions.")]
        private AtmosphereProfile m_pc = new AtmosphereProfile();
        [SerializeField, Tooltip("Mobile parameters. The pipeline selects this profile together with HG phase, UE simple feedback/finite sum and mobile numerical budgets.")]
        private AtmosphereProfile m_mobile = new AtmosphereProfile();

        // Shared resources are serialized once. Both profiles retain the same
        // SkyView-LUT pipeline and one RGBA16F aerial volume with mean transmission.
        [SerializeField, HideInInspector] private ComputeShader m_iblCS;
        [SerializeField, HideInInspector] private ComputeShader m_aerialPerspectiveCompute;
        [SerializeField, HideInInspector] private Shader m_proceduralSkyboxShader;
        [SerializeField, HideInInspector] private Shader m_transmittanceShader;
        [SerializeField, HideInInspector] private Shader m_multiscatteringShader;
        [SerializeField, HideInInspector] private Shader m_skyviewShader;
        [SerializeField, HideInInspector] private Shader m_aerialPerspectiveLUTShader;

        public AtmosphereProfile GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;
        public AtmosphereProfile Profile => GetProfile(MaxRenderQuality.Platform);
        public ComputeShader IBLCS => m_iblCS;
        public ComputeShader AerialPerspectiveCompute => m_aerialPerspectiveCompute;
        public Shader ProceduralSkyboxShader => m_proceduralSkyboxShader;
        public Shader TransmittanceShader => m_transmittanceShader;
        public Shader MultiscatteringShader => m_multiscatteringShader;
        public Shader SkyviewShader => m_skyviewShader;
        public Shader AerialPerspectiveLUTShader => m_aerialPerspectiveLUTShader;

        public static AtmospherePlatform ResolvePlatform(MaxAtmosphereConfig configuration) =>
            MaxRenderQuality.Platform == MaxRenderPlatform.Mobile ? AtmospherePlatform.Mobile : AtmospherePlatform.PC;

        public static int ResolveHistoryKey(MaxAtmosphereConfig configuration)
        {
            unchecked
            {
                int key = (int)ResolvePlatform(configuration);
                key = key * 31 + ResolveGroundAlbedo(configuration).GetHashCode();
                if (!configuration) return key;
                var profile = configuration.Profile;
                var s = profile.Atmosphere;
                key = key * 31 + configuration.GetInstanceID();
                key = key * 31 + s._SeaLevel.GetHashCode();
                key = key * 31 + s._PlanetRadius.GetHashCode();
                key = key * 31 + s._AtmosphereHeight.GetHashCode();
                key = key * 31 + s._SunDiskAngle.GetHashCode();
                key = key * 31 + s._RayleighScatteringScale.GetHashCode();
                key = key * 31 + s._RayleighScatteringScalarHeight.GetHashCode();
                key = key * 31 + s._MieScatteringScale.GetHashCode();
                key = key * 31 + s._MieAnisotropy.GetHashCode();
                key = key * 31 + s._MieScatteringScalarHeight.GetHashCode();
                key = key * 31 + s._OzoneAbsorptionScale.GetHashCode();
                key = key * 31 + s._OzoneLevelCenterHeight.GetHashCode();
                key = key * 31 + s._OzoneLevelWidth.GetHashCode();
                key = key * 31 + (s.EnableAerialPerspective ? 1 : 0);
                key = key * 31 + s._AerialPerspectiveDistance.GetHashCode();
                key = key * 31 + s._AerialPerspectiveStartDepth.GetHashCode();
                key = key * 31 + profile.SkyIBL.ReferenceHeight.GetHashCode();
                key = key * 31 + profile.SkyIBL.SpecularResolution;
                key = key * 31 + profile.SkyIBL.DiffuseSamples;
                return key * 31 + profile.SkyIBL.SpecularSamples;
            }
        }

        public static Color ResolveGroundAlbedo(MaxAtmosphereConfig configuration)
        {
            Color value = configuration ? configuration.Profile.GroundAlbedo :
                new Color(170f / 255f, 170f / 255f, 170f / 255f, 1f);
            value = value.linear;
            return new Color(Mathf.Clamp01(value.r), Mathf.Clamp01(value.g), Mathf.Clamp01(value.b), 1f);
        }

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new AtmosphereProfile();
            if (m_mobile == null) m_mobile = new AtmosphereProfile();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
