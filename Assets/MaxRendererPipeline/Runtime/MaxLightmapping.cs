using UnityEngine;

#if UNITY_EDITOR
using Unity.Collections;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Rendering;
using GILightmapping = UnityEngine.Experimental.GlobalIllumination.Lightmapping;
#endif

namespace MaxSRP
{
    /// <summary>Units and native baker capabilities shared with MaxSRP's lightmap decoder.</summary>
    public static class MaxLightmapping
    {
        // Submit candela/1000. Native Progressive stores irradiance E; the final
        // receiving Lambert BRDF divides E by pi once (not once per bounce).
        // This keeps native lightmaps away from physical-intensity overflow and tiny normalized values.
        public const float BakeIntensityScale = 1000.0f;
        // Inverse input scale for light-intensity checks and raw-E diagnostics.
        public const float LightmapToPipeline = BakeIntensityScale / LightPhotometry.HDRScale;
        public const float DiffuseLightmapToPipeline = LightmapToPipeline / Mathf.PI;

#if UNITY_EDITOR
        private static readonly GILightmapping.RequestLightsDelegate s_delegate = ExtractLights;
        private static SupportedRenderingFeatures s_previousFeatures;
        private static SupportedRenderingFeatures s_features;

        public static void Setup()
        {
            if (s_features == null)
            {
                s_previousFeatures = SupportedRenderingFeatures.active;
                s_features = new SupportedRenderingFeatures
                {
                    defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly,
                    mixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly,
                    lightmapBakeTypes = LightmapBakeType.Realtime | LightmapBakeType.Mixed,
                    lightmapsModes = LightmapsMode.CombinedDirectional | LightmapsMode.NonDirectional,
                    enlighten = false,
                    autoAmbientProbeBaking = false,
                    autoDefaultReflectionProbeBaking = false,
                    supportsHDR = true
                };
                // Unity requires Baked capability for Mixed to remain supported. Fully baked local
                // lights are intentionally rejected by the delegate until the runtime supports them.
                s_features.lightmapBakeTypes |= LightmapBakeType.Baked;
            }
            SupportedRenderingFeatures.active = s_features;
            GILightmapping.SetDelegate(s_delegate);
        }

        public static void Reset()
        {
            if (GILightmapping.GetDelegate() == s_delegate) GILightmapping.ResetDelegate();
            if (SupportedRenderingFeatures.active == s_features)
                SupportedRenderingFeatures.active = s_previousFeatures;
            s_features = null;
            s_previousFeatures = null;
        }

        public static bool IsConfigured => GILightmapping.GetDelegate() == s_delegate &&
            SupportedRenderingFeatures.active == s_features && s_features != null;

        // Construct LinearColor directly; Convert would apply project-dependent gamma conversion
        // to an already linear, temperature-filtered color.
        public static LinearColor EncodeLinear(Color linear, float intensity)
        {
            float maximum = Mathf.Max(linear.r, Mathf.Max(linear.g, linear.b));
            if (maximum <= 0 || intensity <= 0) return LinearColor.Black();
            return new LinearColor
            {
                red = Mathf.Clamp01(linear.r / maximum),
                green = Mathf.Clamp01(linear.g / maximum),
                blue = Mathf.Clamp01(linear.b / maximum),
                intensity = maximum * intensity
            };
        }

        public static void ExtractLights(Light[] requests, NativeArray<LightDataGI> output)
        {
            for (int i = 0; i < requests.Length; ++i)
            {
                Light light = requests[i];
                var data = new LightDataGI();
                if (light && light.isActiveAndEnabled && light.type == UnityEngine.LightType.Point &&
                    light.lightmapBakeType == LightmapBakeType.Mixed)
                {
                    Color filter = LightPhotometry.LinearFilter(light);
                    float intensity = LightPhotometry.PointCandela(light) / BakeIntensityScale;
                    var point = new PointLight
                    {
                        instanceID = light.GetInstanceID(),
                        mode = LightMode.Mixed,
                        // Bake geometric visibility even though the realtime point path has no shadows.
                        shadow = true,
                        position = light.transform.position,
                        orientation = light.transform.rotation,
                        color = EncodeLinear(filter, intensity),
                        indirectColor = EncodeLinear(filter, intensity * Mathf.Max(0, light.bounceIntensity)),
                        range = light.range,
                        sphereRadius = 0,
                        falloff = FalloffType.InverseSquared
                    };
                    var cookie = new Cookie { instanceID = 0, scale = 1 };
                    data.Init(ref point, ref cookie);
                }
                else data.InitNoBake(light ? light.GetInstanceID() : 0);
                output[i] = data;
            }
        }
#else
        public static void Setup() { }
        public static void Reset() { }
#endif
    }
}
