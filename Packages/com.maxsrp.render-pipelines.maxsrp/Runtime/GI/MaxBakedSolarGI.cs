using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("MaxSRP/Baked Solar TOD")]
    public sealed class MaxBakedSolarGI : MonoBehaviour
    {
        public MaxBakedSolarProbeData Coarse;
        public MaxBakedSolarProbeData Fine;
        [Tooltip("Shared directional-light source for the sun and moon. Keep it enabled; night uses the moon settings below.")]
        public Light Sun;
        [Tooltip("24-hour clock. Night (18:00-06:00) reuses the daytime probe trajectory, offset by twelve hours.")]
        [Range(0.0f, 24.0f)] public float Hour = 12.0f;
        [Min(0.0f)] public float Intensity = 1.0f;
        [Tooltip("Display only the diffuse sunlight bounced from the baked probe field.")]
        public bool SolarOnly;

        [Header("Moon Lighting")]
        [Min(0.0f)] public float MoonIlluminanceLux = 0.2f;
        [ColorUsage(false)] public Color MoonColor = new Color(0.75f, 0.82f, 1.0f);

        [Header("Fine Probe Receiver Override")]
        [Tooltip("World-space center of the receiver region.")]
        public Vector3 FineReceiverCenter;
        [Tooltip("Size of the receiver region along its local axes, in meters.")]
        public Vector3 FineReceiverSize;
        public float FineReceiverYaw;
        [Tooltip("Fraction of the fine probe blend retained on upward-facing surfaces in the receiver region.")]
        [Range(0f, 1f)] public float FineReceiverWeight = 1f;

        private static readonly List<MaxBakedSolarGI> Active = new List<MaxBakedSolarGI>();
        private static readonly int SettingsId = Shader.PropertyToID("_MaxSolarProbeSettings");
        private float ClockHour => float.IsNaN(Hour) || float.IsInfinity(Hour) ? 12.0f : Mathf.Repeat(Hour, 24.0f);
        public bool IsNight => ClockHour < 6.0f || ClockHour >= 18.0f;
        private float SolarHour => 6.0f + Mathf.Repeat(ClockHour - 6.0f, 12.0f);
        private MaxSolarIrradianceStream m_coarseStream;
        private MaxSolarIrradianceStream m_fineStream;
        private int m_streamRevision;

        private void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += UpdateEditorStreams;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeStreams;
            UnityEditor.EditorApplication.playModeStateChanged += OnStreamPlayModeChanged;
#endif
        }

        private void OnDisable()
        {
            Active.Remove(this);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= UpdateEditorStreams;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DisposeStreams;
            UnityEditor.EditorApplication.playModeStateChanged -= OnStreamPlayModeChanged;
#endif
            DisposeStreams();
        }

        private void Update()
        {
            if (Application.IsPlaying(gameObject))
                UpdateStreams();
        }

#if UNITY_EDITOR
        private void UpdateEditorStreams()
        {
            if (this && isActiveAndEnabled && !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode &&
                gameObject.scene.isLoaded)
                UpdateStreams();
        }

        private void OnStreamPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingEditMode ||
                state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                DisposeStreams();
        }
#endif

        private void UpdateStreams()
        {
            float hour = SolarHour;
            bool changed = TickStream(Coarse, ref m_coarseStream, hour);
            changed |= TickStream(Fine, ref m_fineStream, hour);
            if (!changed) return;
            unchecked { ++m_streamRevision; }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }

        private static bool TickStream(MaxBakedSolarProbeData volume, ref MaxSolarIrradianceStream stream, float hour)
        {
            bool usesStream = volume && volume.UsesIrradianceStream;
            bool changed = false;
            if (stream != null && (!usesStream || !stream.MatchesSourceConfiguration(volume)))
            {
                stream.Dispose();
                stream = null;
                changed = true;
            }
            if (usesStream)
            {
                if (stream == null)
                    stream = new MaxSolarIrradianceStream(volume);
                changed |= stream.Tick(hour);
            }
            return changed;
        }

        private void DisposeStreams()
        {
            if (m_coarseStream == null && m_fineStream == null) return;
            m_coarseStream?.Dispose();
            m_fineStream?.Dispose();
            m_coarseStream = null;
            m_fineStream = null;
            unchecked { ++m_streamRevision; }
        }

        internal static MaxBakedSolarGI FindForCamera(Camera camera)
        {
            if (!camera || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
                return null;
            Scene scene = camera.cameraType == CameraType.SceneView
                ? SceneManager.GetActiveScene() : camera.gameObject.scene;
            MaxBakedSolarGI chosen = null;
            for (int i = Active.Count - 1; i >= 0; --i)
            {
                var entry = Active[i];
                if (!entry) { Active.RemoveAt(i); continue; }
                if (!entry.isActiveAndEnabled || entry.gameObject.scene != scene) continue;
                if (!chosen || entry.GetInstanceID() < chosen.GetInstanceID()) chosen = entry;
            }
            return chosen;
        }

        internal SunLightData AlignSun(SunLightData sun)
        {
            if (!sun.HasSun || sun.Source != Sun) return sun;

            // Match the NativeGI bake's apparent solar hour and world axes (X east, Z north).
            const float latitude = 39.9f * Mathf.Deg2Rad;
            const float declination = -20.0f * Mathf.Deg2Rad;
            float angle = (SolarHour - 12.0f) * (Mathf.PI / 12.0f);
            float cosDeclination = Mathf.Cos(declination);
            float cosAngle = Mathf.Cos(angle);
            Vector3 direction = new Vector3(
                -cosDeclination * Mathf.Sin(angle),
                Mathf.Sin(latitude) * Mathf.Sin(declination) + Mathf.Cos(latitude) * cosDeclination * cosAngle,
                Mathf.Cos(latitude) * Mathf.Sin(declination) - Mathf.Sin(latitude) * cosDeclination * cosAngle
            ).normalized;
            if (!IsNight) return sun.WithDirection(direction);

            float moonLux = MoonIlluminanceLux > 0.0f && !float.IsInfinity(MoonIlluminanceLux)
                ? MoonIlluminanceLux : 0.0f;
            // The unit-white RGB bake is linear in source illumination. Use the same
            // light snapshot for GI, direct light, atmosphere, clouds and sky IBL.
            return sun.WithLighting(direction, MoonColor.linear * (moonLux / SunLightData.LuxPerUnit));
        }

        internal int HistoryKey
        {
            get
            {
                unchecked
                {
                    int key = GetInstanceID();
                    key = key * 31 + (Coarse ? Coarse.GetInstanceID() : 0);
                    key = key * 31 + (Fine ? Fine.GetInstanceID() : 0);
                    key = key * 31 + SolarHour.GetHashCode();
                    key = key * 31 + IsNight.GetHashCode();
                    key = key * 31 + MoonIlluminanceLux.GetHashCode();
                    key = key * 31 + MoonColor.GetHashCode();
                    key = key * 31 + Intensity.GetHashCode();
                    key = key * 31 + FineReceiverCenter.GetHashCode();
                    key = key * 31 + FineReceiverSize.GetHashCode();
                    key = key * 31 + FineReceiverYaw.GetHashCode();
                    key = key * 31 + FineReceiverWeight.GetHashCode();
                    key = key * 31 + m_streamRevision;
                    return key * 31 + (SolarOnly ? 1 : 0);
                }
            }
        }

        internal static void Bind(MaterialPropertyBlock properties, MaxBakedSolarGI preview, SunLightData sun)
        {
            Vector4 settings = Vector4.zero;
            Vector4 receiverCenter = new Vector4(0f, 0f, 0f, 1f);
            Vector4 receiverExtents = Vector4.zero;
            Vector4 receiverForward = Vector4.zero;
            if (preview && sun.HasSun && sun.Source == preview.Sun)
            {
                float gain = preview.Intensity > 0.0f && !float.IsInfinity(preview.Intensity)
                    ? preview.Intensity : 0.0f;
                // Match the bake's horizon cutoff; this field contains no sky bounce.
                if (sun.Direction.y <= 0.000001f) gain = 0.0f;
                if (gain > 0.0f)
                {
                    float hour = preview.SolarHour;
                    if (BindVolume(preview.Coarse, preview.m_coarseStream, properties, "_MaxSolarCoarse", hour)) settings.x = 1.0f;
                    if (BindVolume(preview.Fine, preview.m_fineStream, properties, "_MaxSolarFine", hour)) settings.y = 1.0f;
                    settings.z = gain;
                    if (settings.x > 0f && settings.y > 0f && preview.FineReceiverWeight < 1f &&
                        preview.FineReceiverSize.x > 0f && preview.FineReceiverSize.y > 0f && preview.FineReceiverSize.z > 0f)
                    {
                        Vector3 center = preview.FineReceiverCenter;
                        receiverCenter = new Vector4(center.x, center.y, center.z, Mathf.Clamp01(preview.FineReceiverWeight));
                        receiverExtents = preview.FineReceiverSize * 0.5f;
                        float yaw = preview.FineReceiverYaw * Mathf.Deg2Rad;
                        receiverForward = new Vector4(Mathf.Sin(yaw), Mathf.Cos(yaw), 0f, 0f);
                    }
                }
                settings.w = preview.SolarOnly ? 1.0f : 0.0f;
            }
            properties.SetVector(SettingsId, settings);
            properties.SetVector("_MaxSolarFineReceiverCenter", receiverCenter);
            properties.SetVector("_MaxSolarFineReceiverExtents", receiverExtents);
            properties.SetVector("_MaxSolarFineReceiverForward", receiverForward);
        }

        private static bool BindVolume(MaxBakedSolarProbeData volume, MaxSolarIrradianceStream stream,
            MaterialPropertyBlock properties, string prefix, float hour)
        {
            return volume && (volume.UsesIrradianceStream
                ? volume.BindStreaming(properties, prefix, hour, stream)
                : volume.Bind(properties, prefix, hour));
        }
    }
}
