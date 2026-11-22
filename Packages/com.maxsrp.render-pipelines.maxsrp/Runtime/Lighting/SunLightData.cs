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
        public bool CastsShadows => HasSun && Source.shadows != LightShadows.None &&
            Mathf.Max(LinearColor.r, Mathf.Max(LinearColor.g, LinearColor.b)) > 0.0f;

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

        public static SunLightData Capture() => Capture(RenderSettings.sun);

        public static SunLightData Capture(Light sun) =>
            sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional &&
                sun.intensity > 0.0f ? new SunLightData(sun) : default;
    }

    // Celestial source energy is independent of the single shadowed surface light.
    // Sun and Moon always retain their own direction/color for atmosphere and GI.
    public readonly struct CelestialLightData
    {
        public readonly SunLightData Sun;
        public readonly SunLightData Moon;
        public readonly SunLightData MainLight;
        public readonly float MoonWeight;
        public readonly float MoonAngularRadius; // degrees
        public readonly bool IsMoonMain;

        public CelestialLightData(SunLightData sun, SunLightData moon, SunLightData mainLight,
            float moonWeight, float moonAngularRadius, bool isMoonMain)
        {
            Sun = sun;
            Moon = moon;
            MainLight = mainLight;
            MoonWeight = moonWeight;
            MoonAngularRadius = moonAngularRadius;
            IsMoonMain = isMoonMain;
        }

        public static CelestialLightData FromSun(SunLightData sun) =>
            new CelestialLightData(sun, default, sun, 0.0f, 0.26785f, false);
    }
}
