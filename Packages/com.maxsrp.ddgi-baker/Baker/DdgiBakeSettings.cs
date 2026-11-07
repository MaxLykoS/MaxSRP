using System;
using System.IO;
using UnityEngine;

namespace MaxSRP.DDGIBaker.Editor
{
    [Serializable]
    internal sealed class DdgiBakeSettings
    {
        public Vector3 origin;
        public Vector3Int counts = new Vector3Int(8, 4, 8);
        public float spacing = 1.5f;
        public float[] hours = { 9f, 10.5f, 12f, 13.5f, 15f };
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
            if (!Finite(origin.x) || !Finite(origin.y) || !Finite(origin.z) ||
                counts.x < 2 || counts.y < 2 || counts.z < 2 ||
                counts.x > 1023 || counts.y > 1023 || counts.z > 1023 ||
                (long)counts.x * counts.y * counts.z > 65536 ||
                16 * Mathf.Max(counts.x, counts.z) > SystemInfo.maxTextureSize ||
                counts.x * counts.z > SystemInfo.maxTextureSize ||
                counts.y > SystemInfo.maxTextureArraySlices)
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
            if (hours == null || hours.Length < 1 || hours.Length > 25)
                throw new ArgumentException("Specify 1 to 25 TOD keys.");
            for (int i = 0; i < hours.Length; i++)
                if (!Finite(hours[i]) || hours[i] < 9f || hours[i] > 15f || (i > 0 && hours[i] <= hours[i - 1]))
                    throw new ArgumentException("TOD keys must be strictly increasing, from 9 to 15 solar hours.");
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
