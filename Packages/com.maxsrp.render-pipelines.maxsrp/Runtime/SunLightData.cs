using UnityEngine;

namespace MaxSRP
{
    // One snapshot supplies every camera and pass in a render invocation.
    public readonly struct SunLightData
    {
        // All daylight HDR values use the same fixed photometric scale:
        // illuminance / LuxPerUnit, luminance / LuxPerUnit (before exposure).
        public const float LuxPerUnit = LightPhotometry.HDRScale;
        public readonly Light Source;
        public readonly Vector4 Direction;
        public readonly Color LinearColor;
        public bool HasSun => Source != null;
        public Vector4 AtmosphereDirection => HasSun ? Direction : new Vector4(0, 1, 0, 0);
        public bool CastsShadows => HasSun && Source.shadows != LightShadows.None;

        private SunLightData(Light source)
        {
            Source = source;
            Direction = -(Vector4)source.transform.forward;
            // White-light illuminance normal to the beam, above the atmosphere.
            // Light.intensity is explicitly lux, matching the point-light unit conversion.
            LinearColor = LightPhotometry.LinearFilter(source) * (source.intensity / LuxPerUnit);
        }

        private SunLightData(Light source, Vector4 direction, Color linearColor)
        {
            Source = source;
            Direction = direction;
            LinearColor = linearColor;
        }

        internal SunLightData WithDirection(Vector3 direction) =>
            new SunLightData(Source, direction, LinearColor);

        internal SunLightData WithLighting(Vector3 direction, Color linearColor) =>
            new SunLightData(Source, direction, linearColor);

        public static SunLightData Capture()
        {
            Light sun = RenderSettings.sun;
            return sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional &&
                sun.intensity > 0.0f ? new SunLightData(sun) : default;
        }
    }
}
