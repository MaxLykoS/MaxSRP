using System;
using System.IO;
using MaxSRP.DDGI;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxSRP.DDGIBaker.Editor
{
    internal sealed class CoarseProbeFeedback : IDisposable
    {
        private readonly NativeGiParity.Reference source;
        private readonly Vector3Int counts;
        private readonly Vector4 originAndFade, spacingAndGamma, countsAndBias;
        private readonly int flags;
        private readonly int[] sourceKeys;
        private Texture2DArray probeData, distance, irradiance;

        internal CoarseProbeFeedback(BakeGeometry geometry, DdgiBakeSettings settings)
        {
            source = NativeGiParity.Load(settings.coarseFeedbackReport);
            var report = source.Report;
            bool compatibleMaterials = settings.materialMode == DdgiBakeMaterialMode.GrayNativeParity
                ? NativeGiParity.IsGrayReport(report)
                : report.material_mode == nameof(DdgiBakeMaterialMode.SceneMaterials) &&
                    report.scene == geometry.Source && report.material_count == geometry.MaterialCount;
            if (!report.hardware_raytracing || report.legacy_probes || report.layout_validation || report.view_bias != 0f ||
                !compatibleMaterials || !NativeGiParity.IsUniformRgb(report.sun_normal_illuminance, 1f))
                throw new InvalidDataException("Coarse feedback needs a modern, unfiltered unit-sun bake with matching materials and zero view bias.");
            if (report.vertices != geometry.VertexCount || report.triangles != geometry.TriangleCount)
                throw new InvalidDataException("The input geometry counts differ from the coarse bake.");
            counts = new Vector3Int(report.grid[0], report.grid[1], report.grid[2]);
            if (report.spacing <= settings.spacing || counts.y > SystemInfo.maxTextureArraySlices ||
                ProbeVolume.DistanceTileSize * Mathf.Max(counts.x, counts.z) > SystemInfo.maxTextureSize)
                throw new InvalidDataException("Coarse feedback requires wider probe spacing and a supported atlas size.");
            Vector3 origin = DdgiBakeSettings.ReadVector(report.volume_origin);
            Vector3 coarseSize = (Vector3)(counts - Vector3Int.one) * report.spacing;
            Vector3 fineSize = (Vector3)(settings.counts - Vector3Int.one) * settings.spacing;
            if (!new Bounds(origin, coarseSize).Intersects(new Bounds(settings.origin, fineSize)))
                throw new InvalidDataException("The coarse and fine probe volumes must overlap.");

            originAndFade = new Vector4(origin.x, origin.y, origin.z, settings.coarseFeedbackFadeMeters);
            spacingAndGamma = new Vector4(report.spacing, report.spacing, report.spacing, report.gamma);
            countsAndBias = new Vector4(counts.x, counts.y, counts.z, report.normal_bias);
            flags = (report.relocation ? 1 : 0) | (report.classification ? 2 : 0);
            sourceKeys = new int[settings.hours.Length];
            for (int i = 0; i < sourceKeys.Length; i++)
            {
                int key = Array.FindIndex(report.keys, candidate => candidate.hour == settings.hours[i]);
                if (key < 0 || !File.Exists(IrradiancePath(key)))
                    throw new InvalidDataException($"Coarse feedback is missing the {settings.hours[i]:g}h irradiance key.");
                sourceKeys[i] = key;
            }

            try
            {
                probeData = LoadTexture("shared_probe_data.ngatlas", 1);
                distance = LoadTexture("shared_distance.ngatlas", ProbeVolume.DistanceTileSize);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void SelectKey(int key)
        {
            if (irradiance) Object.DestroyImmediate(irradiance);
            irradiance = LoadTexture(IrradiancePath(sourceKeys[key]), ProbeVolume.IrradianceTileSize);
        }

        internal static void Bind(CommandBuffer commands, RayTracingShader shader, RenderTexture fineIrradiance,
            RenderTexture fineDistance, RenderTexture fineProbeData, CoarseProbeFeedback coarse)
        {
            // DXR expects bound SRVs even on dispatches that skip the coarse branch.
            commands.SetRayTracingVectorParam(shader, "_CoarseFeedbackOriginAndFade", coarse?.originAndFade ?? Vector4.zero);
            commands.SetRayTracingVectorParam(shader, "_CoarseFeedbackSpacingAndGamma", coarse?.spacingAndGamma ?? Vector4.zero);
            commands.SetRayTracingVectorParam(shader, "_CoarseFeedbackCountsAndBias", coarse?.countsAndBias ?? Vector4.zero);
            commands.SetRayTracingIntParam(shader, "_CoarseFeedbackFlags", coarse?.flags ?? 0);
            commands.SetRayTracingTextureParam(shader, "_CoarseFeedbackIrradiance", AllSlices(coarse != null ? (Texture)coarse.irradiance : fineIrradiance));
            commands.SetRayTracingTextureParam(shader, "_CoarseFeedbackDistance", AllSlices(coarse != null ? (Texture)coarse.distance : fineDistance));
            commands.SetRayTracingTextureParam(shader, "_CoarseFeedbackProbeData", AllSlices(coarse != null ? (Texture)coarse.probeData : fineProbeData));
        }

        private Texture2DArray LoadTexture(string file, int tile)
        {
            string path = Path.Combine(source.AtlasDirectory, file);
            var atlas = NativeGiParity.ReadAtlas(path);
            if (atlas.Width != counts.x * tile || atlas.Height != counts.z * tile || atlas.Layers != counts.y)
                throw new InvalidDataException("Coarse feedback atlas does not match its reported grid: " + path);
            if (tile == 1)
                for (int i = 3; i < atlas.Values.Length; i += 4)
                    if (atlas.Values[i] != 0f && atlas.Values[i] != 1f)
                        throw new InvalidDataException("Coarse feedback requires dense SDK probe states: " + path);

            var texture = new Texture2DArray(atlas.Width, atlas.Height, atlas.Layers, TextureFormat.RGBAFloat, false, true)
            {
                name = "DDGI coarse feedback " + Path.GetFileNameWithoutExtension(path),
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                int sliceValues = atlas.Width * atlas.Height * atlas.Channels;
                for (int layer = 0; layer < atlas.Layers; layer++)
                    texture.SetPixelData(atlas.Values, 0, layer, layer * sliceValues);
                texture.Apply(false, true);
                return texture;
            }
            catch
            {
                Object.DestroyImmediate(texture);
                throw;
            }
        }

        private string IrradiancePath(int key) => Path.Combine(source.AtlasDirectory, $"official_irradiance_{key}.ngatlas");

        private static RenderTargetIdentifier AllSlices(Texture atlas) =>
            new RenderTargetIdentifier(atlas, 0, CubemapFace.Unknown, RenderTargetIdentifier.AllDepthSlices);

        public void Dispose()
        {
            if (irradiance) Object.DestroyImmediate(irradiance);
            if (distance) Object.DestroyImmediate(distance);
            if (probeData) Object.DestroyImmediate(probeData);
        }
    }
}
