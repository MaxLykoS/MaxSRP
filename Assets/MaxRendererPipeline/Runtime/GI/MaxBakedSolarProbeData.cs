using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public sealed class MaxBakedSolarProbeData : ScriptableObject
    {
        public Vector3 Origin;
        public Vector3 Spacing;
        public Vector3Int Counts;
        public float Gamma;
        public float NormalBias;
        public bool Relocation;
        public bool Classification;
        public Texture2DArray Distance;
        public Texture2DArray ProbeData;
        public float[] Hours;
        public Texture2DArray[] Irradiance;

        internal bool Bind(MaterialPropertyBlock properties, string prefix, float hour)
        {
            if (!Distance || !ProbeData || Hours == null || Irradiance == null ||
                Hours.Length == 0 || Hours.Length != Irradiance.Length)
                return false;

            int lower = 0;
            while (lower + 1 < Hours.Length && Hours[lower + 1] <= hour) ++lower;
            int upper = Mathf.Min(lower + 1, Hours.Length - 1);
            if (!Irradiance[lower] || !Irradiance[upper]) return false;

            float blend = Mathf.InverseLerp(Hours[lower], Hours[upper], hour);
            int flags = (Relocation ? 1 : 0) | (Classification ? 2 : 0);
            properties.SetVector(prefix + "Origin", Origin);
            properties.SetVector(prefix + "Spacing", Spacing);
            properties.SetVector(prefix + "Counts", (Vector3)Counts);
            properties.SetVector(prefix + "Params", new Vector4(Gamma, NormalBias, blend, flags));
            properties.SetTexture(prefix + "Irradiance0", Irradiance[lower]);
            properties.SetTexture(prefix + "Irradiance1", Irradiance[upper]);
            properties.SetTexture(prefix + "Distance", Distance);
            properties.SetTexture(prefix + "ProbeData", ProbeData);
            return true;
        }
    }
}
