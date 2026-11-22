using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using System.Threading;
using UnityEditor;
#endif

namespace MaxSRP
{
#if UNITY_EDITOR
    public enum MaxIndirectLightingDebugMode
    {
        [InspectorName("All Enabled")] AllEnabled = 0,
        [InspectorName("Direct Only")] DirectOnly = 1,
        [InspectorName("Sky Only")] SkyOnly = 2,
        [InspectorName("DDGI Only")] DDGIOnly = 3,
        [InspectorName("Lightmap Only")] LightmapOnly = 4,
        [InspectorName("SkyVis Only")] SkyVisOnly = 5
    }
#endif

    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("MaxSRP/Baked Solar TOD")]
    public sealed class MaxBakedSolarGI : MonoBehaviour
    {
        [Tooltip("Baked indirect-irradiance volume. The same volume is sampled throughout the 24-hour clock.")]
        public MaxBakedSolarProbeData Coarse;
        [Tooltip("Directional light used as the solar source and shared shadow settings for the active TOD. Keep it enabled.")]
        public Light Sun;
        [Tooltip("Continuous 24-hour solar clock. Moonlight fades in as the sun descends from 0 to -6 degrees, and fades out in reverse at dawn.")]
        [Range(0.0f, 24.0f)] public float Hour = 12.0f;
        [Tooltip("Gain applied to the baked TOD indirect irradiance. Does not change direct lights, sky, emission or exposure.")]
        [Min(0.0f)] public float Intensity = 1.0f;
#if UNITY_EDITOR
        [HideInInspector, Tooltip("Legacy serialized DDGI-only switch. Use the DDGI Only preset for new authoring.")]
        public bool SolarOnly;

        [Tooltip("Lighting isolation for the active TOD. Only modes exclude other lighting, emission, atmosphere fog and the visible sky. SkyVis displays baked visibility as an unexposed 0-1 mask.")]
        public MaxIndirectLightingDebugMode IndirectLightingDebug = MaxIndirectLightingDebugMode.AllEnabled;
        public bool IsLightingDebugMode => isActiveAndEnabled && IndirectLightingDebug != MaxIndirectLightingDebugMode.AllEnabled;
        public bool IsSkyVisibilityOnly => isActiveAndEnabled && IndirectLightingDebug == MaxIndirectLightingDebugMode.SkyVisOnly;
#endif

        [Header("Moon Lighting")]
        [Tooltip("Nominal fixed-moon illuminance in lux before the solar-altitude fade and the linear RGB color filter. The clear-sky prototype uses 0.2 lux.")]
        [Min(0.0f)] public float MoonIlluminanceLux = 0.2f;
        [ColorUsage(false)] public Color MoonColor = new Color(0.75f, 0.82f, 1.0f);
        [Tooltip("Reference for the real-time moon direction. Snaps to the nearest above-horizon key in Coarse.")]
        [Range(6.0f, 18.0f)] public float MoonBakeHour = 12.0f;
        [Tooltip("Angular radius of the visible moon in degrees.")]
        [Min(0.001f)] public float MoonAngularRadius = 0.26785f;
        [Tooltip("Enable existing cloud rendering. Disabled by default while validating the clear-sky sun/moon transition.")]
        public bool RenderClouds;

        [Header("Ambient Occlusion")]
        [Tooltip("Use the active PC or Mobile AO profile from the MaxSRP pipeline asset.")]
        public bool AmbientOcclusionEnabled;
        [Tooltip("Show linear ambient visibility as grayscale.")]
        public bool AOOnly;

        [Header("Scene Exposure")]
        [Tooltip("Override the pipeline's legacy manual exposure when no post-process configuration is active.")]
        public bool UseSceneExposure;
        [Range(-10.0f, 24.0f)] public float CameraEV100 = 10.5f;
        [Range(-16.0f, 16.0f)] public float ExposureEV;

        [Header("Baked Lighting")]
        [Tooltip("Each array slice stores sky visibility in the matching Unity lightmap UV layout.")]
        public Texture2DArray SkyVisibility;
        [HideInInspector] public MaxBakedLighting.RendererMaterials[] OriginalMaterials = Array.Empty<MaxBakedLighting.RendererMaterials>();
        [HideInInspector] public Material[] GeneratedMaterials = Array.Empty<Material>();
        [TextArea] public string BakeDescription;

        // Gameplay can update the pipeline clock independently of these serialized defaults.
        public float CurrentHour
        {
            get
            {
                if (Application.IsPlaying(gameObject) &&
                    RenderPipelineManager.currentPipeline is MaxRenderPipeline pipeline &&
                    pipeline.IsTimeOfDayAuthor(GetInstanceID()) && pipeline.TryGetTimeOfDayHour(out float hour))
                    return hour;
                return MaxTimeOfDayState.NormalizeHour(Hour);
            }
        }

        public bool IsNight => MaxTimeOfDayState.SolarDirection(CurrentHour).y <= 0.0f;
        public float SolarAltitudeDegrees =>
            Mathf.Asin(Mathf.Clamp(MaxTimeOfDayState.SolarDirection(CurrentHour).y, -1f, 1f)) * Mathf.Rad2Deg;
        public float MoonWeight => MaxTimeOfDayState.EvaluateMoonWeight(SolarAltitudeDegrees);
        public float ResolvedMoonBakeHour => MaxTimeOfDayState.ResolveMoonBakeHour(MoonBakeHour, Coarse);
        public Vector3 MoonDirection => MaxTimeOfDayState.SolarDirection(ResolvedMoonBakeHour);
        public float EffectiveMoonIlluminanceLux => MaxTimeOfDayState.NonNegativeFinite(MoonIlluminanceLux) * MoonWeight;

#if UNITY_EDITOR
        private int m_needsPublication;
#endif

        private void OnEnable()
        {
            MaxBakedLighting.RepairMissingMaterials(OriginalMaterials);
            ApplyToPipeline();
#if UNITY_EDITOR
            EditorApplication.update += PublishValidatedSettings;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= PublishValidatedSettings;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
            MaxTimeOfDay.Withdraw(GetInstanceID());
        }

        /// <summary>Publish changes made to this component's authored fields.</summary>
        public void ApplyToPipeline()
        {
#if UNITY_EDITOR
            Interlocked.Exchange(ref m_needsPublication, 0);
#endif
            if (!isActiveAndEnabled) return;

            Scene scene = gameObject.scene;
            if (!scene.IsValid()) return;
            MaxTimeOfDay.Publish(GetInstanceID(), scene, Application.IsPlaying(gameObject),
                new MaxTimeOfDaySettings(this));
        }

#if UNITY_EDITOR
        // OnValidate can run on Unity's loading thread. Publish on the editor update.
        private void OnValidate() => Interlocked.Exchange(ref m_needsPublication, 1);

        private void PublishValidatedSettings()
        {
            if (Interlocked.Exchange(ref m_needsPublication, 0) == 0) return;
            ApplyToPipeline();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                ApplyToPipeline();
        }
#endif
    }
}
