using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

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

                string volumePath = EditorUtility.SaveFilePanelInProject("Save Baked Solar TOD", "Solar TOD", "asset",
                    "Choose a destination for the probe volume. Its textures will be saved in an adjacent folder.");
                if (string.IsNullOrEmpty(volumePath)) return;
                volumePath = AssetDatabase.GenerateUniqueAssetPath(volumePath);

                string parent = Path.GetDirectoryName(volumePath).Replace('\\', '/');
                string folderGuid = AssetDatabase.CreateFolder(parent, Path.GetFileNameWithoutExtension(volumePath) + " Atlases");
                if (string.IsNullOrEmpty(folderGuid))
                    throw new IOException("Could not create the solar probe atlas folder.");
                string atlasFolder = AssetDatabase.GUIDToAssetPath(folderGuid);
                MaxBakedSolarProbeData volume;
                try
                {
                    volume = ImportVolume(volumePath, atlasFolder, atlasDirectory, report);
                }
                catch
                {
                    AssetDatabase.DeleteAsset(volumePath);
                    AssetDatabase.DeleteAsset(atlasFolder);
                    throw;
                }

                Selection.activeObject = volume;
                EditorGUIUtility.PingObject(volume);
                Debug.Log($"Imported {report.probes:N0} solar probes with {report.keys.Length} TOD keys to {volumePath}.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Solar TOD import failed", exception.Message, "OK");
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
                if (key == null || !IsFinite(key.hour) || key.hour < 9f || key.hour > 15f || key.hour <= previousHour)
                    throw new InvalidDataException("Expected strictly increasing TOD keys from 9 to 15 in " + path);
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
            BakeReport report)
        {
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
                        report.grid[0] * tile, report.grid[2] * tile, report.grid[1],
                        distance && rgFloat, name == "ProbeData");
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
                volume.Hours = report.keys.Select(key => key.hour).ToArray();
                volume.Irradiance = irradiance;
                if (create)
                {
                    AssetDatabase.CreateAsset(volume, volumePath);
                    if (!AssetDatabase.Contains(volume))
                        throw new IOException("Could not save the solar probe volume at " + volumePath);
                }
                EditorUtility.SetDirty(volume);
                AssetDatabase.SaveAssetIfDirty(volume);
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

        private static Texture2DArray ImportAtlas(string source, string destination, int width, int height, int layers,
            bool twoChannels, bool point)
        {
            TextureFormat format = twoChannels ? TextureFormat.RGFloat : TextureFormat.RGBAFloat;
            var texture = new Texture2DArray(width, height, layers, format, false, true)
            {
                name = Path.GetFileNameWithoutExtension(destination),
                filterMode = point ? FilterMode.Point : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(source)))
                {
                    CheckHeader(reader, source, width, height, layers);
                    int texels = width * height;
                    var rgba = new byte[texels * 16];
                    byte[] rg = twoChannels ? new byte[texels * 8] : null;
                    // NGATLAS uses X for columns, Z for rows and Y for slices.
                    for (int layer = 0; layer < layers; layer++)
                    {
                        int read = 0;
                        while (read < rgba.Length)
                        {
                            int count = reader.Read(rgba, read, rgba.Length - read);
                            if (count == 0)
                                throw new EndOfStreamException(source);
                            read += count;
                        }
                        if (rg != null)
                            for (int i = 0; i < texels; i++)
                                Buffer.BlockCopy(rgba, i * 16, rg, i * 8, 8);
                        texture.SetPixelData(rg ?? rgba, 0, layer);
                    }
                }
                texture.Apply(false, false);
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
