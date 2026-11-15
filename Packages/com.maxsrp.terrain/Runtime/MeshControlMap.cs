using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // One metre categorical control source, with independent repaired coarse
    // levels. Its small GPU Clipmap is separate from the HEIGHT Clipmap.
    // Material source textures are compressed; these categorical IDs are not.
    public static class MeshControlContract
    {
        public const int MaterialCount = 16;
        public const int WorldSize = 10000, Spacing = 1, Width = WorldSize + 1;
        public const int LevelCount = 6, ClipSize = 655, SnapSamples = 32;
        public const string DataName = "control-l0.r16", MetadataName = "control.json";
        public const int FormatVersion = 2;
        public const string Recipe = "mountain-river-layer-order-swatches-v5";
        public const string BeforeLayerOrderRecipe = "mountain-river-blend-swatches-v4";
        public const string PreviousRecipe = "mountain-river-swatches-v3";
        public const string LegacyRecipe = "natural16-macro512-clip-v2";
        public static int Stride(int level)
        { if (level < 0 || level >= LevelCount) throw new ArgumentOutOfRangeException(nameof(level)); return 1 << level; }
        public static int WidthAtLevel(int level)
        { int stride = Stride(level); return (WorldSize+stride-1)/stride+1; }
        public static string LevelFile(int level) { Stride(level); return "control-l"+level+".r16"; }

        public readonly struct Point
        {
            public readonly int bottom, top, weightCode;
            public float TopWeight => weightCode / 255f;
            internal Point(int bottom, int top, int weightCode)
            { this.bottom = bottom; this.top = top; this.weightCode = weightCode; }
        }
        public readonly struct Triangle
        {
            public readonly int i0, i1, i2;
            public readonly double w0, w1, w2;
            public readonly bool upper;
            internal Triangle(int a, int b, int c, double wa, double wb, double wc, bool upper)
            { i0 = a; i1 = b; i2 = c; w0 = wa; w1 = wb; w2 = wc; this.upper = upper; }
        }
        public readonly struct Layers
        {
            public readonly int count, id0, id1, id2;
            // Ordered covering opacities, not normalized splat contributions.
            public readonly double opacity0, opacity1, opacity2;
            internal Layers(int count, int a, int b, int c, double wa, double wb, double wc)
            { this.count = count; id0 = a; id1 = b; id2 = c; opacity0 = wa; opacity1 = wb; opacity2 = wc; }
        }
        [Serializable] public sealed class RepairReport
        {
            public int trianglesVisited, invalidBefore, invalidAfter, repairedTriangles, removedTops;
            public long removedOpacityCodes;
        }

        public static ushort Encode(int bottom, int top, double topOpacity)
        {
            if (!Finite(topOpacity) || topOpacity < 0 || topOpacity > 1)
                throw new ArgumentOutOfRangeException(nameof(topOpacity));
            return EncodeQuantized(bottom, top, (int)Math.Floor(topOpacity * 255 + .5));
        }
        public static ushort EncodeQuantized(int bottom, int top, int weightCode)
        {
            // 数值ID必须事先按覆盖层序安排，不能直接使用下载素材编号。
            // TopWeight永远是Top材质的覆盖率；交换两层时需同时改成1-weight。
            if (bottom < 0 || bottom >= MaterialCount || top < bottom || top >= MaterialCount)
                throw new ArgumentOutOfRangeException("Material IDs must be ordered and inside 0..15.");
            if (weightCode < 0 || weightCode > 255) throw new ArgumentOutOfRangeException(nameof(weightCode));
            if (bottom == top) weightCode = 0;
            return (ushort)(bottom | top << 4 | weightCode << 8);
        }
        public static Point Decode(ushort word)
        {
            Point point = Read(word);
            if (point.bottom > point.top || point.bottom == point.top && point.weightCode != 0)
                throw new ArgumentException("Control word is not a canonical ordered 4/4/8 point.", nameof(word));
            return point;
        }
        public static bool TryDecode(ushort word, out Point point)
        {
            point = Read(word);
            return point.bottom <= point.top && (point.bottom != point.top || point.weightCode == 0);
        }
        public static Triangle SelectTriangle(int width, int height, double gridX, double gridZ)
        {
            Dimensions(width, height);
            if (!Finite(gridX) || !Finite(gridZ) || gridX < 0 || gridZ < 0 || gridX > width-1 || gridZ > height-1)
                throw new ArgumentOutOfRangeException("Control coordinates must lie inside the complete lattice.");
            int x = Math.Min((int)Math.Floor(gridX), width-2), z = Math.Min((int)Math.Floor(gridZ), height-2);
            int first = z*width+x;
            double fx = gridX-x, fz = gridZ-z;
            return fx+fz <= 1 ? new Triangle(first,first+1,first+width,1-fx-fz,fx,fz,false) :
                new Triangle(first+width+1,first+width,first+1,fx+fz-1,1-fx,1-fz,true);
        }
        public static Layers Evaluate(ushort[] controls, int width, int height, double gridX, double gridZ)
        {
            if (controls == null || controls.Length != checked(width*height)) throw new ArgumentException("Control extent mismatch.");
            Triangle triangle = SelectTriangle(width,height,gridX,gridZ);
            return Evaluate(Decode(controls[triangle.i0]),Decode(controls[triangle.i1]),Decode(controls[triangle.i2]),triangle.w0,triangle.w1,triangle.w2);
        }
        public static Layers Evaluate(Point a, Point b, Point c, double wa, double wb, double wc)
        {
            if (!Finite(wa) || !Finite(wb) || !Finite(wc) || wa < 0 || wb < 0 || wc < 0 || Math.Abs(wa+wb+wc-1) > 1e-8)
                throw new ArgumentException("Nonnegative triangular weights must sum to one.");
            uint mask = Mask(a)|Mask(b)|Mask(c); int count = PopCount(mask);
            if (count > 3) throw new InvalidOperationException("Unrepaired control triangle contains more than three materials.");
            int first = First(mask); mask &= mask-1;
            int second = mask == 0 ? first : First(mask); if (mask != 0) mask &= mask-1;
            int third = mask == 0 ? second : First(mask);
            double middle = count > 1 ? RawOpacity(a,second)*wa+RawOpacity(b,second)*wb+RawOpacity(c,second)*wc : 0;
            double top = count > 2 ? RawOpacity(a,third)*wa+RawOpacity(b,third)*wb+RawOpacity(c,third)*wc : 0;
            return new Layers(count,first,second,third,1,middle,top);
        }
        public static void Validate(ushort[] controls, int width, int height)
        {
            Dimensions(width,height);
            if (controls == null || controls.Length != checked(width*height)) throw new ArgumentException("Control extent mismatch.");
            for (int i = 0; i < controls.Length; ++i)
                if (!TryDecode(controls[i],out _)) throw new InvalidDataException("Invalid 4/4/8 control at index "+i);
        }
        public static int CountInvalidTriangles(ushort[] controls, int width, int height)
        { Validate(controls,width,height); return CountInvalidUnchecked(controls,width,height); }

        // Removing a top replaces it with its own bottom, already present in
        // every incident triangle. Thus future edits cannot invalidate triangles
        // already visited; one deterministic whole-map traversal suffices.
        public static RepairReport Repair(ushort[] controls, int width, int height)
        {
            Validate(controls,width,height);
            var report = new RepairReport { invalidBefore = CountInvalidUnchecked(controls,width,height) };
            if (report.invalidBefore == 0)
            {
                // The complete triangle set was already inspected above. With
                // no edits there is no second state to inspect or repair.
                report.trianglesVisited = checked((width-1)*(height-1)*2);
                return report;
            }
            for (int z = 0; z < height-1; ++z) for (int x = 0; x < width-1; ++x)
            {
                int a = z*width+x;
                Fix(controls,a,a+1,a+width,report); Fix(controls,a+width+1,a+width,a+1,report);
            }
            report.invalidAfter = CountInvalidUnchecked(controls,width,height);
            if (report.invalidAfter != 0) throw new InvalidOperationException("Control neighborhood repair failed.");
            return report;
        }
        static void Fix(ushort[] values, int a, int b, int c, RepairReport report)
        {
            report.trianglesVisited++;
            Point pa = Read(values[a]), pb = Read(values[b]), pc = Read(values[c]);
            if (PopCount(Mask(pa)|Mask(pb)|Mask(pc)) <= 3) return;
            report.repairedTriangles++;
            uint kept = (1u<<pa.bottom)|(1u<<pb.bottom)|(1u<<pc.bottom);
            if (Before(values,b,a)) Swap(ref a,ref b);
            if (Before(values,c,b)) Swap(ref b,ref c);
            if (Before(values,b,a)) Swap(ref a,ref b);
            Consider(values,a,ref kept,report); Consider(values,b,ref kept,report); Consider(values,c,ref kept,report);
        }
        static void Consider(ushort[] values, int index, ref uint kept, RepairReport report)
        {
            Point point = Read(values[index]); uint bit = 1u<<point.top;
            if ((kept&bit) != 0) return;
            if (PopCount(kept) < 3) { kept |= bit; return; }
            // New contract: all single-material points have top==bottom and
            // zero opacity. Do not retain the old baseline's collapsed weight.
            values[index] = EncodeQuantized(point.bottom,point.bottom,0);
            report.removedTops++; report.removedOpacityCodes += point.weightCode;
        }
        static bool Before(ushort[] values, int a, int b)
        { int wa = values[a]>>8, wb = values[b]>>8; return wa > wb || wa == wb && a < b; }
        static int CountInvalidUnchecked(ushort[] values, int width, int height)
        {
            int result = 0;
            for (int z = 0; z < height-1; ++z) for (int x = 0; x < width-1; ++x)
            {
                int a = z*width+x;
                uint p00 = Mask(Read(values[a])), p10 = Mask(Read(values[a+1]));
                uint p01 = Mask(Read(values[a+width])), p11 = Mask(Read(values[a+width+1]));
                if (PopCount(p00|p10|p01) > 3) result++;
                if (PopCount(p11|p01|p10) > 3) result++;
            }
            return result;
        }
        static Point Read(ushort value) => new Point(value&15,(value>>4)&15,value>>8);
        static uint Mask(Point point) => (1u<<point.bottom)|(1u<<point.top);
        static double RawOpacity(Point point, int id) => (point.bottom == id ? 1 : 0)+(point.top == id ? point.weightCode/255.0 : 0);
        static int First(uint mask) { int id = 0; while ((mask&1) == 0) { mask >>= 1; id++; } return id; }
        static int PopCount(uint value)
        { value -= (value>>1)&0x55555555u; value = (value&0x33333333u)+((value>>2)&0x33333333u); return (int)((((value+(value>>4))&0x0f0f0f0fu)*0x01010101u)>>24); }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void Dimensions(int width, int height)
        { if (width < 2 || height < 2 || (long)width*height > int.MaxValue) throw new ArgumentOutOfRangeException("Control lattice dimensions are invalid."); }
        static void Swap(ref int a, ref int b) { int temporary = a; a = b; b = temporary; }
    }

    [Serializable]
    public sealed class MeshControlLevel
    {
        public int level, stride, width;
        public string fileName, sha256;
        public long bytes;
        public int[] materialPointCounts = new int[MeshControlContract.MaterialCount];
        public MeshControlContract.RepairReport repair;
    }

    [Serializable]
    public sealed class MeshControlMetadata
    {
        public int version = MeshControlContract.FormatVersion;
        public int worldSize = MeshControlContract.WorldSize, spacing = MeshControlContract.Spacing, width = MeshControlContract.Width;
        public int materialCount = MeshControlContract.MaterialCount, clipSize = MeshControlContract.ClipSize;
        public string recipe = MeshControlContract.Recipe;
        public string encoding = "R16-UNorm point-load; bottom4-top4-topOpacity8; ordered IDs; little-endian";
        public string distributionDescription, heightRecipe, heightMetadataSha256;
        public string materialLayerOrder;
        public int swatchOrigin, swatchCellSize, swatchColumns;
        public long bytes;
        public MeshControlLevel[] levels = new MeshControlLevel[MeshControlContract.LevelCount];
    }

    public static class MeshControlBake
    {
        public static void Build(string directory, int spacing = MeshControlContract.Spacing, string heightDirectory = null)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A control data directory is required.");
            if (spacing != 1) throw new ArgumentOutOfRangeException(nameof(spacing),"This baseline explicitly preserves one-metre control density.");
            Directory.CreateDirectory(directory);
            string manifest = Path.Combine(directory,MeshControlContract.MetadataName);
            if (File.Exists(manifest)) File.Delete(manifest);
            if (string.IsNullOrEmpty(heightDirectory))
                heightDirectory = Path.Combine(Application.streamingAssetsPath,"MaxTerrain10km");
            var metadata = new MeshControlMetadata
            {
                distributionDescription = "Procedural cover derived from height/slope/curvature/D8 drainage on an 8m feature lattice, expanded to 1m controls. Not measured land cover; no WorldCover registration. Maladetas uses snow-free alpine rock/scree/meadow rules and continuous variable coverage.",
                heightRecipe = JsonUtility.FromJson<MeshHeightMetadata>(File.ReadAllText(Path.Combine(heightDirectory,MeshHeightLayout.MetadataName))).recipe,
                heightMetadataSha256 = Hash(File.ReadAllBytes(Path.Combine(heightDirectory,MeshHeightLayout.MetadataName))),
                materialLayerOrder = MeshMaterialLayerOrder.Recipe,
                swatchOrigin = MeshControlLandscape.SwatchOrigin,
                swatchCellSize = MeshControlLandscape.SwatchCellSize,
                swatchColumns = MeshControlLandscape.SwatchCount
            };
            using (var landscape = new MeshControlLandscape(heightDirectory))
            {
                for (int level = 0; level < MeshControlContract.LevelCount; ++level)
                {
                    MeshControlLevel entry = BuildLevel(directory,level,landscape);
                    metadata.levels[level] = entry; metadata.bytes += entry.bytes;
                }
            }
            // Each file was independently generated, inspected and repaired.
            // Publishing this last prevents a partial bake appearing valid.
            File.WriteAllText(manifest,JsonUtility.ToJson(metadata,true),new UTF8Encoding(false));
        }
        static MeshControlLevel BuildLevel(string directory, int level, MeshControlLandscape landscape)
        {
            int stride = MeshControlContract.Stride(level), width = MeshControlContract.WidthAtLevel(level);
            var controls = new ushort[width*width];
            for (int z = 0; z < width; ++z)
            {
                int worldZ = Math.Min(z*stride,MeshControlContract.WorldSize);
                for (int x = 0; x < width; ++x)
                {
                    int worldX = Math.Min(x*stride,MeshControlContract.WorldSize);
                    controls[z*width+x] = landscape.Sample(worldX,worldZ);
                }
            }
            var entry = new MeshControlLevel { level = level, stride = stride, width = width, fileName = MeshControlContract.LevelFile(level),
                repair = MeshControlContract.Repair(controls,width,width) };
            foreach (ushort word in controls)
            {
                int bottom = word&15, top = (word>>4)&15;
                entry.materialPointCounts[bottom]++;
                if (top != bottom) entry.materialPointCounts[top]++;
            }
            for (int id = 0; id < MeshControlContract.MaterialCount; ++id)
                if (entry.materialPointCounts[id] == 0) throw new InvalidOperationException("Control level "+level+" failed to use material "+id);
            byte[] bytes = new byte[controls.Length*2]; Buffer.BlockCopy(controls,0,bytes,0,bytes.Length);
            if (!BitConverter.IsLittleEndian) SwapPairs(bytes);
            entry.bytes = bytes.Length; entry.sha256 = Hash(bytes);
            File.WriteAllBytes(Path.Combine(directory,entry.fileName),bytes);
            return entry;
        }
        internal static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        internal static void SwapPairs(byte[] bytes)
        { for (int i = 0; i < bytes.Length; i += 2) { byte value = bytes[i]; bytes[i] = bytes[i+1]; bytes[i+1] = value; } }
        public static ushort[] ReadValues(string directory, int level = 0)
        {
            MeshControlContract.Stride(level);
            MeshControlMetadata metadata = ReadMetadata(directory); MeshControlLevel entry = metadata.levels[level];
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory,entry.fileName));
            if (bytes.LongLength != entry.bytes || Hash(bytes) != entry.sha256) throw new InvalidDataException("Control file extent or SHA mismatch.");
            if (!BitConverter.IsLittleEndian) SwapPairs(bytes);
            var result = new ushort[bytes.Length/2]; Buffer.BlockCopy(bytes,0,result,0,bytes.Length); return result;
        }
        internal static MeshControlMetadata ReadMetadata(string directory)
        {
            var metadata = JsonUtility.FromJson<MeshControlMetadata>(File.ReadAllText(Path.Combine(directory,MeshControlContract.MetadataName)));
            if (metadata == null || metadata.version != MeshControlContract.FormatVersion || metadata.worldSize != MeshControlContract.WorldSize ||
                metadata.spacing != 1 || metadata.width != MeshControlContract.Width || metadata.materialCount != MeshControlContract.MaterialCount ||
                (metadata.recipe != MeshControlContract.Recipe && metadata.recipe != MeshControlContract.BeforeLayerOrderRecipe &&
                    metadata.recipe != MeshControlContract.PreviousRecipe && metadata.recipe != MeshControlContract.LegacyRecipe) ||
                metadata.levels == null || metadata.levels.Length != MeshControlContract.LevelCount)
                throw new InvalidDataException("Control manifest does not match the one-metre 16-material 4/4/8 contract.");
            if (metadata.recipe == MeshControlContract.Recipe && metadata.materialLayerOrder != MeshMaterialLayerOrder.Recipe)
                throw new InvalidDataException("The v5 controls require the rock/soil/grass material layer order.");
            long total = 0;
            for (int level = 0; level < MeshControlContract.LevelCount; ++level)
            {
                MeshControlLevel entry = metadata.levels[level]; int width = MeshControlContract.WidthAtLevel(level);
                if (entry == null || entry.level != level || entry.stride != MeshControlContract.Stride(level) || entry.width != width ||
                    entry.fileName != MeshControlContract.LevelFile(level) || entry.bytes != (long)width*width*2 || entry.sha256 == null || entry.sha256.Length != 64 ||
                    entry.repair == null || entry.repair.invalidAfter != 0 || entry.materialPointCounts == null || entry.materialPointCounts.Length != MeshControlContract.MaterialCount)
                    throw new InvalidDataException("Control level manifest mismatch: "+level);
                var info = new FileInfo(Path.Combine(directory,entry.fileName));
                if (!info.Exists || info.Length != entry.bytes) throw new InvalidDataException("Control source file length mismatch: "+level);
                total += entry.bytes;
            }
            if (metadata.bytes != total) throw new InvalidDataException("Control source total size mismatch.");
            return metadata;
        }

        public static void ValidateMaterialContract(string directory, MeshMaterialSet materials)
        {
            MeshControlMetadata metadata = ReadMetadata(directory);
            // 新控制ID已经重新编号，禁止将它们误绑定到旧切片顺序，反向也同样禁止。
            // 这只是初始化时核对资产身份，不是每像素ID到slice的映射。
            if (!materials || (metadata.materialLayerOrder ?? "") != (materials.layerOrder ?? ""))
                throw new InvalidDataException("Control-map IDs and material-array layer order do not match. Load their matching asset pair.");
        }
    }

    public sealed class MeshControlMap : IDisposable
    {
        sealed class UploadStage : IDisposable
        {
            public Texture2D texture;
            public ushort[] values;
            public long Bytes => values == null ? 0 : values.LongLength*2;
            public void Ensure(int width, int height)
            {
                if (texture && texture.width == width && texture.height == height) return;
                Dispose();
                texture = new Texture2D(width,height,TextureFormat.R16,false,true)
                { name = "ID Clipmap upload "+width+"x"+height, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                values = new ushort[width*height];
            }
            public void Dispose() { MeshResidentHeight.Destroy(texture); texture = null; values = null; }
        }
        readonly string directory;
        readonly FileStream[] streams = new FileStream[MeshControlContract.LevelCount];
        readonly byte[] rowBytes = new byte[MeshControlContract.ClipSize*2];
        readonly UploadStage full = new UploadStage(), column = new UploadStage(), row = new UploadStage();
        public RenderTexture Texture { get; private set; }
        public MeshControlMetadata Metadata { get; private set; }
        public Vector4[] Origins { get; } = new Vector4[MeshControlContract.LevelCount];
        public int Width => Metadata == null ? 0 : Metadata.width;
        public int Spacing => 1;
        public int RepairCount
        { get { int count = 0; if (Metadata != null) foreach (var level in Metadata.levels) count += level.repair.removedTops; return count; } }
        public long GpuBytes => Texture ? (long)Texture.width*Texture.height*Texture.volumeDepth*2 : 0;
        public long CpuStagingBytes => (full.Bytes+column.Bytes+row.Bytes)*2+rowBytes.Length;
        public long GpuStagingBytes => full.Bytes+column.Bytes+row.Bytes;
        public long BytesRead { get; private set; }
        public int ReadCommands { get; private set; }
        public long UploadedBytes { get; private set; }
        public int Uploads { get; private set; }
        public int FullUploads { get; private set; }
        public int StripUploads { get; private set; }
        public int CopyCommands { get; private set; }
        public long LastUploadedBytes { get; private set; }
        public int LastUploads { get; private set; }
        public double LastUpdateMilliseconds { get; private set; }

        public MeshControlMap(string directory)
        {
            this.directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
            Metadata = MeshControlBake.ReadMetadata(this.directory);
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.R16) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm,FormatUsage.Render) || !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm,FormatUsage.Sample) ||
                (SystemInfo.copyTextureSupport&(CopyTextureSupport.Basic|CopyTextureSupport.DifferentTypes|CopyTextureSupport.TextureToRT)) !=
                    (CopyTextureSupport.Basic|CopyTextureSupport.DifferentTypes|CopyTextureSupport.TextureToRT))
                throw new NotSupportedException("ID Clipmap requires R16 array render textures and GPU copies.");
            try
            {
                for (int level = 0; level < streams.Length; ++level)
                    streams[level] = new FileStream(Path.Combine(this.directory,Metadata.levels[level].fileName),FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.RandomAccess);
                var descriptor = new RenderTextureDescriptor(MeshControlContract.ClipSize,MeshControlContract.ClipSize)
                { graphicsFormat = GraphicsFormat.R16_UNorm, depthBufferBits = 0, msaaSamples = 1, dimension = TextureDimension.Tex2DArray,
                    volumeDepth = MeshControlContract.LevelCount, useMipMap = false, autoGenerateMips = false, enableRandomWrite = false };
                Texture = new RenderTexture(descriptor)
                { name = "Delta one-metre ID Clipmap 655x655x6 R16", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
                if (!Texture.Create()) throw new InvalidOperationException("Could not allocate the ID Clipmap.");
            }
            catch { Dispose(); throw; }
        }
        public void UpdateCenter(Vector2 world, bool force = false)
        {
            RequireLive();
            if (float.IsNaN(world.x) || float.IsInfinity(world.x) || float.IsNaN(world.y) || float.IsInfinity(world.y)) throw new ArgumentException("Control center must be finite.");
            world.x = Mathf.Clamp(world.x,0,MeshControlContract.WorldSize); world.y = Mathf.Clamp(world.y,0,MeshControlContract.WorldSize);
            long started = System.Diagnostics.Stopwatch.GetTimestamp(), before = UploadedBytes; int uploadsBefore = Uploads;
            const int n = MeshControlContract.ClipSize;
            for (int level = 0; level < MeshControlContract.LevelCount; ++level)
            {
                int stride = MeshControlContract.Stride(level);
                int ox = level == MeshControlContract.LevelCount-1 ? 0 : Mathf.FloorToInt(world.x/stride/MeshControlContract.SnapSamples)*MeshControlContract.SnapSamples-n/2;
                int oz = level == MeshControlContract.LevelCount-1 ? 0 : Mathf.FloorToInt(world.y/stride/MeshControlContract.SnapSamples)*MeshControlContract.SnapSamples-n/2;
                Vector4 old = Origins[level]; int previousX = Mathf.RoundToInt(old.x/stride), previousZ = Mathf.RoundToInt(old.y/stride);
                int dx = ox-previousX, dz = oz-previousZ;
                if (!force && old.w > .5f && dx == 0 && dz == 0) continue;
                try
                {
                    if (force || old.w < .5f || Math.Abs(dx) >= n || Math.Abs(dz) >= n)
                    { Upload(level,ox,oz,n,n,full); FullUploads++; }
                    else
                    {
                        if (dx != 0) { Upload(level,dx > 0 ? previousX+n : ox,oz,Math.Abs(dx),n,column); StripUploads++; }
                        int left = Math.Max(ox,previousX), right = Math.Min(ox+n,previousX+n);
                        if (dz != 0 && right > left) { Upload(level,left,dz > 0 ? previousZ+n : oz,right-left,Math.Abs(dz),row); StripUploads++; }
                    }
                    Origins[level] = new Vector4(ox*stride,oz*stride,stride,1);
                }
                catch { Origins[level] = new Vector4(0,0,stride,0); throw; }
            }
            LastUploadedBytes = UploadedBytes-before; LastUploads = Uploads-uploadsBefore;
            LastUpdateMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
        }
        void Upload(int level, int gx, int gz, int width, int height, UploadStage stage)
        {
            stage.Ensure(width,height); CopyRectangle(level,gx,gz,width,height,stage.values);
            stage.texture.SetPixelData(stage.values,0); stage.texture.Apply(false,false);
            UploadedBytes += (long)width*height*2; Uploads++;
            const int n = MeshControlContract.ClipSize;
            for (int sy = 0; sy < height;)
            {
                int dy = Mod(gz+sy,n), h = Math.Min(height-sy,n-dy);
                for (int sx = 0; sx < width;)
                {
                    int dx = Mod(gx+sx,n), w = Math.Min(width-sx,n-dx);
                    Graphics.CopyTexture(stage.texture,0,0,sx,sy,w,h,Texture,level,0,dx,dy); CopyCommands++; sx += w;
                }
                sy += h;
            }
        }
        void CopyRectangle(int level, int gx, int gz, int width, int height, ushort[] output)
        {
            int sourceWidth = Metadata.levels[level].width, maximum = sourceWidth-1;
            for (int rowIndex = 0; rowIndex < height; ++rowIndex)
            {
                int sourceZ = Math.Max(0,Math.Min(maximum,gz+rowIndex)), written = 0;
                while (written < width)
                {
                    int sourceX = gx+written;
                    if (sourceX < 0 || sourceX > maximum)
                    {
                        ushort value = ReadPoint(sourceX < 0 ? 0 : maximum,sourceZ,level);
                        int end = written+(sourceX < 0 ? Math.Min(width-written,-sourceX) : width-written);
                        while (written < end) output[rowIndex*width+written++] = value;
                    }
                    else
                    {
                        int count = Math.Min(width-written,sourceWidth-sourceX);
                        ReadRow(level,sourceX,sourceZ,count);
                        for (int i = 0; i < count; ++i) output[rowIndex*width+written+i] = (ushort)(rowBytes[i*2]|rowBytes[i*2+1]<<8);
                        written += count;
                    }
                }
            }
        }
        void ReadRow(int level, int x, int z, int count)
        {
            FileStream stream = streams[level]; stream.Position = ((long)z*Metadata.levels[level].width+x)*2;
            int total = count*2, read = 0;
            while (read < total) { int got = stream.Read(rowBytes,read,total-read); if (got <= 0) throw new EndOfStreamException("Control source changed during reading."); read += got; }
            BytesRead += total; ReadCommands++;
        }
        public ushort ReadPoint(int x, int z, int level = 0)
        {
            RequireLive(); MeshControlContract.Stride(level);
            int maximum = Metadata.levels[level].width-1;
            x = Math.Max(0,Math.Min(maximum,x)); z = Math.Max(0,Math.Min(maximum,z));
            ReadRow(level,x,z,1); return (ushort)(rowBytes[0]|rowBytes[1]<<8);
        }
        public void Bind(Material material)
        {
            RequireLive(); material.DisableKeyword("DD_CONTROL_RESIDENT"); material.SetTexture("_DDControlClip",Texture);
            material.SetVectorArray("_DDControlOrigins",Origins);
            material.SetVector("_DDControlInfo",new Vector4(MeshControlContract.WorldSize,MeshControlContract.ClipSize,MeshControlContract.LevelCount,1));
        }
        static int Mod(int value, int n) { int result = value%n; return result < 0 ? result+n : result; }
        void RequireLive() { if (!Texture) throw new ObjectDisposedException(nameof(MeshControlMap)); }
        public void Dispose()
        {
            full.Dispose(); column.Dispose(); row.Dispose();
            for (int level = 0; level < streams.Length; ++level) { streams[level]?.Dispose(); streams[level] = null; Origins[level] = Vector4.zero; }
            if (Texture) { Texture.Release(); MeshResidentHeight.Destroy(Texture); Texture = null; }
            Metadata = null;
        }
    }
}
