using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Copy authored values on publication. Unity asset references are shared.
    internal readonly struct MaxTimeOfDaySettings
    {
        internal readonly MaxBakedSolarProbeData Coarse;
        internal readonly Light Sun;
        internal readonly float Hour;
        internal readonly float Intensity;
#if UNITY_EDITOR
        internal readonly bool SolarOnly;
        internal readonly MaxIndirectLightingDebugMode IndirectLightingDebug;
#endif
        internal readonly float MoonIlluminanceLux;
        internal readonly Color MoonColor;
        internal readonly float MoonBakeHour;
        internal readonly float MoonAngularRadius;
        internal readonly bool RenderClouds;
        internal readonly bool AmbientOcclusionEnabled;
        internal readonly bool AOOnly;
        internal readonly bool UseSceneExposure;
        internal readonly float CameraEV100;
        internal readonly float ExposureEV;
        internal readonly Texture2DArray SkyVisibility;

        internal MaxTimeOfDaySettings(MaxBakedSolarGI author)
        {
            Coarse = author.Coarse;
            Sun = author.Sun;
            Hour = author.Hour;
            Intensity = author.Intensity;
#if UNITY_EDITOR
            SolarOnly = author.SolarOnly;
            IndirectLightingDebug = author.IndirectLightingDebug;
#endif
            MoonIlluminanceLux = author.MoonIlluminanceLux;
            MoonColor = author.MoonColor;
            MoonBakeHour = author.MoonBakeHour;
            MoonAngularRadius = author.MoonAngularRadius;
            RenderClouds = author.RenderClouds;
            AmbientOcclusionEnabled = author.AmbientOcclusionEnabled;
            AOOnly = author.AOOnly;
            UseSceneExposure = author.UseSceneExposure;
            CameraEV100 = author.CameraEV100;
            ExposureEV = author.ExposureEV;
            SkyVisibility = author.SkyVisibility;
        }
    }

    // The pipeline's current world lighting, clock and probe residency.
    internal sealed class MaxTimeOfDayState
    {
        internal readonly int AuthorId;
        internal bool IsPlaying { get; set; }
        internal float ClockHour { get; private set; }
        internal MaxDDGIPass.Streams ProbeStreams { get; } = new MaxDDGIPass.Streams();
        internal MaxBakedSolarProbeData Coarse => m_settings.Coarse;
        internal Light Sun => m_settings.Sun;
        internal float Intensity => m_settings.Intensity;
        internal float MoonIlluminanceLux => m_settings.MoonIlluminanceLux;
        internal Color MoonColor => m_settings.MoonColor;
        internal float MoonAngularRadius => m_settings.MoonAngularRadius;
        internal bool RenderClouds => m_settings.RenderClouds;
        internal bool AmbientOcclusionEnabled => m_settings.AmbientOcclusionEnabled;
        internal bool AOOnly => m_settings.AOOnly;
        internal bool UseSceneExposure => m_settings.UseSceneExposure;
        internal float CameraEV100 => m_settings.CameraEV100;
        internal float ExposureEV => m_settings.ExposureEV;
        internal Texture2DArray SkyVisibility => m_settings.SkyVisibility;

#if UNITY_EDITOR
        internal static readonly int IndirectDebugId = Shader.PropertyToID("_MaxIndirectLightingDebug");
        internal bool IsLightingDebugMode => m_settings.IndirectLightingDebug != MaxIndirectLightingDebugMode.AllEnabled;
        internal bool IsSkyVisibilityOnly => m_settings.IndirectLightingDebug == MaxIndirectLightingDebugMode.SkyVisOnly;
        internal bool SolarOnly => m_settings.SolarOnly;
#endif

        private MaxTimeOfDaySettings m_settings;

        internal MaxTimeOfDayState(int authorId, MaxTimeOfDaySettings settings)
        {
            AuthorId = authorId;
            m_settings = settings;
            SetHour(settings.Hour);
        }

        internal void ApplySettings(MaxTimeOfDaySettings settings)
        {
            if (!m_settings.Hour.Equals(settings.Hour))
                SetHour(settings.Hour);
            m_settings = settings;
        }

        internal void SetHour(float hour) => ClockHour = NormalizeHour(hour);

        internal static float NormalizeHour(float hour) =>
            float.IsNaN(hour) || float.IsInfinity(hour) ? 12.0f : Mathf.Repeat(hour, 24.0f);

        internal static MaxTimeOfDayState Current =>
            (RenderPipelineManager.currentPipeline as MaxRenderPipeline)?.TimeOfDay;

        internal static MaxTimeOfDayState FindForComponent(MaxBakedSolarGI component)
        {
            MaxTimeOfDayState current = Current;
            return current != null && component && component.isActiveAndEnabled &&
                current.AuthorId == component.GetInstanceID() ? current : null;
        }

        internal CelestialLightData EvaluateLights(SunLightData source)
        {
            float hour = ClockHour;
            if (!source.HasSun || source.Source != Sun)
                return CelestialLightData.FromSun(source);

            Vector3 direction = SolarDirection(hour);
            SunLightData sun = source.WithDirection(direction);
            float altitude = Mathf.Asin(Mathf.Clamp(direction.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;
            float weight = EvaluateMoonWeight(altitude);
            Vector3 moonDirection = SolarDirection(ResolveMoonBakeHour(m_settings.MoonBakeHour, Coarse));
            float moonLux = NonNegativeFinite(MoonIlluminanceLux);
            Color filter = new Color(NonNegativeFinite(MoonColor.r), NonNegativeFinite(MoonColor.g),
                NonNegativeFinite(MoonColor.b), 1.0f).linear;
            SunLightData moon = source.WithLighting(moonDirection,
                filter * (moonLux * weight / SunLightData.LuxPerUnit));
            float radius = float.IsNaN(MoonAngularRadius) || float.IsInfinity(MoonAngularRadius)
                ? 0.26785f : Mathf.Clamp(MoonAngularRadius, 0.001f, 89.0f);
            bool isMoonMain = direction.y <= 0.0f;
            return new CelestialLightData(sun, moon, isMoonMain ? moon : sun, weight, radius, isMoonMain);
        }

        internal static Vector3 SolarDirection(float hour)
        {
            // Match the NativeGI bake's apparent solar hour and world axes (X east, Z north).
            const float latitude = 39.9f * Mathf.Deg2Rad;
            const float declination = -20.0f * Mathf.Deg2Rad;
            float angle = (hour - 12.0f) * (Mathf.PI / 12.0f);
            float cosDeclination = Mathf.Cos(declination);
            float cosAngle = Mathf.Cos(angle);
            return new Vector3(
                -cosDeclination * Mathf.Sin(angle),
                Mathf.Sin(latitude) * Mathf.Sin(declination) + Mathf.Cos(latitude) * cosDeclination * cosAngle,
                Mathf.Cos(latitude) * Mathf.Sin(declination) - Mathf.Sin(latitude) * cosDeclination * cosAngle
            ).normalized;
        }

        internal static float NonNegativeFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) ? Mathf.Max(0.0f, value) : 0.0f;

        internal static float EvaluateMoonWeight(float solarAltitude)
        {
            float t = Mathf.Clamp01(-solarAltitude / 6.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        internal static float ResolveMoonBakeHour(float bakeHour, MaxBakedSolarProbeData volume)
        {
            float requested = float.IsNaN(bakeHour) || float.IsInfinity(bakeHour)
                ? 12.0f : Mathf.Clamp(bakeHour, 6.0f, 18.0f);
            if (!volume || volume.Hours == null) return 12.0f;
            float closest = 12.0f, distance = float.PositiveInfinity;
            foreach (float key in volume.Hours)
            {
                if (float.IsNaN(key) || float.IsInfinity(key) || key < 6.0f || key > 18.0f ||
                    SolarDirection(key).y <= 0.000001f) continue;
                float delta = Mathf.Abs(key - requested);
                if (delta < distance) { closest = key; distance = delta; }
            }
            return closest;
        }

#if UNITY_EDITOR
        // XYZ disable sky/DDGI/baked diffuse. W selects exclusive display;
        // zero retains the normal path and the old internal disabled-flag ABI.
        internal static Vector4 GetIndirectLightingDebugSettings(MaxTimeOfDayState state)
        {
            if (state == null) return Vector4.zero;
            switch (state.m_settings.IndirectLightingDebug)
            {
                case MaxIndirectLightingDebugMode.DirectOnly: return new Vector4(1f, 1f, 1f, 1f);
                case MaxIndirectLightingDebugMode.SkyOnly: return new Vector4(0f, 1f, 1f, 2f);
                case MaxIndirectLightingDebugMode.DDGIOnly: return new Vector4(1f, 0f, 1f, 3f);
                case MaxIndirectLightingDebugMode.LightmapOnly: return new Vector4(1f, 1f, 0f, 4f);
                case MaxIndirectLightingDebugMode.SkyVisOnly: return new Vector4(1f, 1f, 1f, 5f);
                default: return Vector4.zero;
            }
        }

        internal static void BindIndirectLightingDebug(CommandBuffer commands, MaxTimeOfDayState state)
        {
            commands.SetGlobalVector(IndirectDebugId, GetIndirectLightingDebugSettings(state));
        }
#endif
    }
}
