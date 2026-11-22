using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// 与视锥无关的周边中精度覆盖。只申请持久粗页表能够表达的 SVT 页，
    /// 不为隐藏地形扩张精细 AVT 区域；跨越一个世界 sector 才重建需求。
    /// </summary>
    public sealed class MeshWorldVtPrefetch
    {
        public const float Radius = 400f;
        public const int MaximumPages = 128;

        struct Candidate
        {
            public uint Key;
            public int DistanceSquared;
        }

        readonly List<uint> keys = new List<uint>(MaximumPages);
        readonly List<Candidate> candidates = new List<Candidate>(MaximumPages);
        static readonly Comparison<Candidate> compareCandidates = CompareCandidates;
        int centerX = -1, centerZ = -1;

        public IReadOnlyList<uint> Keys => keys;

        /// <summary>
        /// 以相机所在 mip8 页的中心为圆心，约 400 米内全向预取。
        /// 有限的越界坐标贴到地形边缘；无效坐标清空需求，不保留旧位置。
        /// </summary>
        public bool Update(Vector2 position)
        {
            if (float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                float.IsNaN(position.y) || float.IsInfinity(position.y))
            {
                bool changed = keys.Count != 0;
                keys.Clear();
                centerX = centerZ = -1;
                return changed;
            }

            const int mip = MeshWorldVirtualTexture.FirstSvtMip;
            int side = MeshWorldVirtualTexture.PagesPerAxis(mip);
            float pageSize = MeshWorldVirtualTexture.PageWorldSize(mip);
            int x = Sector(position.x, pageSize, side), z = Sector(position.y, pageSize, side);
            if (x == centerX && z == centerZ) return false;
            centerX = x;
            centerZ = z;

            keys.Clear();
            candidates.Clear();
            int reach = (int)Math.Ceiling(Radius / pageSize);
            for (int pz = Math.Max(0, z - reach); pz <= Math.Min(side - 1, z + reach); pz++)
            for (int px = Math.Max(0, x - reach); px <= Math.Min(side - 1, x + reach); px++)
            {
                int dx = px - x, dz = pz - z;
                // 包含接触圆形覆盖范围的整个页，避免圆周上的角落漏页。
                float nearestX = Math.Max(0f, Math.Abs(dx) - 0.5f) * pageSize;
                float nearestZ = Math.Max(0f, Math.Abs(dz) - 0.5f) * pageSize;
                if (nearestX * nearestX + nearestZ * nearestZ > Radius * Radius) continue;
                candidates.Add(new Candidate
                {
                    Key = MeshWorldVirtualTexture.EncodeKey(mip, px, pz),
                    DistanceSquared = dx * dx + dz * dz
                });
            }
            candidates.Sort(compareCandidates);
            for (int i = 0; i < candidates.Count && i < MaximumPages; i++) keys.Add(candidates[i].Key);
            return true;
        }

        static int Sector(float coordinate, float pageSize, int side) =>
            Math.Min(side - 1, (int)(Math.Max(0d, Math.Min(MeshHeightLayout.WorldSize, coordinate)) / pageSize));

        static int CompareCandidates(Candidate a, Candidate b)
        {
            int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
            return distance != 0 ? distance : a.Key.CompareTo(b.Key);
        }
    }
}
