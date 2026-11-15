using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// UE 5.8 AdaptiveVirtualTexture 的职责适配：持久粗表、动态局部表、目录、重映射与回收。
    /// Unity 使用一个 uint Buffer 保存目录和完整页表 mip 链；物理材质页不在这里存放。
    /// 全局世界页身份不随分配变化。反馈里的页表地址必须经过分配年龄检查再翻译。
    /// </summary>
    public sealed class MeshAdaptiveVtTable
    {
        public const int DirectorySide = 128, DirectoryWords = DirectorySide * DirectorySide;
        public const int TableSide = 1024, LowMip = 8, MaxLocalLevel = 8;
        public const int WordCount = DirectoryWords + 1398101;
        public const int MaxAllocations = 256; // 同时锁定的局部根页上限，另于页表面积预算。
        public const int MinLocalLevel = 1, InitialLocalLevel = 3;
        const int OwnerSide = TableSide >> MinLocalLevel, ChunkSize = 256;
        const uint ValidDirectory = 1u << 28;

        public sealed class Allocation
        {
            public int Grid { get; internal set; }
            public int X { get; internal set; }
            public int Y { get; internal set; }
            public int Level { get; internal set; }
            public int BirthFrame { get; internal set; }
            public int LastUsedFrame { get; internal set; }
            public bool Published { get; internal set; }
            public uint RootKey => MeshWorldVirtualTexture.EncodeKey(LowMip, Grid % DirectorySide, Grid / DirectorySide);
        }

        struct Growth { public int Level, Score; }
        readonly Dictionary<uint, int> resident;
        readonly Dictionary<int, Allocation> regions = new Dictionary<int, Allocation>();
        readonly Dictionary<int, Growth> growth = new Dictionary<int, Growth>();
        readonly HashSet<int>[] free = new HashSet<int>[11];
        // 每个最小2×2页表区域记录所属目录格；-1是持久粗表，0是未分配。
        // grid+1 最大16384，short可容纳全部目录格，CPU载荷为512KiB。
        readonly short[] owners = new short[OwnerSide * OwnerSide];
        readonly bool[] dirty = new bool[(WordCount + ChunkSize - 1) / ChunkSize];
        readonly List<int> dirtyChunks = new List<int>();
        readonly int[] mipOffsets = new int[11];
        readonly List<KeyValuePair<uint, int>> cachedFinePages = new List<KeyValuePair<uint, int>>();
        int occupied = DirectoryWords;

        public uint[] Words { get; } = new uint[WordCount];
        public IEnumerable<Allocation> Regions => regions.Values;
        public int ActiveCount => regions.Count;
        public int Revision { get; private set; }
        public int Allocations { get; private set; }
        public int Resizes { get; private set; }
        public int Shrinks { get; private set; }
        public int GrowthFallbacks { get; private set; }
        public int AllocationFailures { get; private set; }
        public int Releases { get; private set; }
        public int StaleFeedback { get; private set; }
        public int InvalidFeedback { get; private set; }
        public long BytesUploaded { get; private set; }
        public int OccupiedTexels => occupied;
        public IEnumerable<uint> LockedRoots
        {
            get { foreach (Allocation allocation in regions.Values) yield return allocation.RootKey; }
        }

        public MeshAdaptiveVtTable(Dictionary<uint, int> residentPages)
        {
            resident = residentPages ?? throw new ArgumentNullException(nameof(residentPages));
            int offset = DirectoryWords;
            for (int mip = 0; mip <= 10; mip++)
            {
                mipOffsets[mip] = offset;
                int side = TableSide >> mip;
                offset += side * side;
                free[mip] = new HashSet<int>();
            }
            free[10].Add(0);
            if (!TryAllocate(7, out int x, out int y) || x != 0 || y != 0)
                throw new InvalidOperationException("The persistent AVT low-mip table must occupy (0,0). ");
            SetOwner(0, 0, 7, -1);
            MarkDirty(0, Words.Length); // 新 GPU Buffer 的全部未映射项必须清零。
        }

        public void BeginFeedback() => growth.Clear();
        public void ClearRequests() => growth.Clear();

        /// <summary>把32位页表空间反馈变成稳定的世界页键。负局部mip同时申请扩大分配。</summary>
        public bool DecodeFeedback(uint encoded, int generatedFrame, int currentFrame, out uint key)
        {
            key = 0;
            if (encoded == 0) return false;
            uint packed = encoded - 1;
            int x = (int)(packed & 4095), y = (int)((packed >> 12) & 4095);
            int localMip = (int)((packed >> 24) & 15) - 3;
            int shift = Math.Max(localMip, 0);
            int baseX = x << shift, baseY = y << shift;
            if ((packed >> 28) != 0 || (uint)baseX >= TableSide || (uint)baseY >= TableSide)
            { InvalidFeedback++; return false; }
            int owner = owners[(baseY >> MinLocalLevel) * OwnerSide + (baseX >> MinLocalLevel)];
            if (owner == -1)
            {
                if (localMip > 7 || x >= (DirectorySide >> shift) || y >= (DirectorySide >> shift))
                { InvalidFeedback++; return false; }
                if (localMip < 0) QueueGrowth(y * DirectorySide + x, InitialLocalLevel, currentFrame);
                key = MeshWorldVirtualTexture.EncodeKey(LowMip + shift, x, y);
                return true;
            }
            if (owner == 0 || !regions.TryGetValue(owner - 1, out Allocation allocation) ||
                generatedFrame < allocation.BirthFrame)
            { StaleFeedback++; return false; }
            if (localMip > allocation.Level)
            { InvalidFeedback++; return false; }
            allocation.LastUsedFrame = currentFrame;
            if (localMip < 0) QueueGrowth(allocation.Grid, allocation.Level + 3, currentFrame);
            int globalMip = LowMip - allocation.Level + shift;
            int relativeX = x - (allocation.X >> shift), relativeY = y - (allocation.Y >> shift);
            int gridPageSide = 1 << (allocation.Level - shift);
            if ((uint)relativeX >= gridPageSide || (uint)relativeY >= gridPageSide)
            { InvalidFeedback++; return false; }
            key = MeshWorldVirtualTexture.EncodeKey(globalMip,
                (allocation.Grid % DirectorySide) * gridPageSide + relativeX,
                (allocation.Grid / DirectorySide) * gridPageSide + relativeY);
            return true;
        }

        void QueueGrowth(int grid, int level, int frame)
        {
            if (regions.TryGetValue(grid, out Allocation allocation))
            {
                allocation.LastUsedFrame = frame;
                if (!allocation.Published || allocation.Level >= Math.Min(MaxLocalLevel, level)) return;
            }
            growth.TryGetValue(grid, out Growth request);
            request.Level = Math.Max(request.Level, Math.Min(MaxLocalLevel, level));
            request.Score++;
            growth[grid] = request;
        }

        /// <summary>局部分配预算每帧至多一增一减；根页到位之前不把新目录暴露给GPU。</summary>
        public bool UpdateAllocations(int frame, int recentVisibleCount)
        {
            int before = Revision;
            foreach (Allocation allocation in regions.Values)
            {
                if (allocation.Published || !resident.ContainsKey(allocation.RootKey)) continue;
                Populate(allocation);
                Publish(allocation);
            }
            int bestGrid = -1, bestScore = -1, bestLevel = 0;
            foreach (var pair in growth)
            {
                if (pair.Value.Score <= bestScore) continue;
                if (regions.TryGetValue(pair.Key, out Allocation current) &&
                    (!current.Published || current.Level >= pair.Value.Level)) continue;
                bestGrid = pair.Key; bestScore = pair.Value.Score; bestLevel = pair.Value.Level;
            }
            if (bestGrid >= 0)
            {
                // 高占用时优先回收不在当前反馈中的区域，避免目录根页不断锁满物理池。
                int beforeReclaim = Revision;
                if (occupied > TableSide * TableSide * 3 / 4 || regions.Count >= MaxAllocations)
                    Reclaim(frame, 15, regions.Count >= MaxAllocations);
                bool reclaimed = Revision != beforeReclaim;
                regions.TryGetValue(bestGrid, out Allocation previous);
                if (previous != null || regions.Count < MaxAllocations)
                {
                    if (!TryGrow(bestGrid, previous, bestLevel, frame))
                    {
                        if (!reclaimed)
                        {
                            Reclaim(frame, 15, false);
                            regions.TryGetValue(bestGrid, out previous);
                            TryGrow(bestGrid, previous, bestLevel, frame);
                        }
                    }
                }
                growth.Remove(bestGrid);
            }
            else if (recentVisibleCount * 100L > MeshWorldVirtualTexture.Slots * 85L)
                Reclaim(frame, 1000, false);
            return before != Revision;
        }

        bool TryGrow(int grid, Allocation previous, int targetLevel, int frame)
        {
            int minimumLevel = previous == null ? MinLocalLevel : previous.Level + 1;
            // Unity固定页表预算的适配：完整的+3级分配放不下时，也可先增加一两级。
            // 通常首次仍申请level3；只有碎片/空间不足时才退到更小的可用区域。
            for (int level = targetLevel; level >= minimumLevel; level--)
                if (Resize(grid, previous, level, frame, false))
                {
                    if (level != targetLevel) GrowthFallbacks++;
                    return true;
                }
            AllocationFailures++;
            return false;
        }

        bool Resize(int grid, Allocation previous, int level, int frame, bool preserveLastUsed)
        {
            if (previous != null && previous.Level == level) return true;
            if (!TryAllocate(level, out int x, out int y)) return false;
            var next = new Allocation { Grid = grid, X = x, Y = y, Level = level,
                BirthFrame = frame, LastUsedFrame = preserveLastUsed ? previous.LastUsedFrame : frame };
            // 迁移只重建映射，物理页仍按原世界键驻留，不重新生成材质。
            if (resident.ContainsKey(next.RootKey))
            {
                Populate(next);
                Publish(next);
            }
            else SetWord(grid, 0);
            if (previous != null)
            {
                SetOwner(previous.X, previous.Y, previous.Level, 0);
                FreeRegion(previous.X, previous.Y, previous.Level);
                occupied -= 1 << (previous.Level * 2);
                if (level < previous.Level) Shrinks++;
                Resizes++;
            }
            else Allocations++;
            regions[grid] = next;
            SetOwner(x, y, level, grid + 1);
            occupied += 1 << (level * 2);
            Revision++;
            return true;
        }

        void Reclaim(int frame, int unusedFrames, bool releaseAll)
        {
            Allocation victim = null;
            foreach (Allocation allocation in regions.Values)
                if (!growth.ContainsKey(allocation.Grid) && (long)frame - allocation.LastUsedFrame >= unusedFrames &&
                    (victim == null || allocation.LastUsedFrame < victim.LastUsedFrame ||
                    (allocation.LastUsedFrame == victim.LastUsedFrame && allocation.Level < victim.Level))) victim = allocation;
            if (victim == null) return;
            // 与UE FreeLRU一致：逐级找能分配的小区域；缩容不刷新最近使用时间，
            // 因此长期不用的区域可在后续帧继续缩小，而不是每一级再等1000帧。
            if (!releaseAll)
                for (int level = victim.Level - 1; level >= MinLocalLevel; level--)
                    if (Resize(victim.Grid, victim, level, frame, true)) return;
            SetWord(victim.Grid, 0);
            SetOwner(victim.X, victim.Y, victim.Level, 0);
            FreeRegion(victim.X, victim.Y, victim.Level);
            occupied -= 1 << (victim.Level * 2);
            regions.Remove(victim.Grid);
            Releases++; Revision++;
        }

        /// <summary>当前分配能否表达这个稳定世界页；粗页始终由持久表表达。</summary>
        public bool CanRepresent(uint key)
        {
            if (!MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int x, out int y)) return false;
            if (mip >= LowMip) return true;
            int shift = LowMip - mip;
            int grid = (y >> shift) * DirectorySide + (x >> shift);
            return regions.TryGetValue(grid, out Allocation allocation) && allocation.Level >= shift;
        }

        void Publish(Allocation allocation)
        {
            allocation.Published = true;
            SetWord(allocation.Grid, (uint)allocation.X | ((uint)allocation.Y << 12) |
                ((uint)allocation.Level << 24) | ValidDirectory);
            Revision++;
        }

        static uint Entry(int slot, int mip) => (uint)(slot + 1) | ((uint)mip << 11);

        void Populate(Allocation allocation)
        {
            uint root = Entry(resident[allocation.RootKey], LowMip);
            for (int mip = 0; mip <= allocation.Level; mip++)
            {
                int side = 1 << (allocation.Level - mip);
                Fill(mip, allocation.X >> mip, allocation.Y >> mip, side, root, 0, false, true);
            }
            cachedFinePages.Clear();
            foreach (var pair in resident)
                if (MeshWorldVirtualTexture.DecodeKey(pair.Key, out int mip, out int px, out int py) &&
                    mip < LowMip && mip >= LowMip - allocation.Level &&
                    (px >> (LowMip - mip)) == allocation.Grid % DirectorySide &&
                    (py >> (LowMip - mip)) == allocation.Grid / DirectorySide)
                    cachedFinePages.Add(pair);
            // 覆盖顺序无关：更精细的实际驻留mip始终保留。
            foreach (var pair in cachedFinePages)
            {
                MeshWorldVirtualTexture.DecodeKey(pair.Key, out int mip, out int px, out int py);
                ApplyLocal(allocation, mip, px, py, Entry(pair.Value, mip), 0, false);
            }
        }

        public void PageAdded(uint key, int slot) => ChangePage(key, Entry(slot, GlobalMip(key)), 0, false);
        public void PageRemoved(uint key, int slot)
        {
            MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int x, out int y);
            uint parent = 0;
            for (int p = mip + 1; p <= MeshWorldVirtualTexture.RootMip; p++)
            {
                uint parentKey = MeshWorldVirtualTexture.EncodeKey(p, x >> (p - mip), y >> (p - mip));
                if (resident.TryGetValue(parentKey, out int parentSlot)) { parent = Entry(parentSlot, p); break; }
            }
            ChangePage(key, parent, Entry(slot, mip), true);
        }

        static int GlobalMip(uint key)
        {
            if (!MeshWorldVirtualTexture.DecodeKey(key, out int mip, out _, out _)) throw new ArgumentOutOfRangeException(nameof(key));
            return mip;
        }

        void ChangePage(uint key, uint value, uint oldValue, bool removing)
        {
            MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int x, out int y);
            if (mip >= LowMip)
            {
                for (int local = 0; local <= mip - LowMip; local++)
                {
                    int shift = mip - LowMip - local;
                    Fill(local, x << shift, y << shift, 1 << shift, value, oldValue, removing, false);
                }
                foreach (Allocation allocation in regions.Values)
                    if (allocation.Published &&
                        (allocation.Grid % DirectorySide >> (mip - LowMip)) == x &&
                        (allocation.Grid / DirectorySide >> (mip - LowMip)) == y)
                        for (int local = 0; local <= allocation.Level; local++)
                            Fill(local, allocation.X >> local, allocation.Y >> local,
                                1 << (allocation.Level - local), value, oldValue, removing, false);
            }
            else
            {
                int grid = (y >> (LowMip - mip)) * DirectorySide + (x >> (LowMip - mip));
                if (regions.TryGetValue(grid, out Allocation allocation) && allocation.Published)
                    ApplyLocal(allocation, mip, x, y, value, oldValue, removing);
            }
        }

        void ApplyLocal(Allocation allocation, int mip, int x, int y, uint value, uint oldValue, bool removing)
        {
            int firstMip = LowMip - allocation.Level;
            if (mip < firstMip) return;
            int relativeX = x - (allocation.Grid % DirectorySide << (LowMip - mip));
            int relativeY = y - (allocation.Grid / DirectorySide << (LowMip - mip));
            for (int local = 0; local <= mip - firstMip; local++)
            {
                int shift = mip - firstMip - local;
                Fill(local, (allocation.X >> local) + (relativeX << shift),
                    (allocation.Y >> local) + (relativeY << shift), 1 << shift,
                    value, oldValue, removing, false);
            }
        }

        void Fill(int mip, int x, int y, int side, uint value, uint oldValue, bool removing, bool overwrite)
        {
            int stride = TableSide >> mip;
            for (int row = 0; row < side; row++)
            {
                int first = mipOffsets[mip] + (y + row) * stride + x;
                for (int column = 0; column < side; column++)
                {
                    int index = first + column;
                    uint current = Words[index];
                    bool replace = overwrite || (removing ? current == oldValue :
                        current == 0 || ((current >> 11) & 31) >= ((value >> 11) & 31));
                    if (replace) SetWord(index, value);
                }
            }
        }

        void SetWord(int index, uint value)
        {
            if (Words[index] == value) return;
            Words[index] = value;
            MarkDirty(index, 1);
        }

        void MarkDirty(int start, int count)
        {
            int end = (start + count - 1) / ChunkSize;
            for (int chunk = start / ChunkSize; chunk <= end; chunk++)
                if (!dirty[chunk]) { dirty[chunk] = true; dirtyChunks.Add(chunk); }
        }

        public bool Upload(ComputeBuffer buffer)
        {
            if (dirtyChunks.Count == 0) return false;
            dirtyChunks.Sort();
            int first = dirtyChunks[0], last = first;
            for (int i = 1; i <= dirtyChunks.Count; i++)
            {
                // 合并行间小间隔，避免局部矩形产生上百次SetData；静止时零上传。
                if (i < dirtyChunks.Count && dirtyChunks[i] <= last + 4) { last = dirtyChunks[i]; continue; }
                int start = first * ChunkSize;
                int count = Math.Min(Words.Length, (last + 1) * ChunkSize) - start;
                buffer.SetData(Words, start, start, count);
                BytesUploaded += (long)count * sizeof(uint);
                if (i < dirtyChunks.Count) first = last = dirtyChunks[i];
            }
            foreach (int chunk in dirtyChunks) dirty[chunk] = false;
            dirtyChunks.Clear();
            return true;
        }

        bool TryAllocate(int level, out int x, out int y)
        {
            x = y = 0;
            int found = level;
            while (found <= 10 && free[found].Count == 0) found++;
            if (found > 10) return false;
            int packed = int.MaxValue;
            foreach (int candidate in free[found]) if (candidate < packed) packed = candidate;
            free[found].Remove(packed);
            x = packed & 1023; y = packed >> 10;
            while (found > level)
            {
                found--;
                int side = 1 << found;
                free[found].Add((x + side) | (y << 10));
                free[found].Add(x | ((y + side) << 10));
                free[found].Add((x + side) | ((y + side) << 10));
            }
            return true;
        }

        void FreeRegion(int x, int y, int level)
        {
            free[level].Add(x | (y << 10));
            while (level < 10)
            {
                int side = 1 << level, parentX = x & ~(side * 2 - 1), parentY = y & ~(side * 2 - 1);
                int a = parentX | (parentY << 10), b = (parentX + side) | (parentY << 10);
                int c = parentX | ((parentY + side) << 10), d = (parentX + side) | ((parentY + side) << 10);
                if (!free[level].Contains(a) || !free[level].Contains(b) || !free[level].Contains(c) || !free[level].Contains(d)) break;
                free[level].Remove(a); free[level].Remove(b); free[level].Remove(c); free[level].Remove(d);
                x = parentX; y = parentY; level++;
                free[level].Add(x | (y << 10));
            }
        }

        void SetOwner(int x, int y, int level, int owner)
        {
            short packedOwner = checked((short)owner);
            int side = (1 << level) >> MinLocalLevel;
            for (int row = 0; row < side; row++)
            for (int column = 0; column < side; column++)
                owners[((y >> MinLocalLevel) + row) * OwnerSide + (x >> MinLocalLevel) + column] = packedOwner;
        }
    }
}
