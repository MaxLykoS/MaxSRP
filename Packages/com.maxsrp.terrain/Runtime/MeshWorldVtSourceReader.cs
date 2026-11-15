using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MaxTerrain
{
    /// <summary>
    /// 只供RVT后台队列使用的固定世界源读取器。它不持有Unity对象，
    /// 不与显示Clipmap共享LRU、文件游标或生命周期；构造时只保存不可变路径/数值。
    /// </summary>
    public sealed class MeshWorldVtSourceReader : IDisposable
    {
        public const int HeightCacheCapacity = 12;
        sealed class Tile { public int Key; public long Use; public ushort[] Values; }
        readonly string heightDirectory, controlPath;
        readonly int controlWidth;
        readonly float heightScale;
        readonly Dictionary<int, Tile> heightCache = new Dictionary<int, Tile>();
        byte[] heightScratch;
        FileStream controls;
        long clock, heightBytesRead;
        int cachedTiles;
        bool disposed;

        public MeshWorldVtSourceReader(string heightDirectory, string controlPath, int controlWidth, float heightScale)
        {
            this.heightDirectory = Path.GetFullPath(heightDirectory);
            this.controlPath = Path.GetFullPath(controlPath);
            if (controlWidth != MeshControlContract.Width || float.IsNaN(heightScale) || float.IsInfinity(heightScale) || heightScale <= 0)
                throw new ArgumentException("RVT source layout must match the fixed 1m lattice.");
            this.controlWidth = controlWidth;
            this.heightScale = heightScale;
        }

        public int CachedHeightTiles => Volatile.Read(ref cachedTiles);
        public long HeightCacheBytes => (long)CachedHeightTiles * MeshHeightLayout.TileBytes;
        public long HeightBytesRead => Interlocked.Read(ref heightBytesRead);

        public MeshWorldVtSourcePage Read(uint key, CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtSourceReader));
            cancellation.ThrowIfCancellationRequested();
            DescribePage(key, out int sourceX, out int sourceZ, out int side);
            var heights = new ushort[side * side];
            var controlValues = new ushort[side * side];
            ReadHeights(sourceX, sourceZ, side, heights, cancellation);
            ReadControls(sourceX, sourceZ, side, controlValues, cancellation);
            cancellation.ThrowIfCancellationRequested();
            ValidatePageSources(key, sourceX, sourceZ, side, heights, controlValues, heightScale);
            return new MeshWorldVtSourcePage(key, sourceX, sourceZ, side, heights, controlValues);
        }

        /// <summary>与同步写页共用窗口公式，绝不使用观察相机或移动Clipmap窗口。</summary>
        public static void DescribePage(uint key, out int sourceX, out int sourceZ, out int side)
        {
            if (!MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int x, out int z) || mip > MeshWorldVirtualTexture.FirstSvtMip)
                throw new ArgumentOutOfRangeException(nameof(key));
            float size = MeshWorldVirtualTexture.PageWorldSize(mip);
            float originX = x * size, originZ = z * size;
            float padding = size * MeshWorldVirtualTexture.Border / MeshWorldVirtualTexture.PageInterior + 2;
            sourceX = (int)Math.Floor(originX - padding);
            sourceZ = (int)Math.Floor(originZ - padding);
            int endX = (int)Math.Ceiling(originX + size + padding), endZ = (int)Math.Ceiling(originZ + size + padding);
            side = Math.Max(endX - sourceX, endZ - sourceZ) + 1;
        }

        void ReadHeights(int sourceX, int sourceZ, int side, ushort[] output, CancellationToken cancellation)
        {
            const int maximum = MeshHeightLayout.WorldSize;
            for (int row = 0; row < side; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                int gz = Clamp(sourceZ + row, 0, maximum);
                int tz = gz / MeshHeightLayout.TileIntervals, lz = gz - tz * MeshHeightLayout.TileIntervals;
                int written = 0;
                while (written < side)
                {
                    int gx = sourceX + written;
                    if (gx < 0 || gx > maximum)
                    {
                        int edge = gx < 0 ? 0 : maximum;
                        int tx = edge / MeshHeightLayout.TileIntervals;
                        ushort[] tile = ReadHeightTile(tx, tz, cancellation);
                        ushort value = tile[lz * MeshHeightLayout.TileResolution + edge - tx * MeshHeightLayout.TileIntervals];
                        int count = gx < 0 ? Math.Min(side - written, -gx) : side - written;
                        int end = written + count;
                        while (written < end) output[row * side + written++] = value;
                    }
                    else
                    {
                        int tx = gx / MeshHeightLayout.TileIntervals, lx = gx - tx * MeshHeightLayout.TileIntervals;
                        int count = Math.Min(side - written, Math.Min(MeshHeightLayout.TileIntervals - lx, maximum - gx + 1));
                        ushort[] tile = ReadHeightTile(tx, tz, cancellation);
                        Buffer.BlockCopy(tile, (lz * MeshHeightLayout.TileResolution + lx) * sizeof(ushort),
                            output, (row * side + written) * sizeof(ushort), count * sizeof(ushort));
                        written += count;
                    }
                }
            }
        }

        ushort[] ReadHeightTile(int x, int z, CancellationToken cancellation)
        {
            int key = z * MeshHeightLayout.TilesPerAxis + x;
            if (heightCache.TryGetValue(key, out Tile cached)) { cached.Use = ++clock; return cached.Values; }
            cancellation.ThrowIfCancellationRequested();
            if (heightScratch == null) heightScratch = new byte[MeshHeightLayout.TileBytes];
            using (var file = new FileStream(Path.Combine(heightDirectory, MeshHeightLayout.TileName(0, x, z)),
                FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                if (file.Length != heightScratch.Length) throw new InvalidDataException("RVT height tile length changed during use.");
                ReadExactly(file, heightScratch, heightScratch.Length, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (heightCache.Count == HeightCacheCapacity)
            {
                Tile oldest = null;
                foreach (Tile entry in heightCache.Values) if (oldest == null || entry.Use < oldest.Use) oldest = entry;
                heightCache.Remove(oldest.Key);
            }
            var values = new ushort[MeshHeightLayout.TileResolution * MeshHeightLayout.TileResolution];
            Buffer.BlockCopy(heightScratch, 0, values, 0, heightScratch.Length);
            // Surface16 source contract is little endian, also checked by the producer.
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < values.Length; i++) values[i] = (ushort)((values[i] >> 8) | (values[i] << 8));
            heightCache.Add(key, new Tile { Key = key, Use = ++clock, Values = values });
            Volatile.Write(ref cachedTiles, heightCache.Count);
            Interlocked.Add(ref heightBytesRead, heightScratch.Length);
            return values;
        }

        void ReadControls(int sourceX, int sourceZ, int side, ushort[] output, CancellationToken cancellation)
        {
            if (controls == null)
                controls = new FileStream(controlPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            int left = Clamp(sourceX, 0, MeshControlContract.WorldSize), right = Clamp(sourceX + side - 1, 0, MeshControlContract.WorldSize);
            int column = Math.Min(side - 1, Math.Max(0, left - sourceX)), count = right - left + 1;
            var bytes = new byte[side * sizeof(ushort)];
            int previousZ = -1;
            for (int z = 0; z < side; z++)
            {
                cancellation.ThrowIfCancellationRequested();
                int worldZ = Clamp(sourceZ + z, 0, MeshControlContract.WorldSize), rowStart = z * side;
                if (worldZ == previousZ) { Array.Copy(output, rowStart - side, output, rowStart, side); continue; }
                previousZ = worldZ;
                controls.Position = ((long)worldZ * controlWidth + left) * sizeof(ushort);
                ReadExactly(controls, bytes, count * sizeof(ushort), cancellation);
                Buffer.BlockCopy(bytes, 0, output, (rowStart + column) * sizeof(ushort), count * sizeof(ushort));
                ushort first = output[rowStart + column], last = output[rowStart + column + count - 1];
                for (int i = 0; i < column; i++) output[rowStart + i] = first;
                for (int i = column + count; i < side; i++) output[rowStart + i] = last;
            }
        }

        static void ReadExactly(Stream stream, byte[] bytes, int count, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < count)
            {
                cancellation.ThrowIfCancellationRequested();
                int received = stream.Read(bytes, offset, count - offset);
                if (received <= 0) throw new EndOfStreamException("World VT data ended inside a page or row.");
                offset += received;
            }
        }

        // Shared by synchronous/offline and asynchronous/runtime producers.
        // All calls are pure CPU; do not introduce Mathf or UnityEngine object access here.
        public static void ValidatePageSources(uint key, int sourceX, int sourceZ, int side,
            ushort[] heights, ushort[] controlValues, float heightScale)
        {
            if (!MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int pageX, out int pageZ) ||
                mip > MeshWorldVirtualTexture.FirstSvtMip || side < 2 || heights == null || controlValues == null ||
                heights.LongLength != (long)side * side || controlValues.LongLength != (long)side * side ||
                float.IsNaN(heightScale) || float.IsInfinity(heightScale) || heightScale <= 0)
                throw new InvalidDataException("World VT page source dimensions, address or height scale are invalid.");
            float step = MeshWorldVirtualTexture.PageWorldSize(mip) / MeshWorldVirtualTexture.PageInterior;
            float first = .5f - MeshWorldVirtualTexture.Border, last = MeshWorldVirtualTexture.PageSide - .5f - MeshWorldVirtualTexture.Border;
            ValidateAxis((pageX * MeshWorldVirtualTexture.PageInterior + first) * step,
                (pageX * MeshWorldVirtualTexture.PageInterior + last) * step, sourceX, side);
            ValidateAxis((pageZ * MeshWorldVirtualTexture.PageInterior + first) * step,
                (pageZ * MeshWorldVirtualTexture.PageInterior + last) * step, sourceZ, side);
            if (MeshControlContract.CountInvalidTriangles(controlValues, side, side) != 0)
                throw new InvalidDataException("World VT page source contains an unrepaired control triangle with more than three materials.");
        }

        static void ValidateAxis(float minimum, float maximum, int origin, int side)
        {
            const int world = MeshControlContract.WorldSize;
            int heightLow = (int)Math.Floor(Clamp(minimum - 1, 0, world));
            int heightHigh = (int)Math.Floor(Clamp(maximum + 1, 0, world)) + 1;
            int controlLow = Math.Min((int)Math.Floor(Clamp(minimum, 0, world)), world - 1);
            int controlHigh = Math.Min((int)Math.Floor(Clamp(maximum, 0, world)), world - 1) + 1;
            long end = (long)origin + side - 1;
            if (heightLow < origin || heightHigh > end || controlLow < origin || controlHigh > end)
                throw new InvalidDataException("World VT source window does not cover the full page border, height derivatives and control vertices.");
        }

        static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(value, maximum));
        static float Clamp(float value, float minimum, float maximum) => Math.Max(minimum, Math.Min(value, maximum));

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            controls?.Dispose();
            controls = null;
            heightCache.Clear();
            heightScratch = null;
            Volatile.Write(ref cachedTiles, 0);
        }
    }
}
