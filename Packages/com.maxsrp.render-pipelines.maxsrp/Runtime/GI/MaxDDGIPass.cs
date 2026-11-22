using System;
using Unity.Profiling;
using UnityEngine;

namespace MaxSRP
{
    internal sealed class MaxDDGIPass : MaxRenderPass
    {
        private static readonly ProfilerMarker s_prepareMarker = new ProfilerMarker("MaxSRP.DDGI");
        private static readonly int SettingsId = Shader.PropertyToID("_MaxDDGISettings");
        private static readonly int LightColorId = Shader.PropertyToID("_MaxDDGILightColor");
        private readonly float m_samplingSpacing;

        internal MaxDDGIPass(float samplingSpacing) => m_samplingSpacing = samplingSpacing;

        internal void Execute(MaxTimeOfDayState state)
        {
            if (state == null) return;
            using (s_prepareMarker.Auto()) state.ProbeStreams.Tick(state, state.IsPlaying);
        }

        protected override void ReleaseResources() { }

        // The pipeline's scene state retains residency when passes are rebuilt.
        internal sealed class Streams : IDisposable
        {
            private int m_lastPlayFrame = -1;
            internal MaxSolarIrradianceStream Coarse { get; private set; }
            internal bool IsLoading => Coarse?.IsLoading ?? false;

            internal void Tick(MaxTimeOfDayState owner, bool isPlaying)
            {
                if (isPlaying)
                {
                    int frame = Time.frameCount;
                    if (m_lastPlayFrame == frame) return;
                    m_lastPlayFrame = frame;
                }

                MaxBakedSolarProbeData volume = owner.Coarse;
                bool usesStream = volume && volume.UsesIrradianceStream;
                if (Coarse != null && (!usesStream || !Coarse.MatchesSourceConfiguration(volume)))
                {
                    Coarse.Dispose();
                    Coarse = null;
                }
                if (usesStream)
                {
                    Coarse ??= new MaxSolarIrradianceStream(volume);
                    Coarse.Tick(owner.ClockHour);
                }
            }

            public void Dispose()
            {
                m_lastPlayFrame = -1;
                Coarse?.Dispose();
                Coarse = null;
            }
        }

        internal void BindLighting(MaterialPropertyBlock properties, MaxTimeOfDayState state, CelestialLightData lights)
        {
#if UNITY_EDITOR
            Vector4 indirectDebug = MaxTimeOfDayState.GetIndirectLightingDebugSettings(state);
            properties.SetVector(MaxTimeOfDayState.IndirectDebugId, indirectDebug);
#endif
            Vector4 settings = Vector4.zero;
            Color lightColor = Color.clear;
            if (state != null && lights.Sun.HasSun && lights.Sun.Source == state.Sun)
            {
                float gain = MaxTimeOfDayState.NonNegativeFinite(state.Intensity);
                bool bindProbes = gain > 0.0f;
#if UNITY_EDITOR
                bindProbes &= indirectDebug.y < 0.5f;
#endif
                if (bindProbes && BindVolume(state.Coarse, state.ProbeStreams.Coarse, properties, state.ClockHour))
                {
                    settings.x = 1.0f;
                    settings.z = gain;
                    // The TOD bake uses a unit-white source at every key.
                    lightColor = lights.Sun.LinearColor;
                }
#if UNITY_EDITOR
                settings.w = state.SolarOnly && !state.IsLightingDebugMode ? 1.0f : 0.0f;
#endif
            }
            properties.SetVector(SettingsId, settings);
            properties.SetVector(LightColorId, lightColor);
        }

        private bool BindVolume(MaxBakedSolarProbeData volume, MaxSolarIrradianceStream stream,
            MaterialPropertyBlock properties, float hour)
        {
            if (!volume) return false;
            Vector3 stride = SamplingStride(volume);
            return volume.UsesIrradianceStream
                ? volume.BindStreaming(properties, hour, stream, stride)
                : volume.Bind(properties, hour, stride);
        }

        private Vector3 SamplingStride(MaxBakedSolarProbeData volume)
        {
            if (m_samplingSpacing <= 0f) return Vector3.one;
            return new Vector3(
                AxisStride(volume.Spacing.x, volume.Counts.x),
                AxisStride(volume.Spacing.y, volume.Counts.y),
                AxisStride(volume.Spacing.z, volume.Counts.z));
        }

        private float AxisStride(float bakedSpacing, int count) => bakedSpacing > 0f
            ? Mathf.Clamp(Mathf.Round(m_samplingSpacing / bakedSpacing), 1f, Mathf.Max(1, count - 1))
            : 1f;
    }
}
