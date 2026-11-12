using System;
using System.IO;
using UnityEngine;

namespace MaxSRP.DDGIBaker.Editor
{
    internal enum DdgiBakeMaterialMode
    {
        SceneMaterials,
        GrayNativeParity
    }

    [Serializable]
    internal sealed class DdgiBakeSettings
    {
        [Tooltip("Use scene textures for diffuse transport. Gray Native Parity is for the NativeGI comparison.")]
        public DdgiBakeMaterialMode materialMode;
        [Tooltip("Use a large single volume with half-float lighting atlases and export packed probes.")]
        public bool compactVolume;
        public Vector3 origin;
        public Vector3Int counts = new Vector3Int(8, 4, 8);
        public float spacing = 2f;
        public float[] hours = HalfDayHours();
        public int rays = 512;
        public int iterations = 64;
        public int geometryPasses = 4;
        public bool rotatingDirections = true;
        public bool classification = true;
        public float hysteresis = .9f;
        public float gamma = 5f;
        public float normalBias = .02f;
        public float distanceExponent = 16f;
        public float minFrontfaceDistance = .2f;
        public float fixedBackfaceThreshold = .1f;
        [Tooltip("Optional coarse bake directory or report.json. Use the same geometry and matching TOD keys.")]
        public string coarseFeedbackReport;
        [Tooltip("Width of the coarse-to-fine feedback transition inside the fine volume, along X and Z.")]
        public float coarseFeedbackFadeMeters = 3f;

        internal bool Relocation => geometryPasses > 0;
        internal int ProbeCount => counts.x * counts.y * counts.z;

        internal void Fit(Bounds bounds)
        {
            origin = bounds.center;
            Vector3 extent = bounds.size;
            counts = new Vector3Int(
                Mathf.Max(2, Mathf.CeilToInt(extent.x / spacing) + 3),
                Mathf.Max(2, Mathf.CeilToInt(extent.y / spacing) + 3),
                Mathf.Max(2, Mathf.CeilToInt(extent.z / spacing) + 3));
        }

        internal void Validate()
        {
            if (materialMode != DdgiBakeMaterialMode.SceneMaterials && materialMode != DdgiBakeMaterialMode.GrayNativeParity)
                throw new ArgumentException("Invalid probe bake material mode.");
            int rows = compactVolume ? counts.y : counts.z;
            int layers = compactVolume ? counts.z : counts.y;
            long probes = (long)counts.x * counts.y * counts.z;
            int probeLimit = compactVolume ? 65535 * 32 : 65536;
            if (!Finite(origin.x) || !Finite(origin.y) || !Finite(origin.z) ||
                counts.x < 2 || counts.y < 2 || counts.z < 2 ||
                counts.x > 1023 || counts.y > 1023 || counts.z > 1023 ||
                probes > probeLimit ||
                16 * Mathf.Max(counts.x, rows) > SystemInfo.maxTextureSize ||
                counts.x * rows > SystemInfo.maxTextureSize ||
                layers > SystemInfo.maxTextureArraySlices)
                throw new ArgumentException("The probe grid exceeds the atlas or probe-count limits.");
            if (rays < 64 || rays > 4096 || rays % 32 != 0 || rays > SystemInfo.maxTextureSize ||
                iterations < 1 || iterations > 512 || geometryPasses < 0 || geometryPasses > 16 ||
                !Finite(spacing) || spacing < .25f || spacing > 100f ||
                !Finite(hysteresis) || hysteresis < 0 || hysteresis >= 1 ||
                !Finite(gamma) || gamma <= 0 || !Finite(normalBias) || normalBias < 0 ||
                !Finite(distanceExponent) || distanceExponent <= 0 ||
                !Finite(minFrontfaceDistance) || minFrontfaceDistance <= 0 ||
                !Finite(fixedBackfaceThreshold) || fixedBackfaceThreshold < 0 || fixedBackfaceThreshold > 1)
                throw new ArgumentException("Invalid probe bake sampling or layout settings.");
            if (compactVolume)
            {
                const long resourceLimit = 4L * 1024 * 1024 * 1024;
                long rayBytes = probes * rays * 16;
                long distanceBytes = probes * 16 * 16 * 4;
                long otherBytes = probes * (16 + 8 * 8 * (8 + 4)); // ProbeData, irradiance, variability.
                if (rayBytes >= resourceLimit || distanceBytes >= resourceLimit ||
                    (SystemInfo.graphicsMemorySize > 0 &&
                        rayBytes + distanceBytes + otherBytes > SystemInfo.graphicsMemorySize * 1024L * 1024L))
                    throw new ArgumentException("The compact bake atlases exceed the DX12 resource size or reported GPU memory. Reduce the ray count or grid size.");
                if (!string.IsNullOrWhiteSpace(coarseFeedbackReport))
                    throw new ArgumentException("Compact volume baking uses one global feedback field and does not take a coarse bake.");
            }
            if (hours == null || hours.Length < 1 || hours.Length > 25)
                throw new ArgumentException("Specify 1 to 25 TOD keys.");
            if (!string.IsNullOrWhiteSpace(coarseFeedbackReport) &&
                (!Finite(coarseFeedbackFadeMeters) || coarseFeedbackFadeMeters <= 0f))
                throw new ArgumentException("Coarse feedback needs a positive transition width in meters.");
            for (int i = 0; i < hours.Length; i++)
                if (!Finite(hours[i]) || hours[i] < 6f || hours[i] > 18f || (i > 0 && hours[i] <= hours[i - 1]))
                    throw new ArgumentException("TOD keys must be strictly increasing, from 6 to 18 solar hours.");
        }

        internal static float[] HalfDayHours()
        {
            // Preserve the existing winter solar orbit and add zero-light horizon
            // endpoints so time interpolation cannot turn on GI before sunrise.
            float halfDay = Mathf.Acos(-Mathf.Tan(39.9f * Mathf.Deg2Rad) * Mathf.Tan(-20f * Mathf.Deg2Rad))
                * (12f / Mathf.PI);
            return new[] { 6f, 12f - halfDay, 7.5f, 9f, 10.5f, 12f, 13.5f, 15f, 16.5f, 12f + halfDay, 18f };
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static Vector3 ReadVector(float[] values)
        {
            if (values == null || values.Length != 3)
                throw new InvalidDataException("Expected a three-component vector.");
            return new Vector3(values[0], values[1], values[2]);
        }

        internal static Vector3 SunDirection(float hour)
        {
            float latitude = 39.9f * Mathf.PI / 180f;
            float declination = -20f * Mathf.PI / 180f;
            float angle = (hour - 12f) * Mathf.PI / 12f;
            return new Vector3(-Mathf.Cos(declination) * Mathf.Sin(angle),
                Mathf.Sin(latitude) * Mathf.Sin(declination) + Mathf.Cos(latitude) * Mathf.Cos(declination) * Mathf.Cos(angle),
                Mathf.Cos(latitude) * Mathf.Sin(declination) - Mathf.Sin(latitude) * Mathf.Cos(declination) * Mathf.Cos(angle)).normalized;
        }

        internal static Vector4 RayRotation(int frame)
        {
            float u = Fraction((frame + .5) * .7548776662466927);
            float v = Fraction((frame + .5) * .5698402909980532);
            float w = Fraction((frame + .5) * .438579021);
            float a = Mathf.Sqrt(1f - u);
            float b = Mathf.Sqrt(u);
            return new Vector4(a * Mathf.Sin(2f * Mathf.PI * v), a * Mathf.Cos(2f * Mathf.PI * v),
                b * Mathf.Sin(2f * Mathf.PI * w), b * Mathf.Cos(2f * Mathf.PI * w)).normalized;
        }

        private static float Fraction(double value) => (float)(value - Math.Floor(value));
    }
}
