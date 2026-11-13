using UnityEngine;

namespace MaxSRP
{
    public enum AtmospherePlatform
    {
        PC = 0,
        Mobile = 1
    }

    [CreateAssetMenu(menuName = "MaxSRP/Atmosphere Configuration")]
    public sealed class MaxAtmosphereConfig : ScriptableObject
    {
        [SerializeField, Tooltip("Both profiles use SkyView LUTs and one RGBA16F aerial volume with mean transmittance. PC: CS phase, analytical feedback, infinite sum, 8-32 sky steps and 32 Multi directions. Mobile: HG phase, UE simple feedback/finite sum and mobile budgets.")]
        private AtmospherePlatform m_platform = AtmospherePlatform.PC;

        public static AtmospherePlatform ResolvePlatform(MaxAtmosphereConfig configuration) =>
            configuration && configuration.m_platform == AtmospherePlatform.Mobile
                ? AtmospherePlatform.Mobile : AtmospherePlatform.PC;

        public static int ResolveHistoryKey(MaxAtmosphereConfig configuration) =>
            unchecked(ResolveGroundAlbedo(configuration).GetHashCode() * 31 + (int)ResolvePlatform(configuration));

        [SerializeField, ColorUsage(false, false)]
        [Tooltip("Average planetary ground reflectance. The colour picker is sRGB; converted to linear reflectance for atmospheric scattering. Black disables ground bounce. UE default is sRGB 170/255, about 0.4 linear.")]
        private Color m_groundAlbedo = new Color(170f / 255f, 170f / 255f, 170f / 255f, 1f);

        public static Color ResolveGroundAlbedo(MaxAtmosphereConfig configuration)
        {
            Color value = configuration ? configuration.m_groundAlbedo :
                new Color(170f / 255f, 170f / 255f, 170f / 255f, 1f);
            value = value.linear;
            return new Color(Mathf.Clamp01(value.r), Mathf.Clamp01(value.g), Mathf.Clamp01(value.b), 1f);
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
#endif
        }
    }
}
