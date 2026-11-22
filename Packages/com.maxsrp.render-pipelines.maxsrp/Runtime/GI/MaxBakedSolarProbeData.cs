using System;
using UnityEngine;

namespace MaxSRP
{
    public sealed class MaxBakedSolarProbeData : ScriptableObject
    {
        private static readonly int OriginId = Shader.PropertyToID("_MaxDDGIOrigin");
        private static readonly int SpacingId = Shader.PropertyToID("_MaxDDGISpacing");
        private static readonly int CountsId = Shader.PropertyToID("_MaxDDGICounts");
        private static readonly int ParamsId = Shader.PropertyToID("_MaxDDGIParams");
        private static readonly int AtlasLayoutId = Shader.PropertyToID("_MaxDDGIAtlasLayout");
        private static readonly int SamplingStrideId = Shader.PropertyToID("_MaxDDGISamplingStride");
        private static readonly int Irradiance0Id = Shader.PropertyToID("_MaxDDGIIrradiance0");
        private static readonly int Irradiance1Id = Shader.PropertyToID("_MaxDDGIIrradiance1");
        private static readonly int DistanceId = Shader.PropertyToID("_MaxDDGIDistance");
        private static readonly int ProbeDataId = Shader.PropertyToID("_MaxDDGIProbeData");

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

        internal bool Bind(MaterialPropertyBlock properties, float hour, Vector3 samplingStride)
        {
            if (UsesIrradianceStream || Hours == null || Irradiance == null ||
                Hours.Length == 0 || Hours.Length != Irradiance.Length)
                return false;

            GetIrradianceKeys(Hours, hour, out int lower, out int upper);
            return BindTextures(properties, Mathf.InverseLerp(Hours[lower], Hours[upper], hour),
                Irradiance[lower], Irradiance[upper], samplingStride);
        }

        internal bool BindStreaming(MaterialPropertyBlock properties, float hour,
            MaxSolarIrradianceStream stream, Vector3 samplingStride)
        {
            if (stream == null || !stream.MatchesSourceConfiguration(this) ||
                !stream.TryGetTextures(hour, out int lower, out int upper, out var first, out var second))
                return false;

            return BindTextures(properties, stream.Blend(hour, lower, upper), first, second, samplingStride);
        }

        private bool BindTextures(MaterialPropertyBlock properties, float blend,
            Texture first, Texture second, Vector3 samplingStride)
        {
            if (!Distance || !ProbeData || !first || !second) return false;

            bool compact = CompactTiles != Vector2Int.zero;
            if (compact && !MatchesCompactLayout(first, second))
                return false;

            int flags = (Relocation ? 1 : 0) | (Classification ? 2 : 0) | (compact ? 4 : 0);
            properties.SetVector(OriginId, Origin);
            properties.SetVector(SpacingId, Spacing);
            properties.SetVector(CountsId, (Vector3)Counts);
            properties.SetVector(ParamsId, new Vector4(Gamma, NormalBias, blend, flags));
            properties.SetVector(AtlasLayoutId, new Vector4(CompactTiles.x, CompactTiles.y, 0f, 0f));
            properties.SetVector(SamplingStrideId, samplingStride);
            properties.SetTexture(Irradiance0Id, first);
            properties.SetTexture(Irradiance1Id, second);
            properties.SetTexture(DistanceId, Distance);
            properties.SetTexture(ProbeDataId, ProbeData);
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
