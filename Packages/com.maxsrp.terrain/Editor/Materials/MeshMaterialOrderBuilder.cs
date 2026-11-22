using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    // [工程制作约定] 只在离线构建时整理16个全局ID的覆盖层序。
    // 泥土/林地在下，草在上；这不是按独立Height贴图的0..1数值排序。
    // 运行时ID仍直接等于数组slice，没有DTA、槽映射或额外的ID查表。
    public static class MeshMaterialOrderBuilder
    {
        public const string AssetPath = "Assets/Samples/MaxTerrain/Materials/Surface16Ordered.asset";
        public const string DirectoryName = "DirectDeltaSurface16Ordered";
        public const string IdentityFileName = "ordered-material-identity.json";

        [Serializable] sealed class Mapping
        {
            public int id, sourceId;
            public string name;
        }

        [Serializable] sealed class LayerIdentity
        {
            public int id, sourceId, mipCount, bytes;
            public string name, format, channel, sourceSha256, orderedSha256;
            public bool byteIdentical;
        }

        [Serializable] sealed class BuildIdentity
        {
            public string scope = "Offline permutation of complete compressed layer mip chains; no decoding or recompression. Not mobile GPU validation.";
            public string layerOrder, sourceAsset, sourceFingerprint, orderedFingerprint;
            public List<Mapping> materials = new List<Mapping>();
            public List<LayerIdentity> layers = new List<LayerIdentity>();
        }

        public static MeshMaterialSet Build(MeshMaterialSet source)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            source.Validate();
            if (!string.IsNullOrEmpty(source.layerOrder))
                throw new InvalidDataException("Layer ordering requires the original download-order source asset; do not order an already ordered set again.");
            if (string.Equals(source.relativeDirectory, DirectoryName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The ordered output must not overwrite its source payloads.");
            MeshMaterialBuilder.ValidatePayloads(source);

            string fingerprint = Fingerprint(source);
            var previous = AssetDatabase.LoadMainAssetAtPath(AssetPath);
            if (previous)
            {
                var existing = previous as MeshMaterialSet;
                if (!existing || existing.sourceFingerprint != fingerprint || existing.resolution != source.resolution ||
                    existing.relativeDirectory != DirectoryName || existing.layerOrder != MeshMaterialLayerOrder.Recipe)
                    throw new IOException("Preserve the existing ordered material asset before changing its source or layer order: " + AssetPath);
                existing.Validate();
                ValidateMetadataOrder(existing, source);
                MeshMaterialBuilder.ValidatePayloads(existing);
                return existing;
            }

            string directory = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, DirectoryName));
            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length != 0)
                throw new IOException("Preserve existing ordered payloads before rebuilding changed or partial sources: " + directory);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));

            var ordered = ScriptableObject.CreateInstance<MeshMaterialSet>();
            ordered.version = source.version;
            ordered.resolution = source.resolution;
            ordered.relativeDirectory = DirectoryName;
            ordered.sourceFingerprint = fingerprint;
            ordered.sourceLicense = source.sourceLicense;
            ordered.layerOrder = MeshMaterialLayerOrder.Recipe;
            var identity = new BuildIdentity
            {
                layerOrder = MeshMaterialLayerOrder.Recipe,
                sourceAsset = AssetDatabase.GetAssetPath(source),
                sourceFingerprint = source.sourceFingerprint,
                orderedFingerprint = fingerprint
            };

            bool saved = false;
            try
            {
                // 名称、UV和法线强度等参数必须随真实压缩slice一起重排。
                // 不能只改控制图ID或名称，否则ID指向的仍是错误素材。
                for (int id = 0; id < MeshMaterialSet.LegacyCount; ++id)
                {
                    int sourceId = MeshMaterialLayerOrder.SourceId(id);
                    ordered.names[id] = source.names[sourceId];
                    ordered.uvPerMetre[id] = source.uvPerMetre[sourceId];
                    ordered.parameters[id] = source.parameters[sourceId];
                    identity.materials.Add(new Mapping { id = id, sourceId = sourceId, name = ordered.names[id] });
                }

                int variantIndex = 0;
                foreach (TextureFormat format in new[] { TextureFormat.BC7, TextureFormat.ASTC_6x6 })
                {
                    MeshMaterialSet.Variant original = source.GetVariant(format);
                    string suffix = format == TextureFormat.BC7 ? "bc7" : "astc6x6";
                    string ahFile = "albedo-height." + suffix + ".bin";
                    string nraoFile = "normal-rough-ao." + suffix + ".bin";
                    // 文件布局原本就是layer-major：每个材质的一整条mip链连续存储。
                    // 直接搬运完整链，不解码、不重压缩，BC7/ASTC的每个block保持原字节。
                    byte[] ah = Reorder(source, original, original.albedoHeightFile, original.albedoHeightSha256, "AlbedoHeight", identity);
                    byte[] nrao = Reorder(source, original, original.normalRoughAoFile, original.normalRoughAoSha256, "NormalRoughnessAO", identity);
                    File.WriteAllBytes(Path.Combine(directory, ahFile), ah);
                    File.WriteAllBytes(Path.Combine(directory, nraoFile), nrao);
                    ordered.variants[variantIndex++] = new MeshMaterialSet.Variant
                    {
                        format = format,
                        albedoHeightFile = ahFile,
                        normalRoughAoFile = nraoFile,
                        albedoHeightSha256 = MeshMaterialResident.Sha256(ah),
                        normalRoughAoSha256 = MeshMaterialResident.Sha256(nrao),
                        arrayBytes = original.arrayBytes
                    };
                }

                ordered.Validate();
                ValidateMetadataOrder(ordered, source);
                MeshMaterialBuilder.ValidatePayloads(ordered);
                File.WriteAllText(Path.Combine(directory, IdentityFileName), JsonUtility.ToJson(identity, true), new UTF8Encoding(false));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.CreateAsset(ordered, AssetPath);
                saved = true;
                EditorUtility.SetDirty(ordered);
                AssetDatabase.SaveAssets();
                return ordered;
            }
            finally
            {
                if (!saved) Object.DestroyImmediate(ordered);
            }
        }

        static byte[] Reorder(MeshMaterialSet source, MeshMaterialSet.Variant variant, string sourceFile,
            string expectedHash, string channel, BuildIdentity identity)
        {
            MeshMaterialSet.SafeRelative(sourceFile);
            string path = Path.Combine(Application.streamingAssetsPath, source.relativeDirectory, sourceFile);
            byte[] original = File.ReadAllBytes(path);
            if (original.LongLength != variant.arrayBytes || MeshMaterialResident.Sha256(original) != expectedHash)
                throw new InvalidDataException("Original compressed payload has changed: " + sourceFile);
            int layerBytes = checked((int)(MeshMaterialSet.ArrayBytes(source.resolution, variant.format) / MeshMaterialSet.LegacyCount));
            if (original.Length != checked(layerBytes * MeshMaterialSet.LegacyCount))
                throw new InvalidDataException("Original layer-major payload does not contain sixteen complete mip chains.");
            var result = new byte[original.Length];
            for (int id = 0; id < MeshMaterialSet.LegacyCount; ++id)
            {
                int sourceId = MeshMaterialLayerOrder.SourceId(id);
                int sourceOffset = sourceId * layerBytes, targetOffset = id * layerBytes;
                Buffer.BlockCopy(original, sourceOffset, result, targetOffset, layerBytes);
                // 独立核对每个搬运后的完整mip链，身份报告可追溯新ID对应的真实素材。
                string sourceHash = HashRange(original, sourceOffset, layerBytes);
                string orderedHash = HashRange(result, targetOffset, layerBytes);
                if (sourceHash != orderedHash) throw new InvalidDataException("Compressed layer changed during ordering: " + id);
                identity.layers.Add(new LayerIdentity
                {
                    id = id, sourceId = sourceId, name = source.names[sourceId],
                    format = variant.format == TextureFormat.BC7 ? "BC7" : "ASTC_6x6",
                    channel = channel, mipCount = source.MipCount, bytes = layerBytes,
                    sourceSha256 = sourceHash, orderedSha256 = orderedHash, byteIdentical = true
                });
            }
            return result;
        }

        static string HashRange(byte[] data, int offset, int count)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(data, offset, count)).Replace("-", "").ToLowerInvariant();
        }

        static string Fingerprint(MeshMaterialSet source)
        {
            var text = new StringBuilder(MeshMaterialLayerOrder.Recipe).Append('|').Append(source.sourceFingerprint)
                .Append('|').Append(source.resolution).Append('|').Append(source.relativeDirectory);
            foreach (TextureFormat format in new[] { TextureFormat.BC7, TextureFormat.ASTC_6x6 })
            {
                MeshMaterialSet.Variant variant = source.GetVariant(format);
                text.Append('|').Append(variant.albedoHeightSha256).Append('|').Append(variant.normalRoughAoSha256);
            }
            for (int id = 0; id < MeshMaterialSet.LegacyCount; ++id)
            {
                int sourceId = MeshMaterialLayerOrder.SourceId(id);
                text.Append('|').Append(id).Append(':').Append(sourceId).Append(':').Append(source.names[sourceId]);
                Append(text, source.uvPerMetre[sourceId]);
                Append(text, source.parameters[sourceId]);
            }
            return MeshMaterialResident.Sha256(Encoding.UTF8.GetBytes(text.ToString()));
        }

        static void Append(StringBuilder text, Vector4 value)
        {
            for (int component = 0; component < 4; ++component)
                text.Append('|').Append(value[component].ToString("R", CultureInfo.InvariantCulture));
        }

        static void ValidateMetadataOrder(MeshMaterialSet ordered, MeshMaterialSet source)
        {
            for (int id = 0; id < MeshMaterialSet.LegacyCount; ++id)
            {
                int sourceId = MeshMaterialLayerOrder.SourceId(id);
                if (ordered.names[id] != source.names[sourceId] || ordered.uvPerMetre[id] != source.uvPerMetre[sourceId] ||
                    ordered.parameters[id] != source.parameters[sourceId])
                    throw new InvalidDataException("Ordered material metadata no longer matches its source slice: " + id);
            }
        }
    }
}
