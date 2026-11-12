using System;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP.EditorTools
{
    internal static class MaxSolarProbeBakeImporter
    {
#pragma warning disable 0649 // These fields are populated by JsonUtility.
        [Serializable]
        internal sealed class BakeReport
        {
            public string scene;
            public int probes, triangles, vertices;
            public int[] grid;
            public float spacing, gamma, normal_bias, view_bias;
            public float[] volume_origin, camera_position, camera_forward;
            public bool relocation, classification, baked_atlases_written;
            public Key[] keys;
        }

        [Serializable]
        internal sealed class Key
        {
            public float hour;
        }

        [Serializable]
        private sealed class CompactBakeManifest
        {
            public int[] counts;
            public float[] origin, hours;
            public float spacing, gamma, normalBias;
            public int columns, rows, layers, active;
            public bool classification, relocation;
        }
#pragma warning restore 0649

        [MenuItem("MaxSRP/Lighting/Import Baked Solar TOD")]
        private static void ImportFromMenu()
        {
            try
            {
                EnsureAvailable();
                string reportPath = EditorUtility.OpenFilePanel("Select the DDGI bake report", Application.dataPath, "json");
                if (string.IsNullOrEmpty(reportPath)) return;

                BakeReport report = ReadReport(reportPath);
                string atlasDirectory = Path.Combine(Path.GetDirectoryName(reportPath), "baked");
                ValidateAtlases(atlasDirectory, report);

                SaveFromMenu("Solar TOD", (volumePath, atlasFolder) =>
                    ImportVolume(volumePath, atlasFolder, atlasDirectory, report));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Solar TOD import failed", exception.Message, "OK");
            }
        }

        [MenuItem("MaxSRP/Lighting/Import Compact Solar TOD")]
        private static void ImportCompactFromMenu()
        {
            try
            {
                EnsureAvailable();
                string manifestPath = EditorUtility.OpenFilePanel("Select the compact solar TOD manifest", Application.dataPath, "json");
                if (string.IsNullOrEmpty(manifestPath)) return;
                SaveFromMenu("Solar TOD Streamed", (volumePath, atlasFolder) =>
                    ImportCompactVolume(manifestPath, volumePath, atlasFolder));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Solar TOD import failed", exception.Message, "OK");
            }
        }

        [MenuItem("Assets/MaxSRP/Compact Solar TOD Volume", true)]
        private static bool CanCompactSelectedVolume() =>
            Selection.activeObject is MaxBakedSolarProbeData volume &&
            volume.Classification && volume.CompactTiles == Vector2Int.zero && volume.Irradiance?.Length > 0;

        [MenuItem("Assets/MaxSRP/Compact Solar TOD Volume")]
        private static void CompactSelectedVolume()
        {
            try
            {
                EnsureAvailable();
                var source = (MaxBakedSolarProbeData)Selection.activeObject;
                SaveFromMenu(source.name + " Compact", (volumePath, atlasFolder) =>
                    CompactVolume(source, volumePath, atlasFolder));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Solar TOD compaction failed", exception.Message, "OK");
            }
        }

        [MenuItem("Assets/MaxSRP/Create Streamed Solar TOD Volume", true)]
        private static bool CanStreamSelectedVolume() =>
            Selection.activeObject is MaxBakedSolarProbeData volume && volume.Irradiance?.Length > 0;

        [MenuItem("Assets/MaxSRP/Create Streamed Solar TOD Volume")]
        private static void StreamSelectedVolume()
        {
            try
            {
                var source = (MaxBakedSolarProbeData)Selection.activeObject;
                string volumePath = EditorUtility.SaveFilePanelInProject("Save Streamed Solar TOD", source.name + " Streamed",
                    "asset", "Save generated data under Assets/LocalGIBakes.", EnsureLocalSolarFolder());
                if (string.IsNullOrEmpty(volumePath)) return;
                ValidateLocalBakePath(volumePath);
                var volume = ExportStreamedVolume(source, AssetDatabase.GenerateUniqueAssetPath(volumePath));
                Selection.activeObject = volume;
                EditorGUIUtility.PingObject(volume);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Solar TOD export failed", exception.Message, "OK");
            }
        }

        internal static MaxBakedSolarProbeData ExportStreamedVolume(MaxBakedSolarProbeData source, string volumePath)
        {
            ValidateStreamDestination(volumePath);
            if (!source || !source.ProbeData || !source.Distance || source.Hours == null ||
                source.Irradiance == null || source.Hours.Length == 0 || source.Hours.Length != source.Irradiance.Length)
                throw new InvalidDataException("Select a baked solar probe volume with all its atlases.");

            var first = source.Irradiance[0];
            if (!first || (first.format != TextureFormat.RGBAHalf && first.format != TextureFormat.RGBAFloat))
                throw new InvalidDataException("Streamed solar irradiance requires RGBAHalf or RGBAFloat.");
            bool compact = source.CompactTiles != Vector2Int.zero;
            int tilesX = compact ? source.CompactTiles.x : source.Counts.x;
            int tilesY = compact ? source.CompactTiles.y : source.Counts.z;
            int layers = compact ? source.Distance.depth : source.Counts.y;
            if (first.width != tilesX * 8L || first.height != tilesY * 8L || first.depth != layers ||
                source.Distance.width != tilesX * 16L || source.Distance.height != tilesY * 16L || source.Distance.depth != layers ||
                source.ProbeData.width != source.Counts.x || source.ProbeData.height != source.Counts.z ||
                source.ProbeData.depth != source.Counts.y || (compact && source.ProbeData.format != TextureFormat.RGBAFloat))
                throw new InvalidDataException("The solar atlases must match the probe layout.");
            for (int i = 0; i < source.Hours.Length; ++i)
            {
                var texture = source.Irradiance[i];
                if (!texture || texture.format != first.format || texture.width != first.width ||
                    texture.height != first.height || texture.depth != first.depth || texture.mipmapCount != 1 ||
                    GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat))
                    throw new InvalidDataException("Solar irradiance keys must have matching linear texture arrays without mipmaps.");
            }
            ValidateHours(source.Hours);

            int layerBytes = checked(first.width * first.height * (first.format == TextureFormat.RGBAHalf ? 8 : 16));
            checked { _ = layerBytes * first.depth; }
            var layerBuffer = new byte[layerBytes];
            return CreateStreamedVolume(source, volumePath, first.width, first.height, first.depth, first.format,
                (i, path) => WriteStreamKey(source.Irradiance[i], path, layerBuffer));
        }

        internal static MaxBakedSolarProbeData ImportCompactVolume(string manifestPath, string volumePath, string atlasFolder)
        {
            ValidateStreamDestination(volumePath);
            if (!AssetDatabase.IsValidFolder(atlasFolder))
                throw new IOException("Choose an atlas folder inside the project.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            var manifest = JsonUtility.FromJson<CompactBakeManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.counts?.Length != 3 || manifest.origin?.Length != 3 ||
                manifest.counts.Any(count => count <= 0) || manifest.origin.Any(value => !IsFinite(value)) ||
                !IsFinite(manifest.spacing) || manifest.spacing <= 0f || !IsFinite(manifest.gamma) || manifest.gamma <= 0f ||
                !IsFinite(manifest.normalBias) || manifest.normalBias < 0f || manifest.columns < 1 || manifest.rows < 1 ||
                manifest.layers < 1 || manifest.layers > SystemInfo.maxTextureArraySlices ||
                manifest.columns > SystemInfo.maxTextureSize / 16 || manifest.rows > SystemInfo.maxTextureSize / 16)
                throw new InvalidDataException("The compact solar TOD manifest has invalid layout or lighting parameters.");
            ValidateHours(manifest.hours);

            var counts = new Vector3Int(manifest.counts[0], manifest.counts[1], manifest.counts[2]);
            if (counts.x > SystemInfo.maxTextureSize || counts.z > SystemInfo.maxTextureSize ||
                counts.y > SystemInfo.maxTextureArraySlices || manifest.active < 0 || manifest.active > (1 << 24) ||
                manifest.active > (long)counts.x * counts.z * counts.y ||
                manifest.active > (long)manifest.columns * manifest.rows * manifest.layers)
                throw new InvalidDataException("The compact solar TOD manifest exceeds the probe or atlas capacity.");

            int width = manifest.columns * 8;
            int height = manifest.rows * 8;
            int keyBytes = checked(width * height * manifest.layers * 8);
            string probePath = Path.Combine(directory, "probe_data.bin");
            string distancePath = Path.Combine(directory, "distance.bin");
            string KeyPath(int index) => Path.Combine(directory, $"irradiance_{index}.bin");
            ValidateRawSize(probePath, (long)counts.x * counts.z * counts.y * 16);
            ValidateRawSize(distancePath, (long)width * 2 * height * 2 * manifest.layers * 4);
            for (int i = 0; i < manifest.hours.Length; ++i) ValidateRawSize(KeyPath(i), keyBytes);

            string probeAsset = atlasFolder + "/ProbeData.asset";
            string distanceAsset = atlasFolder + "/Distance.asset";
            if (File.Exists(probeAsset) || File.Exists(distanceAsset))
                throw new IOException("Choose an empty atlas folder for the compact solar volume.");
            var source = ScriptableObject.CreateInstance<MaxBakedSolarProbeData>();
            try
            {
                source.Counts = counts;
                source.Origin = new Vector3(manifest.origin[0], manifest.origin[1], manifest.origin[2]);
                source.Spacing = Vector3.one * manifest.spacing;
                source.Gamma = manifest.gamma;
                source.NormalBias = manifest.normalBias;
                source.Relocation = manifest.relocation;
                source.Classification = manifest.classification;
                source.CompactTiles = new Vector2Int(manifest.columns, manifest.rows);
                source.Hours = manifest.hours;
                source.ProbeData = ImportRawArray(probePath, probeAsset, counts.x, counts.z, counts.y, 1, TextureFormat.RGBAFloat);
                source.Distance = ImportRawArray(distancePath, distanceAsset, width * 2, height * 2,
                    manifest.layers, 16, TextureFormat.RGHalf);
                return CreateStreamedVolume(source, volumePath, width, height, manifest.layers, TextureFormat.RGBAHalf,
                    (i, destination) => File.Copy(KeyPath(i), destination));
            }
            catch
            {
                if (source.ProbeData) AssetDatabase.DeleteAsset(probeAsset);
                if (source.Distance) AssetDatabase.DeleteAsset(distanceAsset);
                throw;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static void ValidateHours(float[] hours)
        {
            if (hours == null || hours.Length == 0)
                throw new InvalidDataException("The solar volume has no TOD keys.");
            for (int i = 0; i < hours.Length; ++i)
                if (!IsFinite(hours[i]) || (i > 0 && hours[i] <= hours[i - 1]))
                    throw new InvalidDataException("TOD keys must be finite and strictly increasing.");
        }

        private static void ValidateRawSize(string path, long expected)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expected)
                throw new InvalidDataException($"The compact solar bake is missing valid {file.Name} data.");
        }

        private static Texture2DArray ImportRawArray(string source, string asset, int width, int height, int layers,
            int tile, TextureFormat format)
        {
            if (!SystemInfo.SupportsTextureFormat(format))
                throw new NotSupportedException($"The editor cannot import {format} solar atlases.");
            using var reader = new BinaryReader(File.OpenRead(source));
            return BuildAtlas(asset, width, height, layers, tile, format, null, (_, bytes) => ReadLayer(reader, bytes),
                makeNoLongerReadable: true);
        }

        private static void ValidateStreamDestination(string volumePath)
        {
            EnsureAvailable();
            ValidateLocalBakePath(volumePath);
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new NotSupportedException("Streamed solar TOD currently requires Windows.");
            string assetRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (string.IsNullOrEmpty(volumePath) || !volumePath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !Path.GetFullPath(volumePath).StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(volumePath) != ".asset" || File.Exists(volumePath) ||
                !AssetDatabase.IsValidFolder(Path.GetDirectoryName(volumePath).Replace('\\', '/')))
                throw new IOException("Choose a new asset path inside the project.");
        }

        private static MaxBakedSolarProbeData CreateStreamedVolume(MaxBakedSolarProbeData source, string volumePath,
            int width, int height, int layers, TextureFormat format, Action<int, string> writeKey)
        {
            string relativeDirectory = "MaxSRP/SolarTOD/" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Application.streamingAssetsPath, relativeDirectory);
            var volume = ScriptableObject.CreateInstance<MaxBakedSolarProbeData>();
            try
            {
                Directory.CreateDirectory(directory);
                for (int i = 0; i < source.Hours.Length; ++i)
                    writeKey(i, Path.Combine(directory, $"irradiance_{i}.bin"));

                EditorUtility.CopySerialized(source, volume);
                volume.name = Path.GetFileNameWithoutExtension(volumePath);
                volume.Irradiance = null;
                volume.StreamedIrradiance = new MaxBakedSolarProbeData.IrradianceStreamSource
                {
                    RelativeDirectory = relativeDirectory,
                    Width = width,
                    Height = height,
                    Layers = layers,
                    Format = format
                };
                SaveVolume(volume, volumePath, true);
                AssetDatabase.Refresh();
                return volume;
            }
            catch
            {
                if (AssetDatabase.Contains(volume))
                    AssetDatabase.DeleteAsset(volumePath);
                else
                    UnityEngine.Object.DestroyImmediate(volume);
                if (Directory.Exists(directory))
                {
                    for (int i = 0; i < source.Hours.Length; ++i)
                    {
                        string keyPath = Path.Combine(directory, $"irradiance_{i}.bin");
                        File.Delete(keyPath);
                        File.Delete(keyPath + ".meta");
                    }
                    Directory.Delete(directory);
                    File.Delete(directory + ".meta");
                }
                throw;
            }
        }

        private static void WriteStreamKey(Texture2DArray texture, string path, byte[] layerBuffer)
        {
            AsyncGPUReadbackRequest request = default;
            if (!texture.isReadable)
            {
                if (!SystemInfo.supportsAsyncGPUReadback)
                    throw new NotSupportedException("The editor cannot read back this solar irradiance texture.");
                request = AsyncGPUReadback.Request(texture, 0);
                request.WaitForCompletion();
                if (request.hasError || request.layerCount != texture.depth)
                    throw new IOException("Could not read back a solar irradiance texture.");
            }

            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            for (int layer = 0; layer < texture.depth; ++layer)
            {
                var data = texture.isReadable ? texture.GetPixelData<byte>(0, layer) : request.GetData<byte>(layer);
                if (data.Length != layerBuffer.Length)
                    throw new InvalidDataException("A solar irradiance layer has an unexpected byte count.");
                data.CopyTo(layerBuffer);
                file.Write(layerBuffer, 0, layerBuffer.Length);
            }
        }

        private static void SaveFromMenu(string name, Func<string, string, MaxBakedSolarProbeData> create)
        {
            string root = EnsureLocalSolarFolder();
            string volumePath = EditorUtility.SaveFilePanelInProject("Save Baked Solar TOD", name, "asset",
                "The textures will be saved in an adjacent folder.", root);
            if (string.IsNullOrEmpty(volumePath)) return;
            ValidateLocalBakePath(volumePath);
            volumePath = AssetDatabase.GenerateUniqueAssetPath(volumePath);
            string parent = Path.GetDirectoryName(volumePath).Replace('\\', '/');
            string folderGuid = AssetDatabase.CreateFolder(parent, Path.GetFileNameWithoutExtension(volumePath) + " Atlases");
            if (string.IsNullOrEmpty(folderGuid))
                throw new IOException("Could not create the solar probe atlas folder.");
            string atlasFolder = AssetDatabase.GUIDToAssetPath(folderGuid);
            MaxBakedSolarProbeData volume;
            try
            {
                volume = create(volumePath, atlasFolder);
            }
            catch
            {
                AssetDatabase.DeleteAsset(volumePath);
                AssetDatabase.DeleteAsset(atlasFolder);
                throw;
            }
            Selection.activeObject = volume;
            EditorGUIUtility.PingObject(volume);
        }

        private sealed class CompactLayout
        {
            internal readonly int[] Slots;
            internal readonly Vector2Int Tiles;
            internal readonly int Layers;
            internal readonly int Active;

            internal CompactLayout(int width, int height, int layers, Action<int, byte[]> readLayer)
            {
                int plane = checked(width * height);
                Slots = new int[checked(plane * layers)];
                byte[] pixels = new byte[checked(plane * 16)];
                int active = 0;
                for (int layer = 0; layer < layers; layer++)
                {
                    readLayer(layer, pixels);
                    for (int i = 0; i < plane; i++)
                    {
                        float state = BitConverter.ToSingle(pixels, i * 16 + 12);
                        if (state != 0f && state != 1f)
                            throw new InvalidDataException("Expected DDGI probe classification state 0 or 1.");
                        Slots[layer * plane + i] = state == 1f ? -1 : active++;
                    }
                }
                Active = active;
                int maxTiles = SystemInfo.maxTextureSize / 16;
                if (maxTiles < 1) throw new NotSupportedException("The GPU cannot sample a solar probe tile.");
                int tileCount = Math.Max(1, active);
                int columns = Math.Min(maxTiles, Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(tileCount))));
                int rows = Math.Min(maxTiles, (tileCount + columns - 1) / columns);
                Tiles = new Vector2Int(columns, rows);
                Layers = (tileCount + columns * rows - 1) / (columns * rows);
                if (Layers > SystemInfo.maxTextureArraySlices)
                    throw new NotSupportedException("The compact solar atlas exceeds the array layer limit.");
            }

            internal void EncodeSlots(int layer, byte[] pixels)
            {
                int plane = pixels.Length / 16;
                for (int i = 0; i < plane; i++)
                    BitConverter.TryWriteBytes(pixels.AsSpan(i * 16 + 12, 4), (float)(Slots[layer * plane + i] + 1));
            }

            internal void CopyTiles(int layer, byte[] pixels, int width, int height, int tile, int stride,
                NativeArray<byte>[] output)
            {
                int columns = width / tile;
                int plane = columns * (height / tile);
                int tilesPerLayer = Tiles.x * Tiles.y;
                int rowBytes = tile * stride;
                for (int i = 0; i < plane; i++)
                {
                    int slot = Slots[layer * plane + i];
                    if (slot < 0) continue;
                    int inLayer = slot % tilesPerLayer;
                    int sx = (i % columns) * tile;
                    int sy = (i / columns) * tile;
                    int dx = (inLayer % Tiles.x) * tile;
                    int dy = (inLayer / Tiles.x) * tile;
                    for (int row = 0; row < tile; row++)
                        NativeArray<byte>.Copy(pixels, ((sy + row) * width + sx) * stride, output[slot / tilesPerLayer],
                            ((dy + row) * Tiles.x * tile + dx) * stride, rowBytes);
                }
            }
        }

        internal static MaxBakedSolarProbeData CompactVolume(MaxBakedSolarProbeData source, string volumePath, string atlasFolder)
        {
            if (!source || !source.Classification || source.CompactTiles != Vector2Int.zero ||
                source.Irradiance == null || source.Irradiance.Length == 0 ||
                source.Hours == null || source.Hours.Length != source.Irradiance.Length)
                throw new InvalidDataException("Select a classified dense solar probe volume.");
            if (AssetDatabase.LoadMainAssetAtPath(volumePath))
                throw new IOException("Choose a new destination for the compact solar volume.");

            void CheckTexture(Texture2DArray texture, int tile)
            {
                if (!texture || !texture.isReadable || texture.mipmapCount != 1 ||
                    texture.width != source.Counts.x * tile || texture.height != source.Counts.z * tile ||
                    texture.depth != source.Counts.y ||
                    (texture.format != TextureFormat.RGBAFloat && (tile != 16 || texture.format != TextureFormat.RGFloat)))
                    throw new InvalidDataException("Compaction requires readable, full-precision solar atlases with matching grid dimensions.");
            }

            CheckTexture(source.ProbeData, 1);
            CheckTexture(source.Distance, 16);
            foreach (var texture in source.Irradiance) CheckTexture(texture, 8);
            var layout = new CompactLayout(source.Counts.x, source.Counts.z, source.Counts.y,
                (layer, bytes) => source.ProbeData.GetPixelData<byte>(0, layer).CopyTo(bytes));

            Texture2DArray CopyAtlas(Texture2DArray texture, string name, int tile) =>
                BuildAtlas(atlasFolder + "/" + name + ".asset", texture.width, texture.height, texture.depth, tile,
                    texture.format, layout, (layer, bytes) => texture.GetPixelData<byte>(0, layer).CopyTo(bytes));

            var volume = ScriptableObject.CreateInstance<MaxBakedSolarProbeData>();
            try
            {
                EditorUtility.CopySerialized(source, volume);
                volume.name = Path.GetFileNameWithoutExtension(volumePath);
                volume.CompactTiles = layout.Tiles;
                volume.ProbeData = CopyAtlas(source.ProbeData, "ProbeData", 1);
                volume.Distance = CopyAtlas(source.Distance, "Distance", 16);
                volume.Irradiance = source.Irradiance.Select((texture, i) => CopyAtlas(texture, $"Irradiance_{i}", 8)).ToArray();
                SaveVolume(volume, volumePath, true);
                Debug.Log($"Packed {layout.Active:N0}/{layout.Slots.Length:N0} active solar probes to {volumePath}.");
                return volume;
            }
            finally
            {
                if (!AssetDatabase.Contains(volume))
                    UnityEngine.Object.DestroyImmediate(volume);
            }
        }

        internal static void EnsureAvailable()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Lightmapping.isRunning)
                throw new InvalidOperationException("Import solar probe data while the editor and lightmapper are idle.");
            if (!BitConverter.IsLittleEndian || !SystemInfo.supports2DArrayTextures)
                throw new NotSupportedException("Solar probe data requires little-endian texture array support.");
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Linear))
                throw new NotSupportedException("Solar probe data requires linearly filterable RGBAFloat textures.");
        }

        internal static BakeReport ReadReport(string path)
        {
            BakeReport report = JsonUtility.FromJson<BakeReport>(File.ReadAllText(path));
            if (report == null || !report.baked_atlases_written || report.vertices <= 0 || report.triangles <= 0
                || report.grid == null || report.grid.Length != 3
                || report.grid.Any(count => count < 2 || count > 65536)
                || (long)report.grid[0] * report.grid[1] * report.grid[2] != report.probes
                || report.probes > 65536
                || 16 * Mathf.Max(report.grid[0], report.grid[2]) > SystemInfo.maxTextureSize
                || report.grid[1] > SystemInfo.maxTextureArraySlices
                || !IsFinite(report.spacing) || report.spacing <= 0
                || !IsFinite(report.gamma) || report.gamma <= 0
                || !IsFinite(report.normal_bias) || report.normal_bias < 0 || report.view_bias != 0
                || report.keys == null || report.keys.Length == 0 || report.keys.Length > 25)
                throw new InvalidDataException("Invalid static solar probe report " + path);

            ReadVector(report.volume_origin, "volume_origin");
            float previousHour = float.NegativeInfinity;
            foreach (Key key in report.keys)
            {
                if (key == null || !IsFinite(key.hour) || key.hour < 6f || key.hour > 18f || key.hour <= previousHour)
                    throw new InvalidDataException("Expected strictly increasing TOD keys from 6 to 18 in " + path);
                previousHour = key.hour;
            }
            return report;
        }

        internal static void ValidateAtlases(string atlasDirectory, BakeReport report)
        {
            ForEachAtlas(report, (name, file, tile, distance) =>
            {
                string source = Path.Combine(atlasDirectory, file);
                using (BinaryReader reader = new BinaryReader(File.OpenRead(source)))
                    CheckHeader(reader, source, report.grid[0] * tile, report.grid[2] * tile, report.grid[1]);
            });
        }

        internal static MaxBakedSolarProbeData ImportVolume(string volumePath, string atlasFolder, string atlasDirectory,
            BakeReport report, bool compact = true)
        {
            CompactLayout layout = null;
            if (compact && report.classification)
            {
                string source = Path.Combine(atlasDirectory, "shared_probe_data.ngatlas");
                using var reader = new BinaryReader(File.OpenRead(source));
                CheckHeader(reader, source, report.grid[0], report.grid[2], report.grid[1]);
                var candidate = new CompactLayout(report.grid[0], report.grid[2], report.grid[1],
                    (_, pixels) => ReadLayer(reader, pixels));
                if (candidate.Active < report.probes) layout = candidate;
            }

            var volume = AssetDatabase.LoadAssetAtPath<MaxBakedSolarProbeData>(volumePath);
            bool create = !volume;
            if (create)
                volume = ScriptableObject.CreateInstance<MaxBakedSolarProbeData>();

            bool rgFloat = SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Sample)
                && SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Linear);
            Texture2DArray distanceAtlas = null;
            Texture2DArray probeAtlas = null;
            var irradiance = new Texture2DArray[report.keys.Length];
            int index = 0;
            try
            {
                ForEachAtlas(report, (name, file, tile, distance) =>
                {
                    string source = Path.Combine(atlasDirectory, file);
                    Texture2DArray texture = ImportAtlas(source, atlasFolder + "/" + name + ".asset",
                        report.grid[0] * tile, report.grid[2] * tile, report.grid[1], tile, distance && rgFloat, layout);
                    if (distance)
                        distanceAtlas = texture;
                    else if (name == "ProbeData")
                        probeAtlas = texture;
                    else
                        irradiance[index++] = texture;
                });

                volume.Distance = distanceAtlas;
                volume.ProbeData = probeAtlas;
                volume.Origin = ReadVector(report.volume_origin, "volume_origin");
                volume.Spacing = Vector3.one * report.spacing;
                volume.Counts = new Vector3Int(report.grid[0], report.grid[1], report.grid[2]);
                volume.Gamma = report.gamma;
                volume.NormalBias = report.normal_bias;
                volume.Relocation = report.relocation;
                volume.Classification = report.classification;
                volume.CompactTiles = layout?.Tiles ?? Vector2Int.zero;
                volume.Hours = report.keys.Select(key => key.hour).ToArray();
                volume.Irradiance = irradiance;
                volume.StreamedIrradiance = null;
                SaveVolume(volume, volumePath, create);
                Debug.Log($"Imported {layout?.Active ?? report.probes:N0}/{report.probes:N0} solar probes " +
                    $"with {report.keys.Length} TOD keys to {volumePath}.");
                return volume;
            }
            finally
            {
                if (create && !AssetDatabase.Contains(volume))
                    UnityEngine.Object.DestroyImmediate(volume);
            }
        }

        private static void ForEachAtlas(BakeReport report, Action<string, string, int, bool> process)
        {
            process("Distance", "shared_distance.ngatlas", 16, true);
            process("ProbeData", "shared_probe_data.ngatlas", 1, false);
            for (int i = 0; i < report.keys.Length; i++)
                process($"Irradiance_{i}", $"official_irradiance_{i}.ngatlas", 8, false);
        }

        private static void CheckHeader(BinaryReader reader, string source, int width, int height, int layers)
        {
            const int headerBytes = 24;
            const int texelBytes = 16;
            if (reader.BaseStream.Length != headerBytes + (long)width * height * layers * texelBytes
                || Encoding.ASCII.GetString(reader.ReadBytes(8)) != "NGATLAS1"
                || reader.ReadUInt32() != (uint)width || reader.ReadUInt32() != (uint)height
                || reader.ReadUInt32() != (uint)layers || reader.ReadUInt32() != 4)
                throw new InvalidDataException("Invalid solar probe atlas header or dimensions " + source);
        }

        private static string EnsureLocalSolarFolder()
        {
            string folder = "Assets";
            foreach (string part in new[] { "LocalGIBakes", "Bistro", "SolarGI" })
            {
                if (!AssetDatabase.IsValidFolder(folder + "/" + part))
                    AssetDatabase.CreateFolder(folder, part);
                folder += "/" + part;
            }
            return folder;
        }

        private static void ValidateLocalBakePath(string path)
        {
            string root = Path.GetFullPath("Assets/LocalGIBakes") + Path.DirectorySeparatorChar;
            if (string.IsNullOrEmpty(path) ||
                !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Generated solar GI data must be saved under Assets/LocalGIBakes (excluded from Git).");
        }

        private static void SaveVolume(MaxBakedSolarProbeData volume, string path, bool create)
        {
            ValidateLocalBakePath(path);
            if (create)
            {
                AssetDatabase.CreateAsset(volume, path);
                if (!AssetDatabase.Contains(volume))
                    throw new IOException("Could not save the solar probe volume at " + path);
            }
            EditorUtility.SetDirty(volume);
            AssetDatabase.SaveAssetIfDirty(volume);
        }

        private static void ReadLayer(BinaryReader reader, byte[] pixels)
        {
            int read = 0;
            while (read < pixels.Length)
            {
                int count = reader.Read(pixels, read, pixels.Length - read);
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
        }

        private static Texture2DArray ImportAtlas(string source, string destination, int width, int height, int layers,
            int tile, bool twoChannels, CompactLayout layout)
        {
            using var reader = new BinaryReader(File.OpenRead(source));
            CheckHeader(reader, source, width, height, layers);
            TextureFormat format = twoChannels ? TextureFormat.RGFloat : TextureFormat.RGBAFloat;
            byte[] rgba = twoChannels ? new byte[checked(width * height * 16)] : null;
            return BuildAtlas(destination, width, height, layers, tile, format, layout, (_, pixels) =>
            {
                ReadLayer(reader, rgba ?? pixels);
                if (rgba != null)
                    for (int i = 0; i < width * height; i++)
                        Buffer.BlockCopy(rgba, i * 16, pixels, i * 8, 8);
            });
        }

        private static Texture2DArray BuildAtlas(string destination, int width, int height, int layers, int tile,
            TextureFormat format, CompactLayout layout, Action<int, byte[]> readLayer, bool makeNoLongerReadable = false)
        {
            bool packTiles = layout != null && tile > 1;
            int stride = format switch
            {
                TextureFormat.RGHalf => 4,
                TextureFormat.RGFloat => 8,
                TextureFormat.RGBAFloat => 16,
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            var texture = new Texture2DArray(packTiles ? layout.Tiles.x * tile : width,
                packTiles ? layout.Tiles.y * tile : height, packTiles ? layout.Layers : layers, format, false, true)
            {
                name = Path.GetFileNameWithoutExtension(destination),
                filterMode = tile == 1 ? FilterMode.Point : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            try
            {
                var pixels = new byte[checked(width * height * stride)];
                NativeArray<byte>[] packed = null;
                if (packTiles)
                {
                    packed = new NativeArray<byte>[layout.Layers];
                    for (int layer = 0; layer < packed.Length; layer++)
                        packed[layer] = texture.GetPixelData<byte>(0, layer);
                }
                // Source layers use X columns, Z rows and Y slices. A compact
                // tile includes its original one-texel border on all four sides.
                for (int layer = 0; layer < layers; layer++)
                {
                    readLayer(layer, pixels);
                    if (packTiles)
                        layout.CopyTiles(layer, pixels, width, height, tile, stride, packed);
                    else
                    {
                        if (layout != null) layout.EncodeSlots(layer, pixels);
                        texture.SetPixelData(pixels, 0, layer);
                    }
                }
                texture.Apply(false, makeNoLongerReadable);
                var previous = AssetDatabase.LoadAssetAtPath<Texture2DArray>(destination);
                if (previous)
                {
                    EditorUtility.CopySerialized(texture, previous);
                    EditorUtility.SetDirty(previous);
                    AssetDatabase.SaveAssetIfDirty(previous);
                    return previous;
                }
                AssetDatabase.CreateAsset(texture, destination);
                if (!AssetDatabase.Contains(texture))
                    throw new IOException("Could not save the solar probe atlas at " + destination);
                return texture;
            }
            finally
            {
                if (!AssetDatabase.Contains(texture))
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static Vector3 ReadVector(float[] values, string field)
        {
            if (values == null || values.Length != 3 || values.Any(value => !IsFinite(value)))
                throw new InvalidDataException("Invalid solar probe report field " + field);
            return new Vector3(values[0], values[1], values[2]);
        }
    }
}
