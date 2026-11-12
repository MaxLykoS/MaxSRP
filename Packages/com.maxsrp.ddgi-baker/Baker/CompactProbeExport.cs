using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP.DDGIBaker.Editor
{
    // Streaming exporter for the RIGHT_Z_UP bake's storage order. Probe offsets and
    // octahedral directions still use Unity world XYZ, so only address order changes.
    internal sealed class CompactProbeExport
    {
        [Serializable]
        private sealed class MemoryBytes
        {
            public long probeData, distance, irradiancePerKey, irradianceAllKeys, total, runtimeTwoKeys;
        }

        [Serializable]
        private sealed class Manifest
        {
            public int[] counts;
            public float[] origin, hours;
            public float spacing, gamma, normalBias;
            public int columns, rows, layers, active;
            public bool classification, relocation;
            public string scene, materialMode;
            public int materialCount, rays, iterations;
            public MemoryBytes memoryBytes;
        }

        private readonly string destination;
        private readonly Vector3Int counts;
        private readonly int[] slotBySource;
        private readonly Manifest manifest;
        private readonly bool[] writtenKeys;
        private bool wroteDistance;

        internal CompactProbeExport(string dest, DdgiBakeSettings settings, BakeGeometry geometry, RenderTexture layout)
        {
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("Compact probe export expects little-endian GPU and CPU data.");
            if (settings.hours == null || settings.hours.Length == 0)
                throw new ArgumentException("The compact volume needs at least one TOD key.", nameof(settings));
            destination = Path.GetFullPath(dest);
            Directory.CreateDirectory(destination);
            if (File.Exists(Path.Combine(destination, "manifest.json")))
                throw new IOException("Choose a new output directory for a complete compact bake.");

            counts = settings.counts;
            CheckTexture(layout, 1, GraphicsFormat.R32G32B32A32_SFloat);
            writtenKeys = new bool[settings.hours.Length];

            int probesPerPlane = checked(counts.x * counts.y);
            int probeCount = checked(probesPerPlane * counts.z);
            slotBySource = new int[probeCount];
            var lookup = new uint[checked(probeCount * 4)];
            int active = 0;

            // Source [layer Z][row Y][column X] to runtime [layer Y][row Z][column X].
            for (int z = 0; z < counts.z; ++z)
            {
                var request = RequestLayer(layout, z);
                var source = request.GetData<uint>();
                if (source.Length != probesPerPlane * 4)
                    throw new IOException("Unexpected probe-layout readback stride.");
                for (int y = 0; y < counts.y; ++y)
                {
                    for (int x = 0; x < counts.x; ++x)
                    {
                        int local = y * counts.x + x;
                        int src = local * 4;
                        int target = ((y * counts.z + z) * counts.x + x) * 4;
                        lookup[target] = source[src];
                        lookup[target + 1] = source[src + 1];
                        lookup[target + 2] = source[src + 2];

                        float state = BitConverter.Int32BitsToSingle((int)source[src + 3]);
                        if (settings.classification && state != 0f && state != 1f)
                            throw new IOException($"Unexpected probe state at ({x}, {y}, {z}): {state}");
                        int slot = 0;
                        if (!settings.classification || state == 0f)
                        {
                            slot = ++active;
                            if (slot > 1 << 24)
                                throw new IOException("Active probe IDs exceed exact float32 integer precision.");
                        }
                        lookup[target + 3] = (uint)BitConverter.SingleToInt32Bits(slot);
                        slotBySource[z * probesPerPlane + local] = slot;
                    }
                }
            }

            int maxAxis = Math.Min(64, SystemInfo.maxTextureSize / 16);
            if (maxAxis <= 0) throw new NotSupportedException("Device cannot allocate even one probe tile.");
            int paddedActive = Math.Max(1, active);
            int targetLayers = DivideRoundUp(paddedActive, maxAxis * maxAxis);
            int targetSlots = DivideRoundUp(paddedActive, targetLayers);
            int columns = Math.Min(maxAxis, (int)Math.Ceiling(Math.Sqrt(targetSlots)));
            int rows = DivideRoundUp(targetSlots, columns);
            int layers = DivideRoundUp(paddedActive, columns * rows);
            if (layers > SystemInfo.maxTextureArraySlices)
                throw new NotSupportedException($"Packed probe atlas needs {layers} array slices.");

            long packedSlots = (long)columns * rows * layers;
            long distanceBytes = packedSlots * 16 * 16 * 4;
            long irradianceBytes = packedSlots * 8 * 8 * 8;
            long lookupBytes = (long)probeCount * 16;
            manifest = new Manifest
            {
                counts = new[] { counts.x, counts.y, counts.z },
                origin = new[] { settings.origin.x, settings.origin.y, settings.origin.z },
                spacing = settings.spacing,
                gamma = settings.gamma,
                normalBias = settings.normalBias,
                hours = (float[])settings.hours.Clone(),
                columns = columns,
                rows = rows,
                layers = layers,
                active = active,
                classification = settings.classification,
                scene = geometry.Source,
                materialMode = settings.materialMode.ToString(),
                materialCount = geometry.MaterialCount,
                rays = settings.rays,
                iterations = settings.iterations,
                relocation = settings.Relocation,
                memoryBytes = new MemoryBytes
                {
                    probeData = lookupBytes,
                    distance = distanceBytes,
                    irradiancePerKey = irradianceBytes,
                    irradianceAllKeys = irradianceBytes * writtenKeys.Length,
                    total = lookupBytes + distanceBytes + irradianceBytes * writtenKeys.Length,
                    runtimeTwoKeys = lookupBytes + distanceBytes + irradianceBytes * Math.Min(2, writtenKeys.Length)
                }
            };

            WriteFile("probe_data.bin", stream =>
            {
                // Stream in bounded chunks even if the original grid gets larger.
                var buffer = new byte[1024 * 1024];
                int length = checked(lookup.Length * sizeof(uint));
                for (int offset = 0; offset < length;)
                {
                    int count = Math.Min(length - offset, buffer.Length);
                    Buffer.BlockCopy(lookup, offset, buffer, 0, count);
                    stream.Write(buffer, 0, count);
                    offset += count;
                }
            });
        }

        internal void WriteDistance(RenderTexture distance)
        {
            WriteAtlas(distance, "distance.bin", 16, 4, GraphicsFormat.R16G16_SFloat);
            wroteDistance = true;
            TryWriteManifest();
        }

        internal void WriteIrradiance(int key, RenderTexture irradiance)
        {
            if ((uint)key >= (uint)writtenKeys.Length)
                throw new ArgumentOutOfRangeException(nameof(key));
            WriteAtlas(irradiance, $"irradiance_{key}.bin", 8, 8, GraphicsFormat.R16G16B16A16_SFloat);
            writtenKeys[key] = true;
            TryWriteManifest();
        }

        private void WriteAtlas(RenderTexture texture, string file, int tile, int texelBytes, GraphicsFormat format)
        {
            CheckTexture(texture, tile, format);
            int sourceRow = checked(texture.width * texelBytes);
            int sourcePlane = checked(sourceRow * texture.height);
            int targetRow = checked(manifest.columns * tile * texelBytes);
            int targetPlane = checked(targetRow * manifest.rows * tile);
            int planeSlots = manifest.columns * manifest.rows;
            int probesPerPlane = counts.x * counts.y;
            int tileRow = tile * texelBytes;

            WriteFile(file, stream =>
            {
                var sourceBytes = new byte[sourcePlane];
                var targetBytes = new byte[targetPlane];
                int targetSlice = 0;
                for (int z = 0; z < counts.z; ++z)
                {
                    int sourceStart = z * probesPerPlane;
                    if (Array.FindIndex(slotBySource, sourceStart, probesPerPlane, slot => slot > 0) < 0)
                        continue;
                    var request = RequestLayer(texture, z);
                    var source = request.GetData<byte>();
                    if (source.Length != sourceBytes.Length)
                        throw new IOException("Unexpected readback stride for " + file);
                    source.CopyTo(sourceBytes);

                    for (int y = 0; y < counts.y; ++y)
                    {
                        for (int x = 0; x < counts.x; ++x)
                        {
                            int slot = slotBySource[sourceStart + y * counts.x + x] - 1;
                            if (slot < 0) continue;

                            // Slot assignment and source traversal both follow Z, Y, X.
                            // Therefore only one packed destination slice is live at a time.
                            int slice = slot / planeSlots;
                            if (slice != targetSlice)
                            {
                                if (slice != targetSlice + 1) throw new IOException("Nonsequential packed probe slots.");
                                stream.Write(targetBytes, 0, targetPlane);
                                Array.Clear(targetBytes, 0, targetPlane);
                                targetSlice = slice;
                            }
                            int withinSlice = slot % planeSlots;
                            int tileX = withinSlice % manifest.columns;
                            int tileY = withinSlice / manifest.columns;
                            int src = y * tile * sourceRow + x * tileRow;
                            int dst = tileY * tile * targetRow + tileX * tileRow;
                            for (int row = 0; row < tile; ++row)
                                Buffer.BlockCopy(sourceBytes, src + row * sourceRow, targetBytes, dst + row * targetRow, tileRow);
                        }
                    }
                }
                if (targetSlice != manifest.layers - 1)
                    throw new IOException("Incomplete packed probe atlas.");
                stream.Write(targetBytes, 0, targetPlane);
            });
        }

        private void CheckTexture(RenderTexture texture, int tile, GraphicsFormat format)
        {
            if (!texture || texture.dimension != TextureDimension.Tex2DArray ||
                texture.width != counts.x * tile || texture.height != counts.y * tile ||
                texture.volumeDepth != counts.z || texture.graphicsFormat != format)
                throw new ArgumentException($"Expected a {counts.x * tile}×{counts.y * tile}×{counts.z} {format} probe atlas.");
        }

        private static AsyncGPUReadbackRequest RequestLayer(RenderTexture texture, int layer)
        {
            var request = AsyncGPUReadback.Request(texture, 0, 0, texture.width, 0, texture.height, layer, 1);
            request.WaitForCompletion();
            if (request.hasError || request.layerCount != 1)
                throw new IOException($"Could not read {texture.name} slice {layer}.");
            return request;
        }

        private void TryWriteManifest()
        {
            if (!wroteDistance || Array.IndexOf(writtenKeys, false) >= 0) return;
            WriteFile("manifest.json", stream =>
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest, true));
                stream.Write(bytes, 0, bytes.Length);
            });
        }

        private void WriteFile(string file, Action<FileStream> write)
        {
            string path = Path.Combine(destination, file);
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    write(stream);
                if (File.Exists(path))
                    File.Replace(staged, path, null);
                else
                    File.Move(staged, path);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
        }

        private static int DivideRoundUp(int value, int divisor) => (value - 1) / divisor + 1;
    }
}
