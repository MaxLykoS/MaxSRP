using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>SVT 预烘焙页的固定索引。每次读取独占一个文件流。</summary>
    public sealed class MeshWorldVtArchive
    {
        public const string DefaultRelativeDirectory = "DirectDeltaWorldSvt03";
        public const string ManifestName = "manifest.json";
        public const int FormatVersion = 4;
        public const int Bc3FormatVersion = 5;
        public const string Bc3Subdirectory = "BC3";
        public const int PageBytes = MeshWorldVirtualTexture.ChannelBytes * 2;

        [Serializable]
        public sealed class Manifest
        {
            public int version, interior, border, firstMip, rootMip, bytesPerPage, worldSize, mip0PagesPerAxis;
            public string albedoFormat, dataFormat, recipe, payloadSha256;
            public PageEntry[] pages;
        }

        [Serializable]
        public sealed class PageEntry
        {
            public int key;
            public string sha256;
        }

        readonly string payloadPath;
        readonly Dictionary<uint, int> indexes = new Dictionary<uint, int>();
        readonly Manifest manifest;
        readonly MeshWorldVtProducer producer;

        public int PageCount => manifest.pages.Length;
        public int BytesPerPage { get; }
        public long PayloadBytes => (long)PageCount * BytesPerPage;
        public string RecipeIdentity => manifest.recipe;

        public MeshWorldVtArchive(string archiveDirectory, MeshWorldVtProducer producer, Material settings, bool useBc3 = false)
        {
            this.producer = producer ?? throw new ArgumentNullException(nameof(producer));
            BytesPerPage = useBc3 ? MeshWorldVtBc3.PageBytes : PageBytes;
            var albedoFormat = useBc3 ? MeshWorldVtBc3.ColorFormat : MeshWorldVirtualTexture.AlbedoFormat;
            var dataFormat = useBc3 ? MeshWorldVtBc3.DataFormat : MeshWorldVirtualTexture.DataFormat;
            string indexPath = Path.Combine(archiveDirectory, ManifestName);
            manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(indexPath));
            int expectedVersion = useBc3 ? Bc3FormatVersion : FormatVersion;
            if (manifest == null || manifest.version != expectedVersion ||
                manifest.worldSize != MeshHeightLayout.WorldSize || manifest.mip0PagesPerAxis != MeshWorldVirtualTexture.PagesPerAxis(0) ||
                manifest.interior != MeshWorldVirtualTexture.PageInterior || manifest.border != MeshWorldVirtualTexture.Border ||
                manifest.firstMip != MeshWorldVirtualTexture.FirstSvtMip || manifest.rootMip != MeshWorldVirtualTexture.RootMip ||
                manifest.albedoFormat != albedoFormat.ToString() || manifest.dataFormat != dataFormat.ToString() ||
                manifest.bytesPerPage != BytesPerPage || !IsSha256(manifest.recipe) || !IsSha256(manifest.payloadSha256) ||
                manifest.pages == null)
                throw new InvalidDataException($"World SVT archive layout differs from the terrain cache contract: {indexPath} " +
                    $"(version {manifest?.version}, expected {expectedVersion}).");

            // 文件按 mip、z、x 顺序保存。启动时检查整套粗页齐全，再用索引定位字节范围。
            int index = 0;
            for (int mip = manifest.firstMip; mip <= manifest.rootMip; mip++)
            {
                int side = MeshWorldVirtualTexture.PagesPerAxis(mip);
                for (int z = 0; z < side; z++)
                    for (int x = 0; x < side; x++)
                    {
                        uint key = MeshWorldVirtualTexture.EncodeKey(mip, x, z);
                        if (index >= manifest.pages.Length || manifest.pages[index] == null ||
                            manifest.pages[index].key != key || !IsSha256(manifest.pages[index].sha256))
                            throw new InvalidDataException("World SVT archive is missing a complete ordered mip level.");
                        indexes.Add(key, index++);
                    }
            }
            if (index != manifest.pages.Length) throw new InvalidDataException("World SVT archive has extra pages.");

            payloadPath = Path.Combine(archiveDirectory, PayloadName(manifest.payloadSha256, useBc3));
            var file = new FileInfo(payloadPath);
            if (!file.Exists || file.Length != PayloadBytes)
                throw new InvalidDataException("World SVT archive payload is missing or truncated: " + payloadPath);
            if (!MatchesRecipe(settings))
                throw new InvalidDataException("World SVT recipe differs from active source data or material settings. Rebake the archive.");
        }

        public bool MatchesRecipe(Material settings) => producer.GetRecipeIdentity(settings) == manifest.recipe;
        public bool Contains(uint key) => indexes.ContainsKey(key);

        public static string PayloadName(string sha256, bool useBc3 = false)
        {
            if (!IsSha256(sha256)) throw new ArgumentException("SVT payload name needs a lowercase SHA256.", nameof(sha256));
            return "pages-" + sha256 + (useBc3 ? ".bc3.bin" : ".rgba8.bin");
        }

        static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
            return true;
        }

        int PageIndex(uint key)
        {
            if (!indexes.TryGetValue(key, out int index)) throw new KeyNotFoundException("No offline SVT page for key " + key);
            return index;
        }

        public byte[] Read(uint key)
        {
            // 离线烘焙工具用同步读取抽查生成的归档。
            int index = PageIndex(key);
            var bytes = new byte[BytesPerPage];
            using (var file = Open(index, FileOptions.RandomAccess))
                MeshWorldVtProducer.ReadExactly(file, bytes, bytes.Length);
            Verify(index, bytes);
            return bytes;
        }

        public async Task<byte[]> ReadAsync(uint key, CancellationToken token)
        {
            // 运行时只读取请求的页。读完并通过页校验后，调度器才会拿到字节数据。
            token.ThrowIfCancellationRequested();
            int index = PageIndex(key);
            var bytes = new byte[BytesPerPage];
            using (var file = Open(index, FileOptions.RandomAccess | FileOptions.Asynchronous))
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int received = await file.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                    if (received <= 0) throw new EndOfStreamException("World SVT archive ended inside a page.");
                    offset += received;
                }
            }
            token.ThrowIfCancellationRequested();
            Verify(index, bytes);
            return bytes;
        }

        FileStream Open(int index, FileOptions options)
        {
            // 每页固定长度，按底色图、数据图的顺序存放两个输出。
            var file = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, options);
            try
            {
                file.Position = (long)index * BytesPerPage;
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        void Verify(int index, byte[] bytes)
        {
            if (MeshMaterialResident.Sha256(bytes) != manifest.pages[index].sha256)
                throw new InvalidDataException("World SVT page checksum failed: key " + manifest.pages[index].key);
        }
    }
}
