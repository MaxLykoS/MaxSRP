using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaxTerrain
{
    // Metadata only. No unsupported platform texture is referenced/auto-loaded.
    // Both formats contain the same sixteen stable IDs and packed source data.
    public sealed class MeshMaterialSet : ScriptableObject
    {
        public const int Capacity = 16;
        public const int Version = 1;
        [Serializable] public sealed class Variant
        {
            public TextureFormat format;
            public string albedoHeightFile, normalRoughAoFile;
            public string albedoHeightSha256, normalRoughAoSha256;
            public long arrayBytes;
        }
        public int version = Version, resolution = 1024;
        public string relativeDirectory = "DirectDeltaSurface16";
        public string sourceFingerprint, sourceLicense = "CC0-1.0 / Poly Haven";
        // 非空表示材质全局ID的制作层序；必须与控制图元数据一致。
        public string layerOrder;
        public string[] names = new string[Capacity];
        public Vector4[] uvPerMetre = new Vector4[Capacity];
        // normal strength, actual source resolution, last mip, authored flag.
        public Vector4[] parameters = new Vector4[Capacity];
        public Variant[] variants = new Variant[2];
        public int MipCount => CountMips(resolution);
        public static int CountMips(int size) { int count = 1; while ((size >>= 1) > 0) ++count; return count; }
        public static int BlockSide(TextureFormat format)
        {
            if (format == TextureFormat.BC7) return 4;
            if (format == TextureFormat.ASTC_6x6) return 6;
            throw new ArgumentOutOfRangeException(nameof(format), "Only BC7 and ASTC 6x6 are valid material encodings.");
        }
        public static int MipBytes(int size, TextureFormat format)
        { int block = BlockSide(format), count = (size + block - 1) / block; return checked(count * count * 16); }
        public static long ArrayBytes(int size, TextureFormat format)
        { long result = 0; for (int mip = 0; mip < CountMips(size); ++mip) result += MipBytes(Math.Max(1,size >> mip),format); return result * Capacity; }

        public Variant GetVariant(TextureFormat format)
        {
            foreach (var variant in variants) if (variant != null && variant.format == format) return variant;
            throw new InvalidDataException("Missing compressed material payload for " + format);
        }
        public void Validate()
        {
            if (version != Version || resolution < 4 || resolution > 2048 || (resolution & (resolution-1)) != 0 ||
                names == null || names.Length != Capacity || uvPerMetre == null || uvPerMetre.Length != Capacity ||
                parameters == null || parameters.Length != Capacity || variants == null || variants.Length != 2 || string.IsNullOrEmpty(sourceFingerprint))
                throw new InvalidDataException("Invalid sixteen-material source contract.");
            SafeRelative(relativeDirectory);
            for (int id = 0; id < Capacity; ++id)
                if (string.IsNullOrEmpty(names[id]) || uvPerMetre[id].x <= 0 || uvPerMetre[id].y <= 0 ||
                    !Finite(uvPerMetre[id]) || !Finite(parameters[id]) || parameters[id].x < 0 ||
                    parameters[id].y != resolution || parameters[id].z != MipCount-1 || parameters[id].w != 1)
                    throw new InvalidDataException("Every one of the sixteen material IDs must be authored with valid UV/data: " + id);
            foreach (TextureFormat format in new[] { TextureFormat.BC7, TextureFormat.ASTC_6x6 })
            {
                Variant v = GetVariant(format);
                if (v.arrayBytes != ArrayBytes(resolution,format) || !Hash(v.albedoHeightSha256) || !Hash(v.normalRoughAoSha256))
                    throw new InvalidDataException("Compressed material byte layout/identity mismatch.");
                SafeRelative(v.albedoHeightFile); SafeRelative(v.normalRoughAoFile);
            }
        }
        static bool Finite(Vector4 value) => F(value.x) && F(value.y) && F(value.z) && F(value.w);
        static bool F(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Hash(string text)
        { if (text == null || text.Length != 64) return false; foreach (char c in text) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
        public static void SafeRelative(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) throw new InvalidDataException("Material payload paths must be relative.");
            foreach (string part in path.Replace('\\','/').Split('/')) if (part.Length == 0 || part == "." || part == "..") throw new InvalidDataException("Invalid material payload path.");
        }
    }

    // One resident pair is bound to BOTH game materials. All mips/IDs are present;
    // there is no DTA slot map, source eviction, high/low split or RGBA fallback.
    public sealed class MeshMaterialResident : IDisposable
    {
        public MeshMaterialSet Set { get; }
        public TextureFormat Format { get; }
        public Texture2DArray AlbedoHeight { get; private set; }
        public Texture2DArray NormalRoughAo { get; private set; }
        public long GpuBytes => AlbedoHeight ? MeshMaterialSet.ArrayBytes(Set.resolution,Format) * 2 : 0;
        public MeshMaterialResident(MeshMaterialSet set, TextureFormat? requestedFormat = null)
        {
            if (!set) throw new ArgumentNullException(nameof(set)); Set = set; set.Validate();
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            Format = requestedFormat ?? TextureFormat.ASTC_6x6;
#else
            Format = requestedFormat ?? TextureFormat.BC7;
#endif
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.SupportsTextureFormat(Format))
                throw new NotSupportedException("This surface requires native GPU " + Format + " arrays; an uncompressed fallback is deliberately not used.");
            var variant = set.GetVariant(Format);
            try
            {
                AlbedoHeight = Load(variant.albedoHeightFile,variant.albedoHeightSha256,true,variant.arrayBytes);
                NormalRoughAo = Load(variant.normalRoughAoFile,variant.normalRoughAoSha256,false,variant.arrayBytes);
            }
            catch { Dispose(); throw; }
        }
        Texture2DArray Load(string file, string hash, bool srgb, long expectedBytes)
        {
            string relative = Set.relativeDirectory.TrimEnd('/') + "/" + file;
            byte[] bytes = ReadStreaming(relative);
            if (bytes.LongLength != expectedBytes || Sha256(bytes) != hash) throw new InvalidDataException("Material payload differs from its baked identity: " + file);
            var texture = new Texture2DArray(Set.resolution,Set.resolution,MeshMaterialSet.Capacity,Format,true,!srgb)
            { name = srgb ? "Surface16 compressed Albedo Height" : "Surface16 compressed Normal Roughness AO", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                int offset = 0;
                for (int layer = 0; layer < MeshMaterialSet.Capacity; ++layer)
                    for (int mip = 0; mip < Set.MipCount; ++mip)
                    { texture.SetPixelData(bytes,mip,layer,offset); offset += MeshMaterialSet.MipBytes(Math.Max(1,Set.resolution >> mip),Format); }
                if (offset != bytes.Length) throw new InvalidDataException("Material block stream did not fill exactly sixteen complete mip chains.");
                texture.Apply(false,true);
                if (texture.width != Set.resolution || texture.height != Set.resolution || texture.depth != MeshMaterialSet.Capacity || texture.mipmapCount != Set.MipCount ||
                    texture.format != Format || !GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat) ||
                    GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) != srgb || texture.isReadable)
                    throw new InvalidOperationException("Unity changed the requested compressed material format or retained its CPU mirror.");
                return texture;
            }
            catch { UnityEngine.Object.DestroyImmediate(texture); throw; }
        }
        public void Bind(Material material)
        {
            if (!AlbedoHeight || !NormalRoughAo) throw new ObjectDisposedException(nameof(MeshMaterialResident));
            material.SetTexture("_DDAlbedoHeight",AlbedoHeight); material.SetTexture("_DDNormalRoughAo",NormalRoughAo);
            material.SetVectorArray("_DDMaterialUv",Set.uvPerMetre); material.SetVectorArray("_DDMaterialInfo",Set.parameters);
        }
        public static string Sha256(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        static byte[] ReadStreaming(string relative)
        {
            MeshMaterialSet.SafeRelative(relative);
            string root = Application.streamingAssetsPath.Replace('\\','/');
            if (!root.StartsWith("jar:file:",StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(Path.Combine(root,relative));
            // Android packaged assets: read the exact APK entry, not a fake disk path.
            int split = root.IndexOf("!/",StringComparison.Ordinal);
            if (split < 0) throw new InvalidDataException("Invalid Android StreamingAssets URI.");
            string archive = new Uri(root.Substring(4,split-4)).LocalPath;
            string entryName = root.Substring(split+2).TrimEnd('/') + "/" + relative;
            using (var stream = File.OpenRead(archive)) using (var zip = new ZipArchive(stream,ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(entryName); if (entry == null) throw new FileNotFoundException("Material APK entry missing.",entryName);
                using (var input = entry.Open()) using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
        public void Dispose()
        { Destroy(AlbedoHeight); AlbedoHeight = null; Destroy(NormalRoughAo); NormalRoughAo = null; }
        static void Destroy(UnityEngine.Object value)
        { if (!value) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
    }
}
