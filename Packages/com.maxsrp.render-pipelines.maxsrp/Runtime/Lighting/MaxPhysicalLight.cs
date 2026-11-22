using UnityEngine;

namespace MaxSRP
{
    public enum PointLightUnit { Candela, Lumen }

    // Unit metadata only. Intensity, color, range and enable state belong to Light.
    [DisallowMultipleComponent, RequireComponent(typeof(Light))]
    [AddComponentMenu("MaxSRP/Physical Light")]
    public sealed class MaxPhysicalLight : MonoBehaviour
    {
        [Tooltip("Unit of Light.intensity for point lights. Directional lights always use lux.")]
        public PointLightUnit PointUnit = PointLightUnit.Candela;
    }

    public static class LightPhotometry
    {
        // Shared by all light inputs, sky/fog/IBL and physical camera exposure.
        public const float HDRScale = 100000.0f;

        public static float PointCandela(Light light)
        {
            float intensity = Mathf.Max(0.0f, light.intensity);
            return light.TryGetComponent<MaxPhysicalLight>(out var data) && data.PointUnit == PointLightUnit.Lumen
                ? intensity / (4.0f * Mathf.PI) : intensity;
        }

        public static Color LinearFilter(Light light)
        {
            Color filter = light.color.linear;
            // Unity's temperature conversion already returns linear RGB.
            if (light.useColorTemperature)
                filter *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
            return filter;
        }

        public static Color PointColor(Light light)
        {
            return LinearFilter(light) * (PointCandela(light) / HDRScale);
        }
    }
}
