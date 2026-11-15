using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // This module owns height data only. It does not depend on the historical
    // TerrainResearch, TerrainData, material evaluators, RVT, or SVT modules.
    public static class MeshHeightLayout
    {
        public const int Version = 1;
        public const int WorldSize = 10000;
        public const int PaddedSize = 10240;
        public const int TileIntervals = 512;
        public const int TileResolution = TileIntervals + 1;
        public const int TilesPerAxis = 20;
        // Largest odd square side for five R16 layers within a 16 MiB payload.
        public const int ClipSize = 1295;
        public const int ClipLevels = 5;
        public const long ClipBudgetBytes = 16L * 1024 * 1024;
        public const long ClipPayloadBytes = (long)ClipSize * ClipSize * ClipLevels * 2;
        public const int LeafIntervals = 32;
        public const int LeafAxis = PaddedSize / LeafIntervals;
        public const float HeightScale = 1200f;
        public const string Recipe = "separable-mountain-valley-v1";
        public const string MetadataName = "metadata.json";
        public const string BoundsName = "leaf-bounds.bin";
        public const int TileBytes = TileResolution * TileResolution * sizeof(ushort);
        public static int TilesAtLevel(int level)
        {
            RequireLevel(level);
            int coverage = TileIntervals << level;
            return (WorldSize + coverage - 1) / coverage;
        }
        public static string TileName(int level, int x, int z)
        { return "height-l" + level + "-x" + x.ToString("D2") + "-z" + z.ToString("D2") + ".r16"; }
        public static int PositiveMod(int value, int divisor)
        { int result = value % divisor; return result < 0 ? result + divisor : result; }
        public static void RequireLevel(int level)
        { if (level < 0 || level >= ClipLevels) throw new ArgumentOutOfRangeException(nameof(level)); }
        public static float Decode(ushort value) { return value * (HeightScale / 65535f); }
    }

    [Serializable]
    public sealed class MeshHeightMetadata
    {
        public int version = MeshHeightLayout.Version;
        public int worldSize = MeshHeightLayout.WorldSize;
        public int paddedSize = MeshHeightLayout.PaddedSize;
        public int tileIntervals = MeshHeightLayout.TileIntervals;
        public int tileResolution = MeshHeightLayout.TileResolution;
        public int clipLevels = MeshHeightLayout.ClipLevels;
        // Legacy bake-time cache setting. It does not change any source samples
        // or tile files; runtime Clipmap size is MeshHeightLayout.ClipSize.
        public int clipSize = MeshHeightLayout.ClipSize;
        public int leafIntervals = MeshHeightLayout.LeafIntervals;
        public int leafAxis = MeshHeightLayout.LeafAxis;
        public float heightScale = MeshHeightLayout.HeightScale;
        public string recipe = MeshHeightLayout.Recipe;
        public string encoding = "unsigned-16-little-endian; original-grid decimation; shared endpoints";
        public float elevationOffset;
        public float sourceSpacing;
        public string sourceDescription;
        public long heightFileBytes;
        public int heightFileCount;
    }

    public static class MeshHeightBake
    {
        // CPU-friendly, explicitly synthetic test terrain. Every level is sampled
        // from this SAME integer, quantized field: no independently averaged mips.
        sealed class HeightRecipe
        {
            readonly float[] broadX = new float[10001], broadZ = new float[10001];
            readonly float[] mountainX = new float[10001], mountainZ = new float[10001];
            readonly float[] hillX = new float[10001], hillZ = new float[10001];
            readonly float[] ridgeX = new float[10001], ridgeZ = new float[10001];
            readonly float[] valleyX = new float[10001], valleyZ = new float[10001];
            readonly float[] detailX = new float[10001], detailZ = new float[10001];
            readonly float[] microX = new float[10001], microZ = new float[10001];
            public HeightRecipe()
            {
                for (int i = 0; i <= MeshHeightLayout.WorldSize; i++)
                {
                    broadX[i] = (float)(.5 + .5 * Math.Sin(i * .0011));
                    broadZ[i] = (float)(.5 + .5 * Math.Cos(i * .0009));
                    mountainX[i] = Gaussian(i, 7250, 950); mountainZ[i] = Gaussian(i, 6600, 1250);
                    hillX[i] = Gaussian(i, 2600, 1100); hillZ[i] = Gaussian(i, 2900, 900);
                    float ridge = (float)Math.Abs(Math.Sin(i * .0017));
                    ridgeX[i] = ridge * ridge * ridge;
                    ridgeZ[i] = (float)(.5 + .5 * Math.Sin(i * .0013));
                    valleyX[i] = Gaussian(i, 4800, 240); valleyZ[i] = Gaussian(i, 5100, 2600);
                    detailX[i] = (float)Math.Sin(i * .019); detailZ[i] = (float)Math.Cos(i * .021);
                    microX[i] = (float)Math.Sin(i * .11); microZ[i] = (float)Math.Sin(i * .13);
                }
            }
            static float Gaussian(int value, double center, double radius)
            { double d = (value - center) / radius; return (float)Math.Exp(-d * d); }
            public ushort Sample(int x, int z)
            {
                x = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, x));
                z = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, z));
                float h = 100 + 70 * broadX[x] * broadZ[z]
                    + 620 * mountainX[x] * mountainZ[z] + 320 * hillX[x] * hillZ[z]
                    + 130 * ridgeX[x] * ridgeZ[z] - 75 * valleyX[x] * valleyZ[z]
                    + 16 * detailX[x] * detailZ[z] + 6 * microX[x] * microZ[z];
                return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(h * (65535.0 / MeshHeightLayout.HeightScale))));
            }
        }

        // Called by the new scene's editor builder, never implicitly by a frame.
        // Metadata is published last: partial/failed builds are not valid data sets.
        public static void Build(string directory, string heightRecipe = MeshHeightLayout.Recipe)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A data directory is required.", nameof(directory));
            if (heightRecipe != MeshHeightLayout.Recipe && heightRecipe != MeshLandscapeHeight.Recipe)
                throw new ArgumentException("Unknown height recipe.", nameof(heightRecipe));
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string manifest = Path.Combine(directory, MeshHeightLayout.MetadataName);
            if (File.Exists(manifest)) File.Delete(manifest);
            HeightRecipe recipe = heightRecipe == MeshHeightLayout.Recipe ? new HeightRecipe() : null;
            MeshLandscapeHeight landscape = heightRecipe == MeshLandscapeHeight.Recipe ? new MeshLandscapeHeight() : null;
            int side = MeshHeightLayout.TileResolution, leafAxis = MeshHeightLayout.LeafAxis;
            var values = new ushort[side * side]; var bytes = new byte[MeshHeightLayout.TileBytes];
            var bounds = new ushort[leafAxis * leafAxis * 2];
            for (int i = 0; i < bounds.Length; i += 2) bounds[i] = ushort.MaxValue;
            var metadata = new MeshHeightMetadata { recipe = heightRecipe };
            for (int level = 0; level < MeshHeightLayout.ClipLevels; level++)
            {
                int count = MeshHeightLayout.TilesAtLevel(level), stride = 1 << level;
                for (int tz = 0; tz < count; tz++) for (int tx = 0; tx < count; tx++)
                {
                    for (int z = 0; z < side; z++)
                    {
                        int iz = (tz * MeshHeightLayout.TileIntervals + z) * stride;
                        for (int x = 0; x < side; x++)
                        {
                            int ix = (tx * MeshHeightLayout.TileIntervals + x) * stride;
                            ushort h = landscape != null ? landscape.Sample(ix, iz) : recipe.Sample(ix, iz); values[z * side + x] = h;
                            if (level == 0) IncludeLeafBoundary(bounds, ix, iz, h);
                        }
                    }
                    Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                    if (!BitConverter.IsLittleEndian) SwapPairs(bytes);
                    WriteComplete(Path.Combine(directory, MeshHeightLayout.TileName(level, tx, tz)), bytes);
                    metadata.heightFileCount++; metadata.heightFileBytes += bytes.Length;
                }
            }
            for (int i = 0; i < bounds.Length; i += 2)
                if (bounds[i] > bounds[i + 1]) throw new InvalidDataException("A padded terrain leaf has no height bounds.");
            var boundsBytes = new byte[bounds.Length * sizeof(ushort)];
            Buffer.BlockCopy(bounds, 0, boundsBytes, 0, boundsBytes.Length);
            if (!BitConverter.IsLittleEndian) SwapPairs(boundsBytes);
            WriteComplete(Path.Combine(directory, MeshHeightLayout.BoundsName), boundsBytes);
            File.WriteAllText(manifest, JsonUtility.ToJson(metadata, true), new UTF8Encoding(false));
            landscape?.Dispose();
        }
        static void IncludeLeafBoundary(ushort[] bounds, int x, int z, ushort value)
        {
            int n = MeshHeightLayout.LeafAxis, step = MeshHeightLayout.LeafIntervals;
            int lx = Math.Min(x / step, n - 1), lz = Math.Min(z / step, n - 1);
            Include(bounds, lx, lz, value);
            bool left = x > 0 && x < MeshHeightLayout.PaddedSize && x % step == 0;
            bool below = z > 0 && z < MeshHeightLayout.PaddedSize && z % step == 0;
            if (left) Include(bounds, lx - 1, lz, value);
            if (below) Include(bounds, lx, lz - 1, value);
            if (left && below) Include(bounds, lx - 1, lz - 1, value);
        }
        static void Include(ushort[] bounds, int x, int z, ushort h)
        {
            int i = (z * MeshHeightLayout.LeafAxis + x) * 2;
            if (h < bounds[i]) bounds[i] = h;
            if (h > bounds[i + 1]) bounds[i + 1] = h;
        }
        static void WriteComplete(string path, byte[] bytes)
        {
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
        internal static void SwapPairs(byte[] bytes)
        { for (int i = 0; i < bytes.Length; i += 2) { byte a = bytes[i]; bytes[i] = bytes[i + 1]; bytes[i + 1] = a; } }
    }

    // A bounded synchronous file reader for this first mesh-only experiment.
    // The Clipmap does not hide a complete CPU-resident heightfield.
    public sealed class MeshHeightSource : IDisposable
    {
        public const int CacheCapacity = 12;
        sealed class Tile { public int key; public long use; public ushort[] values; }
        readonly Dictionary<int, Tile> cache = new Dictionary<int, Tile>();
        readonly string directory;
        // 仅供独立RVT后台读取器取得不可变路径；不共享本对象的LRU或文件访问。
        internal string DirectoryPath => directory;
        long clock;
        bool disposed;
        public MeshHeightMetadata Metadata { get; private set; }
        public long BytesRead { get; private set; }
        public int TilesRead { get; private set; }
        public int CachedTileCount => cache.Count;
        public long CpuCacheBytes => (long)cache.Count * MeshHeightLayout.TileBytes;
        public MeshHeightSource(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A data directory is required.", nameof(directory));
            this.directory = Path.GetFullPath(directory);
            string path = Path.Combine(this.directory, MeshHeightLayout.MetadataName);
            if (!File.Exists(path)) throw new FileNotFoundException("Build the independent 10km height data first.", path);
            Metadata = JsonUtility.FromJson<MeshHeightMetadata>(File.ReadAllText(path));
            ValidateMetadata(Metadata);
            int fileCount = 0; long fileBytes = 0;
            for (int level = 0; level < MeshHeightLayout.ClipLevels; level++)
            {
                int n = MeshHeightLayout.TilesAtLevel(level);
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                {
                    RequireLength(Path.Combine(this.directory, MeshHeightLayout.TileName(level, x, z)), MeshHeightLayout.TileBytes);
                    fileCount++; fileBytes += MeshHeightLayout.TileBytes;
                }
            }
            if (Metadata.heightFileCount != fileCount || Metadata.heightFileBytes != fileBytes)
                throw new InvalidDataException("Height manifest file count or payload size differs from the height layout.");
            RequireLength(Path.Combine(this.directory, MeshHeightLayout.BoundsName), MeshHeightLayout.LeafAxis * MeshHeightLayout.LeafAxis * 4);
        }
        static void ValidateMetadata(MeshHeightMetadata m)
        {
            if (m == null || m.version != MeshHeightLayout.Version || m.worldSize != MeshHeightLayout.WorldSize ||
                m.paddedSize != MeshHeightLayout.PaddedSize || m.tileIntervals != MeshHeightLayout.TileIntervals ||
                m.tileResolution != MeshHeightLayout.TileResolution || m.clipLevels != MeshHeightLayout.ClipLevels ||
                m.leafIntervals != MeshHeightLayout.LeafIntervals ||
                m.leafAxis != MeshHeightLayout.LeafAxis || float.IsNaN(m.heightScale) ||
                float.IsInfinity(m.heightScale) || m.heightScale <= 0 || m.heightScale > 8192 ||
                (m.recipe != MeshHeightLayout.Recipe && m.recipe != MeshLandscapeHeight.Recipe && m.recipe != "maladetas-dem-1m-v1"))
                throw new InvalidDataException("Height metadata does not describe this exact 10km mesh recipe.");
        }
        static void RequireLength(string path, long length)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != length) throw new InvalidDataException("Height file is missing or has an invalid length: " + path);
        }
        public ushort[] ReadLeafBounds()
        {
            RequireLive();
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, MeshHeightLayout.BoundsName)); BytesRead += bytes.Length;
            if (bytes.Length != MeshHeightLayout.LeafAxis * MeshHeightLayout.LeafAxis * 4) throw new InvalidDataException("Leaf bounds length changed.");
            if (!BitConverter.IsLittleEndian) MeshHeightBake.SwapPairs(bytes);
            var result = new ushort[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            for (int i = 0; i < result.Length; i += 2) if (result[i] > result[i + 1]) throw new InvalidDataException("Invalid leaf height bounds.");
            return result;
        }
        public ushort[] ReadTile(int level, int tx, int tz)
        {
            RequireLive(); int n = MeshHeightLayout.TilesAtLevel(level);
            if (tx < 0 || tz < 0 || tx >= n || tz >= n) throw new ArgumentOutOfRangeException(nameof(tx));
            int key = level * 1024 + tz * 32 + tx;
            if (cache.TryGetValue(key, out Tile found)) { found.use = ++clock; return found.values; }
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, MeshHeightLayout.TileName(level, tx, tz)));
            if (bytes.Length != MeshHeightLayout.TileBytes) throw new InvalidDataException("Height tile length changed during use.");
            BytesRead += bytes.Length; TilesRead++;
            if (!BitConverter.IsLittleEndian) MeshHeightBake.SwapPairs(bytes);
            var values = new ushort[MeshHeightLayout.TileResolution * MeshHeightLayout.TileResolution];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            if (cache.Count == CacheCapacity)
            {
                Tile oldest = null;
                foreach (var entry in cache.Values) if (oldest == null || entry.use < oldest.use) oldest = entry;
                cache.Remove(oldest.key);
            }
            cache.Add(key, new Tile { key = key, values = values, use = ++clock }); return values;
        }
        public ushort SampleGrid(int xMeters, int zMeters, int level)
        {
            MeshHeightLayout.RequireLevel(level);
            int gx = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, xMeters)) >> level;
            int gz = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, zMeters)) >> level;
            int tx = gx / MeshHeightLayout.TileIntervals, tz = gz / MeshHeightLayout.TileIntervals;
            ushort[] values = ReadTile(level, tx, tz);
            return values[(gz - tz * MeshHeightLayout.TileIntervals) * MeshHeightLayout.TileResolution + gx - tx * MeshHeightLayout.TileIntervals];
        }
        public float SampleMeters(float x, float z, int level)
        {
            RequireLive(); MeshHeightLayout.RequireLevel(level);
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z)) throw new ArgumentException("Height coordinates must be finite.");
            int stride = 1 << level;
            float gx = Mathf.Clamp(x, 0, MeshHeightLayout.WorldSize) / stride;
            float gz = Mathf.Clamp(z, 0, MeshHeightLayout.WorldSize) / stride;
            int ix = Mathf.FloorToInt(gx), iz = Mathf.FloorToInt(gz);
            float a = Mathf.Lerp(Decode(SampleGrid(ix * stride, iz * stride, level)), Decode(SampleGrid((ix + 1) * stride, iz * stride, level)), gx - ix);
            float b = Mathf.Lerp(Decode(SampleGrid(ix * stride, (iz + 1) * stride, level)), Decode(SampleGrid((ix + 1) * stride, (iz + 1) * stride, level)), gx - ix);
            return Mathf.Lerp(a, b, gz - iz);
        }
        public float Decode(ushort value) => value * (Metadata.heightScale / 65535f);

        // Global grid indices, rather than world metres. Copies contiguous file
        // rows, avoiding one dictionary/file query per height sample.
        public void CopyGridRectangle(int level, int gridX, int gridZ, int width, int height, ushort[] output)
        {
            RequireLive(); MeshHeightLayout.RequireLevel(level);
            if (width < 1 || height < 1 || output == null || output.Length != checked(width * height)) throw new ArgumentException("A tightly packed height rectangle is required.");
            int maximum = MeshHeightLayout.WorldSize >> level;
            for (int row = 0; row < height; row++)
            {
                int gz = Math.Max(0, Math.Min(maximum, gridZ + row));
                int tz = gz / MeshHeightLayout.TileIntervals, lz = gz - tz * MeshHeightLayout.TileIntervals;
                int written = 0;
                while (written < width)
                {
                    int gx = gridX + written;
                    if (gx < 0 || gx > maximum)
                    {
                        int edge = gx < 0 ? 0 : maximum;
                        ushort value = SampleGrid(edge << level, gz << level, level);
                        int count = gx < 0 ? Math.Min(width - written, -gx) : width - written;
                        int end = written + count;
                        while (written < end) output[row * width + written++] = value;
                    }
                    else
                    {
                        int tx = gx / MeshHeightLayout.TileIntervals, lx = gx - tx * MeshHeightLayout.TileIntervals;
                        int count = Math.Min(width - written, Math.Min(MeshHeightLayout.TileIntervals - lx, maximum - gx + 1));
                        ushort[] tile = ReadTile(level, tx, tz);
                        Buffer.BlockCopy(tile, (lz * MeshHeightLayout.TileResolution + lx) * 2, output, (row * width + written) * 2, count * 2);
                        written += count;
                    }
                }
            }
        }
        public void ClearCache() { RequireLive(); cache.Clear(); }
        public void Dispose() { if (disposed) return; disposed = true; cache.Clear(); Metadata = null; }
        void RequireLive() { if (disposed) throw new ObjectDisposedException(nameof(MeshHeightSource)); }
    }

    public sealed class MeshResidentHeight : IDisposable
    {
        readonly float heightScale;
        public Texture2DArray Texture { get; private set; }
        public long GpuBytes => Texture ? (long)MeshHeightLayout.TileBytes * MeshHeightLayout.TilesPerAxis * MeshHeightLayout.TilesPerAxis : 0;
        public MeshResidentHeight(MeshHeightSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            heightScale = source.Metadata.heightScale;
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.R16)) throw new NotSupportedException("This mesh experiment requires R16 texture arrays.");
            int count = MeshHeightLayout.TilesPerAxis;
            try
            {
                Texture = new Texture2DArray(MeshHeightLayout.TileResolution, MeshHeightLayout.TileResolution, count * count, TextureFormat.R16, false, true)
                { name = "DirectDelta full resident 10km R16 heights", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
                for (int z = 0; z < count; z++) for (int x = 0; x < count; x++) Texture.SetPixelData(source.ReadTile(0, x, z), 0, z * count + x);
                Texture.Apply(false, true); // Release Unity's complete CPU texture mirror.
                source.ClearCache();
            }
            catch { Dispose(); throw; }
        }
        public void Bind(Material material)
        { if (!Texture) throw new ObjectDisposedException(nameof(MeshResidentHeight)); material.SetTexture("_MeshResidentHeight", Texture); material.SetFloat("_MeshHeightScale", heightScale); }
        public void Dispose() { Destroy(Texture); Texture = null; }
        internal static void Destroy(UnityEngine.Object value)
        { if (value) { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); } }
    }

    public sealed class MeshHeightClipmap : IDisposable
    {
        sealed class UploadStage : IDisposable
        {
            public Texture2D texture;
            public ushort[] values;
            public void Ensure(int width, int height)
            {
                if (texture && texture.width == width && texture.height == height) return;
                Dispose();
                texture = new Texture2D(width, height, TextureFormat.R16, false, true)
                { name = "Delta height Clipmap upload " + width + "x" + height, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                values = new ushort[width * height];
            }
            public void Dispose() { MeshResidentHeight.Destroy(texture); texture = null; values = null; }
            public long Bytes => values == null ? 0 : values.LongLength * 2;
        }
        readonly MeshHeightSource source;
        readonly UploadStage full = new UploadStage(), column = new UploadStage(), row = new UploadStage();
        public RenderTexture Texture { get; private set; }
        public Vector4[] Origins { get; } = new Vector4[MeshHeightLayout.ClipLevels];
        public long GpuBytes => Texture ? (long)Texture.width * Texture.height * Texture.volumeDepth * 2 : 0;
        // Logical payloads only; CPU staging includes our ushort arrays and the
        // readable Texture2D mirrors. Driver/object overhead is not measured.
        public long CpuStagingBytes => (full.Bytes + column.Bytes + row.Bytes) * 2;
        public long GpuStagingBytes => full.Bytes + column.Bytes + row.Bytes;
        public long UploadedBytes { get; private set; }
        public int Uploads { get; private set; }
        public int CopyCommands { get; private set; }
        public int FullUploads { get; private set; }
        public int StripUploads { get; private set; }
        public long LastUploadedBytes { get; private set; }
        public int LastUploads { get; private set; }
        public int LastCopyCommands { get; private set; }
        public double LastUpdateMilliseconds { get; private set; }
        public MeshHeightClipmap(MeshHeightSource source)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.R16) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Linear) ||
                (SystemInfo.copyTextureSupport & (CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes | CopyTextureSupport.TextureToRT)) !=
                    (CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes | CopyTextureSupport.TextureToRT))
                throw new NotSupportedException("This mesh experiment requires R16 array render textures and GPU texture copies.");
            var description = new RenderTextureDescriptor(MeshHeightLayout.ClipSize, MeshHeightLayout.ClipSize)
            {
                graphicsFormat = GraphicsFormat.R16_UNorm, depthBufferBits = 0, msaaSamples = 1,
                dimension = TextureDimension.Tex2DArray, volumeDepth = MeshHeightLayout.ClipLevels,
                useMipMap = false, autoGenerateMips = false, enableRandomWrite = false
            };
            if ((long)description.width * description.height * description.volumeDepth * 2 > MeshHeightLayout.ClipBudgetBytes)
                throw new InvalidOperationException("The height Clipmap exceeds its 16 MiB logical GPU payload budget.");
            try
            {
                Texture = new RenderTexture(description)
                { name = "Delta toroidal height Clipmap " + MeshHeightLayout.ClipSize + "x" + MeshHeightLayout.ClipSize + "x" + MeshHeightLayout.ClipLevels + " R16", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
                if (!Texture.Create()) throw new InvalidOperationException("Height Clipmap allocation failed.");
            }
            catch { Dispose(); throw; }
        }
        public void UpdateCenter(Vector2 world, bool force = false)
        {
            if (!Texture) throw new ObjectDisposedException(nameof(MeshHeightClipmap));
            if (float.IsNaN(world.x) || float.IsInfinity(world.x) || float.IsNaN(world.y) || float.IsInfinity(world.y)) throw new ArgumentException("Clipmap center must be finite.");
            long started = System.Diagnostics.Stopwatch.GetTimestamp(), bytesBefore = UploadedBytes;
            int uploadsBefore = Uploads, copiesBefore = CopyCommands;
            world.x = Mathf.Clamp(world.x, 0, MeshHeightLayout.WorldSize); world.y = Mathf.Clamp(world.y, 0, MeshHeightLayout.WorldSize);
            int n = MeshHeightLayout.ClipSize;
            for (int level = 0; level < MeshHeightLayout.ClipLevels; level++)
            {
                int stride = 1 << level;
                int ox = level == MeshHeightLayout.ClipLevels - 1 ? 0 : Mathf.FloorToInt(world.x / stride / 64) * 64 - n / 2;
                int oz = level == MeshHeightLayout.ClipLevels - 1 ? 0 : Mathf.FloorToInt(world.y / stride / 64) * 64 - n / 2;
                Vector4 old = Origins[level]; int previousX = Mathf.RoundToInt(old.x / stride), previousZ = Mathf.RoundToInt(old.y / stride);
                int dx = ox - previousX, dz = oz - previousZ;
                if (!force && old.w > .5f && dx == 0 && dz == 0) continue;
                try
                {
                    if (force || old.w < .5f || Math.Abs(dx) >= n || Math.Abs(dz) >= n)
                    { UploadRectangle(level, ox, oz, n, n, full); FullUploads++; }
                    else
                    {
                        // New columns cover the complete new Z span. New rows
                        // cover only overlapping X, so corners are copied once.
                        if (dx != 0)
                        {
                            int x = dx > 0 ? previousX + n : ox;
                            UploadRectangle(level, x, oz, Math.Abs(dx), n, column); StripUploads++;
                        }
                        int left = Math.Max(ox, previousX), right = Math.Min(ox + n, previousX + n);
                        if (dz != 0 && right > left)
                        {
                            int z = dz > 0 ? previousZ + n : oz;
                            UploadRectangle(level, left, z, right - left, Math.Abs(dz), row); StripUploads++;
                        }
                    }
                    // Origins become visible only after every strip has been queued.
                    Origins[level] = new Vector4(ox * stride, oz * stride, stride, 1);
                }
                catch { Origins[level] = new Vector4(0, 0, stride, 0); throw; }
            }
            LastUploadedBytes = UploadedBytes - bytesBefore; LastUploads = Uploads - uploadsBefore;
            LastCopyCommands = CopyCommands - copiesBefore;
            LastUpdateMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        void UploadRectangle(int level, int gx, int gz, int width, int height, UploadStage stage)
        {
            stage.Ensure(width, height); source.CopyGridRectangle(level, gx, gz, width, height, stage.values);
            stage.texture.SetPixelData(stage.values, 0); stage.texture.Apply(false, false);
            UploadedBytes += (long)width * height * 2; Uploads++;
            int n = MeshHeightLayout.ClipSize;
            for (int sy = 0; sy < height;)
            {
                int dy = MeshHeightLayout.PositiveMod(gz + sy, n), copyHeight = Math.Min(height - sy, n - dy);
                for (int sx = 0; sx < width;)
                {
                    int dx = MeshHeightLayout.PositiveMod(gx + sx, n), copyWidth = Math.Min(width - sx, n - dx);
                    Graphics.CopyTexture(stage.texture, 0, 0, sx, sy, copyWidth, copyHeight, Texture, level, 0, dx, dy);
                    CopyCommands++; sx += copyWidth;
                }
                sy += copyHeight;
            }
        }
        public bool Covers(Vector2 minimum, Vector2 maximum, int level)
        {
            MeshHeightLayout.RequireLevel(level); Vector4 origin = Origins[level];
            float span = (MeshHeightLayout.ClipSize - 1) * origin.z;
            return origin.w > .5f && minimum.x >= origin.x && minimum.y >= origin.y && maximum.x <= origin.x + span && maximum.y <= origin.y + span;
        }
        public void Bind(Material material)
        {
            if (!Texture) throw new ObjectDisposedException(nameof(MeshHeightClipmap));
            material.SetTexture("_MeshHeightClip", Texture); material.SetVectorArray("_MeshClipOrigins", Origins);
            material.SetFloat("_MeshHeightScale", source.Metadata.heightScale);
        }
        public void Dispose()
        {
            full.Dispose(); column.Dispose(); row.Dispose();
            if (Texture) { Texture.Release(); MeshResidentHeight.Destroy(Texture); Texture = null; }
            for (int i = 0; i < Origins.Length; i++) Origins[i] = Vector4.zero;
        }
    }
}
