using System;
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
        [Serializable]
        public sealed class IrradianceStreamSource
        {
            public string RelativeDirectory;
            public int Width, Height, Layers;
            public TextureFormat Format;
        }

        [HideInInspector] public IrradianceStreamSource StreamedIrradiance;
        // Tiles per array slice. Zero selects the original dense XZ layout.
        public Vector2Int CompactTiles;

        internal bool UsesIrradianceStream => StreamedIrradiance != null &&
            !string.IsNullOrEmpty(StreamedIrradiance.RelativeDirectory);

        internal static void GetIrradianceKeys(float[] hours, float hour, out int lower, out int upper)
        {
            lower = 0;
            while (lower + 1 < hours.Length && hours[lower + 1] <= hour) ++lower;
            upper = Mathf.Min(lower + 1, hours.Length - 1);
        }

        internal bool Bind(MaterialPropertyBlock properties, string prefix, float hour)
        {
            if (UsesIrradianceStream || Hours == null || Irradiance == null ||
                Hours.Length == 0 || Hours.Length != Irradiance.Length)
                return false;

            GetIrradianceKeys(Hours, hour, out int lower, out int upper);
            return BindTextures(properties, prefix, Mathf.InverseLerp(Hours[lower], Hours[upper], hour),
                Irradiance[lower], Irradiance[upper]);
        }

        internal bool BindStreaming(MaterialPropertyBlock properties, string prefix, float hour,
            MaxSolarIrradianceStream stream)
        {
            if (stream == null || !stream.MatchesSourceConfiguration(this) ||
                !stream.TryGetTextures(hour, out int lower, out int upper, out var first, out var second))
                return false;

            return BindTextures(properties, prefix, stream.Blend(hour, lower, upper), first, second);
        }

        private bool BindTextures(MaterialPropertyBlock properties, string prefix, float blend, Texture first, Texture second)
        {
            if (!Distance || !ProbeData || !first || !second) return false;

            bool compact = CompactTiles != Vector2Int.zero;
            if (compact && !MatchesCompactLayout(first, second))
                return false;

            int flags = (Relocation ? 1 : 0) | (Classification ? 2 : 0) | (compact ? 4 : 0);
            properties.SetVector(prefix + "Origin", Origin);
            properties.SetVector(prefix + "Spacing", Spacing);
            properties.SetVector(prefix + "Counts", (Vector3)Counts);
            properties.SetVector(prefix + "Params", new Vector4(Gamma, NormalBias, blend, flags));
            properties.SetVector(prefix + "AtlasLayout", new Vector4(CompactTiles.x, CompactTiles.y, 0f, 0f));
            properties.SetTexture(prefix + "Irradiance0", first);
            properties.SetTexture(prefix + "Irradiance1", second);
            properties.SetTexture(prefix + "Distance", Distance);
            properties.SetTexture(prefix + "ProbeData", ProbeData);
            return true;
        }

        private bool MatchesCompactLayout(Texture lower, Texture upper)
        {
            if (CompactTiles.x <= 0 || CompactTiles.y <= 0 || ProbeData.format != TextureFormat.RGBAFloat ||
                ProbeData.width != Counts.x || ProbeData.height != Counts.z || ProbeData.depth != Counts.y)
                return false;

            long irradianceWidth = 8L * CompactTiles.x;
            long irradianceHeight = 8L * CompactTiles.y;
            return Distance.width == 2 * irradianceWidth && Distance.height == 2 * irradianceHeight &&
                lower.width == irradianceWidth && lower.height == irradianceHeight && LayerCount(lower) == Distance.depth &&
                upper.width == irradianceWidth && upper.height == irradianceHeight && LayerCount(upper) == Distance.depth;
        }

        private static int LayerCount(Texture texture) =>
            texture is Texture2DArray array ? array.depth : ((RenderTexture)texture).volumeDepth;
    }
}
