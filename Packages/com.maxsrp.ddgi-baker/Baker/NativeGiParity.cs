using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MaxSRP.DDGIBaker.Editor
{
    internal static class NativeGiParity
    {
        private const int HeaderBytes = 24;
        private const int FileChannels = 4;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NGATLAS1");

        [Serializable]
        internal sealed class Key
        {
            public float hour;
        }

        [Serializable]
        internal sealed class BakeReport
        {
            public string scene;
            public string device;
            public string ray_backend;
            public string material_mode;
            public int material_count;
            public string baked_atlas_directory;
            public bool baked_atlases_written;
            public bool hardware_raytracing;
            public int triangles, vertices, probes;
            public int[] grid;
            public int rays_per_probe, iterations, geometry_passes;
            public int inactive_probes, relocated_probes, official_updates_per_iteration;
            public float spacing, solar_hour, hysteresis;
            public float gamma, normal_bias, view_bias, distance_exponent;
            public float min_frontface_distance, fixed_ray_backface_threshold;
            public float[] albedo, sun_normal_illuminance, volume_origin, camera_position, camera_forward;
            public bool legacy_probes, classification, relocation, rotating_directions, layout_validation;
            public double bake_seconds;
            public string coarse_feedback_report;
            public float coarse_feedback_fade_meters;
            public Key[] keys;
        }

        internal static bool IsGrayReport(BakeReport report) =>
            (string.IsNullOrEmpty(report.material_mode) || report.material_mode == nameof(DdgiBakeMaterialMode.GrayNativeParity)) &&
            IsUniformRgb(report.albedo, .7f);

        internal static bool IsUniformRgb(float[] rgb, float expected) => rgb != null && rgb.Length == 3 &&
            Array.TrueForAll(rgb, value => Mathf.Abs(value - expected) <= 1e-6f);

        internal sealed class Atlas
        {
            public readonly int Width, Height, Layers, Channels;
            public readonly float[] Values;

            // Values are tightly packed in layer, y, x, channel order.
            public Atlas(int width, int height, int layers, int channels, float[] values)
            {
                if (values == null || values.Length != ValueCount(width, height, layers, channels))
                    throw new ArgumentException("Atlas payload does not match its dimensions.", nameof(values));
                Width = width;
                Height = height;
                Layers = layers;
                Channels = channels;
                Values = values;
            }

            public int TexelCount => Values.Length / Channels;

            public float Get(int x, int y, int layer, int channel)
            {
                return Values[((layer * Height + y) * Width + x) * Channels + channel];
            }

            public Vector3 GetRgb(int x, int y, int layer)
            {
                int index = ((layer * Height + y) * Width + x) * Channels;
                return new Vector3(Values[index], Values[index + 1], Channels >= 3 ? Values[index + 2] : 0f);
            }
        }

        [Serializable]
        internal sealed class Metrics
        {
            public int count;
            public double meanAbsoluteError, maxAbsoluteError, rmse, referenceRms, relativeRmse, maxAbsOverGlobalScale;
            private double maximumMagnitude = 1;

            internal void Add(float expected, float actual)
            {
                if (!Finite(expected) || !Finite(actual))
                    throw new InvalidDataException("Parity comparison encountered a nonfinite value.");
                double error = Math.Abs((double)expected - actual);
                ++count;
                meanAbsoluteError += error;
                maxAbsoluteError = Math.Max(maxAbsoluteError, error);
                maximumMagnitude = Math.Max(maximumMagnitude, Math.Max(Math.Abs((double)expected), Math.Abs((double)actual)));
                rmse += error * error;
                referenceRms += (double)expected * expected;
            }

            internal void Finish()
            {
                meanAbsoluteError /= count;
                rmse = Math.Sqrt(rmse / count);
                referenceRms = Math.Sqrt(referenceRms / count);
                relativeRmse = rmse / Math.Max(referenceRms, 1e-15);
                maxAbsOverGlobalScale = maxAbsoluteError / maximumMagnitude;
            }
        }

        [Serializable]
        internal sealed class Comparison
        {
            public string referenceReport;
            public int keyIndex;
            public float hour;
            public Metrics encodedIrradiance, storedDistanceMean, storedDistanceSecondMoment, normalizedProbeOffset;
            public Metrics sampledLinearIrradiance;
            public int referenceInactiveProbes, actualInactiveProbes, classificationMismatches;
            public int referenceRelocatedProbes, actualRelocatedProbes;
            public int sampleQueries, nonzeroReferenceQueries;

            public string ToJson() => JsonUtility.ToJson(this, true);
        }

        internal sealed class Reference
        {
            private readonly string reportPath, atlasDirectory;
            public BakeReport Report { get; }
            internal string AtlasDirectory => atlasDirectory;

            internal Reference(string reportPath, string atlasDirectory, BakeReport report)
            {
                this.reportPath = reportPath;
                this.atlasDirectory = atlasDirectory;
                Report = report;
            }

            public Comparison Compare(int keyIndex, Atlas irradiance, Atlas distance, Atlas probeData)
            {
                if ((uint)keyIndex >= (uint)Report.keys.Length)
                    throw new ArgumentOutOfRangeException(nameof(keyIndex));
                var expectedIrradiance = ReadAtlas(Path.Combine(atlasDirectory, $"official_irradiance_{keyIndex}.ngatlas"));
                var expectedDistance = ReadAtlas(Path.Combine(atlasDirectory, "shared_distance.ngatlas"));
                var expectedProbeData = ReadAtlas(Path.Combine(atlasDirectory, "shared_probe_data.ngatlas"));
                var grid = new Vector3Int(Report.grid[0], Report.grid[1], Report.grid[2]);
                int irradianceTile = GetTile(expectedIrradiance, grid, "irradiance");
                int distanceTile = GetTile(expectedDistance, grid, "distance");
                if (expectedProbeData.Width != grid.x || expectedProbeData.Height != grid.z || expectedProbeData.Layers != grid.y)
                    throw new InvalidDataException("Probe-data atlas does not match the report grid.");
                CheckShape(expectedIrradiance, irradiance, 3, "irradiance");
                CheckShape(expectedDistance, distance, 2, "distance");
                CheckShape(expectedProbeData, probeData, 4, "probe data");

                var result = new Comparison
                {
                    referenceReport = reportPath,
                    keyIndex = keyIndex,
                    hour = Report.keys[keyIndex].hour,
                    encodedIrradiance = CompareChannels(expectedIrradiance, irradiance, 0, 3),
                    storedDistanceMean = CompareChannels(expectedDistance, distance, 0, 1),
                    storedDistanceSecondMoment = CompareChannels(expectedDistance, distance, 1, 1),
                    normalizedProbeOffset = CompareChannels(expectedProbeData, probeData, 0, 3),
                    sampledLinearIrradiance = new Metrics(),
                };
                for (int texel = 0; texel < probeData.TexelCount; ++texel)
                {
                    int index = texel * FileChannels;
                    float expectedState = expectedProbeData.Values[index + 3];
                    float actualState = probeData.Values[index + 3];
                    if ((expectedState != 0f && expectedState != 1f) || (actualState != 0f && actualState != 1f))
                        throw new InvalidDataException($"Invalid probe classification state at texel {texel}.");
                    if (expectedState == 1f) ++result.referenceInactiveProbes;
                    if (actualState == 1f) ++result.actualInactiveProbes;
                    if (expectedState != actualState) ++result.classificationMismatches;
                    if (IsRelocated(expectedProbeData.Values, index)) ++result.referenceRelocatedProbes;
                    if (IsRelocated(probeData.Values, index)) ++result.actualRelocatedProbes;
                }
                var referenceField = new Field(Report, grid, irradianceTile, distanceTile,
                    expectedIrradiance, expectedDistance, expectedProbeData);
                var actualField = new Field(Report, grid, irradianceTile, distanceTile, irradiance, distance, probeData);
                CompareSamples(grid, referenceField, actualField, result);
                return result;
            }
        }

        public static Reference Load(string bakeDirectory)
        {
            string path = Path.GetFullPath(bakeDirectory);
            string reportPath = Directory.Exists(path) ? Path.Combine(path, "report.json") : path;
            var report = JsonUtility.FromJson<BakeReport>(File.ReadAllText(reportPath));
            if (report == null || string.IsNullOrWhiteSpace(report.scene) || report.vertices <= 0 || report.triangles <= 0
                || report.grid == null || report.grid.Length != 3 || report.grid[0] < 2 || report.grid[0] > 65536
                || report.grid[1] < 2 || report.grid[1] > 65536 || report.grid[2] < 2 || report.grid[2] > 65536
                || report.probes < 8 || report.probes > 65536
                || (long)report.grid[0] * report.grid[1] * report.grid[2] != report.probes
                || report.volume_origin == null || report.volume_origin.Length != 3
                || !Array.TrueForAll(report.volume_origin, Finite)
                || !Finite(report.spacing) || report.spacing <= 0f || !Finite(report.gamma) || report.gamma <= 0f
                || !Finite(report.normal_bias) || report.normal_bias < 0f || !Finite(report.view_bias) || report.view_bias < 0f
                || report.rays_per_probe <= 0 || report.iterations <= 0
                || report.keys == null || report.keys.Length == 0)
                throw new InvalidDataException($"Invalid NativeGI bake settings in {reportPath}.");

            float previous = float.NegativeInfinity;
            foreach (var key in report.keys)
            {
                if (key == null || !Finite(key.hour) || key.hour <= previous)
                    throw new InvalidDataException($"Invalid NativeGI TOD key order in {reportPath}.");
                previous = key.hour;
            }

            string reportDirectory = Path.GetDirectoryName(reportPath);
            string atlasDirectory = Path.Combine(reportDirectory, "baked");
            if (!Directory.Exists(atlasDirectory) && !string.IsNullOrWhiteSpace(report.baked_atlas_directory))
            {
                string listedDirectory = report.baked_atlas_directory;
                atlasDirectory = Directory.Exists(listedDirectory)
                    ? Path.GetFullPath(listedDirectory)
                    : Path.Combine(reportDirectory, listedDirectory);
            }
            if (!Directory.Exists(atlasDirectory))
                throw new DirectoryNotFoundException($"NativeGI baked atlas directory was not found: {atlasDirectory}");
            return new Reference(reportPath, atlasDirectory, report);
        }

        public static Atlas ReadAtlas(string path)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("NGATLAS requires little-endian floats.");
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.BaseStream.Length < HeaderBytes || Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != "NGATLAS1")
                    throw new InvalidDataException($"Invalid NativeGI atlas header: {path}");
                int width = reader.ReadInt32(), height = reader.ReadInt32(), layers = reader.ReadInt32();
                if (reader.ReadInt32() != FileChannels)
                    throw new InvalidDataException($"Invalid NativeGI atlas channel count: {path}");
                int count = ValueCount(width, height, layers, FileChannels);
                int size = checked(count * sizeof(float));
                if (reader.BaseStream.Length != HeaderBytes + (long)size)
                    throw new InvalidDataException($"Invalid NativeGI atlas byte length: {path}");
                var values = new float[count];
                var bytes = new byte[Math.Min(1024 * 1024, size)];
                for (int offset = 0; offset < size;)
                {
                    int read = reader.Read(bytes, 0, Math.Min(bytes.Length, size - offset));
                    if (read == 0) throw new EndOfStreamException(path);
                    Buffer.BlockCopy(bytes, 0, values, offset, read);
                    offset += read;
                }
                if (!Array.TrueForAll(values, Finite))
                    throw new InvalidDataException($"Nonfinite NativeGI atlas value: {path}");
                return new Atlas(width, height, layers, FileChannels, values);
            }
        }

        public static void WriteAtlas(string path, Atlas atlas)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("NGATLAS requires little-endian floats.");
            if (!Array.TrueForAll(atlas.Values, Finite))
                throw new InvalidDataException($"Nonfinite atlas value: {path}");
            int count = ValueCount(atlas.Width, atlas.Height, atlas.Layers, FileChannels);
            int size = checked(count * sizeof(float));
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Magic);
                writer.Write(atlas.Width);
                writer.Write(atlas.Height);
                writer.Write(atlas.Layers);
                writer.Write(FileChannels);
                var bytes = new byte[Math.Min(1024 * 1024, size)];
                var padded = atlas.Channels == FileChannels ? null : new float[bytes.Length / sizeof(float)];
                for (int offset = 0; offset < size;)
                {
                    int length = Math.Min(bytes.Length, size - offset);
                    if (padded == null)
                    {
                        Buffer.BlockCopy(atlas.Values, offset, bytes, 0, length);
                    }
                    else
                    {
                        int startTexel = offset / (FileChannels * sizeof(float));
                        int texels = length / (FileChannels * sizeof(float));
                        for (int texel = 0; texel < texels; ++texel)
                            for (int channel = 0; channel < atlas.Channels; ++channel)
                                padded[texel * FileChannels + channel] = atlas.Values[(startTexel + texel) * atlas.Channels + channel];
                        Buffer.BlockCopy(padded, 0, bytes, 0, length);
                    }
                    writer.Write(bytes, 0, length);
                    offset += length;
                }
            }
        }

        private static int ValueCount(int width, int height, int layers, int channels)
        {
            long count = (long)width * height * layers * channels;
            if (width < 1 || width > 16384 || height < 1 || height > 16384 || layers < 1 || layers > 2048
                || channels < 2 || channels > FileChannels || count > int.MaxValue / sizeof(float))
                throw new InvalidDataException("Atlas dimensions exceed the supported range.");
            return (int)count;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsRelocated(float[] data, int index)
        {
            return Mathf.Abs(data[index]) + Mathf.Abs(data[index + 1]) + Mathf.Abs(data[index + 2]) > 1e-6f;
        }

        private static int GetTile(Atlas atlas, Vector3Int grid, string name)
        {
            int tile = atlas.Width / grid.x;
            if (tile < 4 || tile > 66 || atlas.Width != tile * grid.x || atlas.Height != tile * grid.z || atlas.Layers != grid.y)
                throw new InvalidDataException($"NativeGI {name} atlas does not match the report grid.");
            return tile;
        }

        private static void CheckShape(Atlas expected, Atlas actual, int channels, string name)
        {
            if (actual == null || expected.Width != actual.Width || expected.Height != actual.Height
                || expected.Layers != actual.Layers || actual.Channels < channels)
                throw new InvalidDataException($"GPU {name} atlas does not match the NativeGI reference dimensions.");
        }

        private static Metrics CompareChannels(Atlas expected, Atlas actual, int firstChannel, int channels)
        {
            var metrics = new Metrics();
            for (int texel = 0; texel < expected.TexelCount; ++texel)
                for (int channel = firstChannel; channel < firstChannel + channels; ++channel)
                    metrics.Add(expected.Values[texel * expected.Channels + channel], actual.Values[texel * actual.Channels + channel]);
            metrics.Finish();
            return metrics;
        }

        private static void CompareSamples(Vector3Int grid, Field reference, Field actual, Comparison result)
        {
            Vector3Int cellCounts = grid - Vector3Int.one;
            int cells = cellCounts.x * cellCounts.y * cellCounts.z;
            int samples = Math.Min(cells, 512);
            var normals = new[] { Vector3.up, Vector3.right, Vector3.back, new Vector3(-1f, 1f, 1f).normalized };
            var view = new Vector3(1f, -2f, 3f).normalized;
            // Stratify across the whole grid, including interior cells and both ends.
            for (int i = 0; i < samples; ++i)
            {
                int cell = samples == 1 ? 0 : i * (cells - 1) / (samples - 1);
                int x = cell % cellCounts.x;
                int z = cell / cellCounts.x % cellCounts.z;
                int y = cell / (cellCounts.x * cellCounts.z);
                var position = reference.Start + new Vector3(x + 0.31f, y + 0.59f, z + 0.77f) * reference.Spacing;
                foreach (var normal in normals)
                {
                    Vector3 expected = reference.Sample(position, normal, view);
                    Vector3 observed = actual.Sample(position, normal, view);
                    if (expected.sqrMagnitude > 0f) ++result.nonzeroReferenceQueries;
                    ++result.sampleQueries;
                    for (int channel = 0; channel < 3; ++channel)
                        result.sampledLinearIrradiance.Add(expected[channel], observed[channel]);
                }
            }
            result.sampledLinearIrradiance.Finish();
        }

        // CPU diagnostic of the same octahedral, distance-weighted interpolation
        // used by the project receiver. Both sides use the same sampler.
        private sealed class Field
        {
            private readonly BakeReport report;
            private readonly Vector3Int grid;
            private readonly int irradianceTile, distanceTile;
            private readonly Atlas irradiance, distance, probeData;
            public readonly Vector3 Start;
            public float Spacing => report.spacing;

            public Field(BakeReport report, Vector3Int grid, int irradianceTile, int distanceTile,
                Atlas irradiance, Atlas distance, Atlas probeData)
            {
                this.report = report;
                this.grid = grid;
                this.irradianceTile = irradianceTile;
                this.distanceTile = distanceTile;
                this.irradiance = irradiance;
                this.distance = distance;
                this.probeData = probeData;
                Start = new Vector3(report.volume_origin[0], report.volume_origin[1], report.volume_origin[2])
                    - (Vector3)(grid - Vector3Int.one) * (Spacing * 0.5f);
            }

            public Vector3 Sample(Vector3 position, Vector3 normal, Vector3 view)
            {
                Vector3 biased = position + normal * report.normal_bias - view * report.view_bias;
                Vector3 gridPosition = (biased - Start) / Spacing;
                int x = Mathf.Clamp((int)gridPosition.x, 0, grid.x - 1);
                int y = Mathf.Clamp((int)gridPosition.y, 0, grid.y - 1);
                int z = Mathf.Clamp((int)gridPosition.z, 0, grid.z - 1);
                var alpha = new Vector3(Mathf.Clamp01(gridPosition.x - x), Mathf.Clamp01(gridPosition.y - y), Mathf.Clamp01(gridPosition.z - z));
                Vector2 octNormal = Octahedral(normal);
                Vector3 accumulated = Vector3.zero;
                float totalWeight = 0f;
                for (int corner = 0; corner < 8; ++corner)
                {
                    int dx = corner & 1, dy = (corner >> 1) & 1, dz = (corner >> 2) & 1;
                    int px = Mathf.Min(x + dx, grid.x - 1);
                    int py = Mathf.Min(y + dy, grid.y - 1);
                    int pz = Mathf.Min(z + dz, grid.z - 1);
                    if (report.classification && probeData.Get(px, pz, py, 3) == 1f) continue;

                    var probe = Start + new Vector3(px, py, pz) * Spacing;
                    if (report.relocation) probe += probeData.GetRgb(px, pz, py) * Spacing;
                    float wrap = (Vector3.Dot((probe - position).normalized, normal) + 1f) * 0.5f;
                    float weight = wrap * wrap + 0.2f;
                    Vector3 biasedDelta = biased - probe;
                    Vector3 moments = 2f * Bilinear(distance, distanceTile, px, pz, py, Octahedral(biasedDelta));
                    float separation = biasedDelta.magnitude - moments.x;
                    float visibility = 1f;
                    if (separation > 0f)
                    {
                        float variance = Mathf.Abs(moments.x * moments.x - moments.y);
                        visibility = variance / (variance + separation * separation);
                        visibility *= visibility * visibility;
                    }
                    weight = Mathf.Max(1e-6f, weight * Mathf.Max(0.05f, visibility));
                    if (weight < 0.2f) weight *= weight * weight * 25f;
                    weight *= Mathf.Max(0.001f, dx == 0 ? 1f - alpha.x : alpha.x)
                        * Mathf.Max(0.001f, dy == 0 ? 1f - alpha.y : alpha.y)
                        * Mathf.Max(0.001f, dz == 0 ? 1f - alpha.z : alpha.z);

                    Vector3 encoded = Bilinear(irradiance, irradianceTile, px, pz, py, octNormal);
                    float exponent = report.gamma * 0.5f;
                    accumulated += weight * new Vector3(Mathf.Pow(Mathf.Max(0f, encoded.x), exponent),
                        Mathf.Pow(Mathf.Max(0f, encoded.y), exponent), Mathf.Pow(Mathf.Max(0f, encoded.z), exponent));
                    totalWeight += weight;
                }
                if (totalWeight == 0f) return Vector3.zero;
                Vector3 average = accumulated / totalWeight;
                return (2f * Mathf.PI) * Vector3.Scale(average, average);
            }

            private static Vector2 Octahedral(Vector3 direction)
            {
                float length = Mathf.Abs(direction.x) + Mathf.Abs(direction.y) + Mathf.Abs(direction.z);
                if (length == 0f) return Vector2.zero;
                var oct = new Vector2(direction.x, direction.y) / length;
                if (direction.z < 0f)
                    oct = new Vector2((1f - Mathf.Abs(oct.y)) * Mathf.Sign(oct.x), (1f - Mathf.Abs(oct.x)) * Mathf.Sign(oct.y));
                return oct;
            }

            private static Vector3 Bilinear(Atlas atlas, int tile, int probeX, int probeZ, int layer, Vector2 oct)
            {
                float interiorRadius = (tile - 2) * 0.5f;
                float x = probeX * tile + tile * 0.5f + oct.x * interiorRadius - 0.5f;
                float y = probeZ * tile + tile * 0.5f + oct.y * interiorRadius - 0.5f;
                int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
                Vector3 a = Vector3.LerpUnclamped(atlas.GetRgb(ix, iy, layer), atlas.GetRgb(ix + 1, iy, layer), x - ix);
                Vector3 b = Vector3.LerpUnclamped(atlas.GetRgb(ix, iy + 1, layer), atlas.GetRgb(ix + 1, iy + 1, layer), x - ix);
                return Vector3.LerpUnclamped(a, b, y - iy);
            }
        }
    }
}
