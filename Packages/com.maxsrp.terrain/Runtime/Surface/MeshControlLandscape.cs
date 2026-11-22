using System;
using UnityEngine;

namespace MaxTerrain
{
    // Authored mountain/valley cover derived from the existing height source.
    // These rules are not a GIS reconstruction and never modify geometry.
    // Terrain features are authored at 8m, then expanded onto the 1m controls.
    public sealed class MeshControlLandscape : IDisposable
    {
        public const int SwatchOrigin = 128;
        public const int SwatchCellSize = 128;
        public const int SwatchCount = 4;
        public const int LandscapeSpacing = 8;
        const int Side = (MeshControlContract.WorldSize + LandscapeSpacing - 1) / LandscapeSpacing + 1;
        const int Cells = Side - 1;

        struct Face
        {
            public int id0, id1, id2;
            public float a, ax, az, b, bx, bz, c, cx, cz;
        }

        Face[] faces;

        public MeshControlLandscape(string heightDirectory)
        {
            var metadata = JsonUtility.FromJson<MeshHeightMetadata>(System.IO.File.ReadAllText(
                System.IO.Path.Combine(heightDirectory, MeshHeightLayout.MetadataName)));
            bool dem = metadata.recipe == "maladetas-dem-1m-v1";
            var heights = ReadHeights(heightDirectory);
            if (!dem) heights = SmoothFeatures(heights);
            var slope = new float[heights.Length];
            var hollow = new float[heights.Length];
            var drainage = new float[heights.Length];
            TerrainFeatures(heights, slope, hollow);
            Drainage(heights, drainage);

            var words = new ushort[heights.Length];
            var scores = new float[MeshControlContract.LegacyMaterialCount];
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    int wx = Math.Min(x * LandscapeSpacing, MeshControlContract.WorldSize);
                    int wz = Math.Min(z * LandscapeSpacing, MeshControlContract.WorldSize);
                    if (TrySwatch(wx, wz, out int id)) words[z * Side + x] = Pure(id);
                    else
                    {
                        int index = z * Side + x;
                        float bank = drainage[index];
                        for (int dz = -2; dz <= 2; ++dz)
                            for (int dx = -2; dx <= 2; ++dx)
                            {
                                int distance = Math.Max(Math.Abs(dx), Math.Abs(dz));
                                if (distance == 0) continue;
                                float reach = distance == 1 ? .52f : .18f;
                                bank = Math.Max(bank, drainage[At(x + dx, z + dz)] * reach);
                            }
                        words[index] = dem
                            ? DemCover(wx, wz, heights[index] + metadata.elevationOffset, slope[index], hollow[index], drainage[index], bank, scores)
                            : Cover(wx, wz, heights[index], slope[index], hollow[index], drainage[index], bank, scores);
                    }
                }

            // Keep every interpolation face within the same three-ID contract
            // as the final control data. Pure swatches cannot be removed by repair.
            MeshControlContract.Repair(words, Side, Side);
            faces = new Face[Cells * Cells * 2];
            for (int z = 0; z < Cells; ++z)
                for (int x = 0; x < Cells; ++x)
                {
                    int v = z * Side + x, f = (z * Cells + x) * 2;
                    faces[f] = MakeFace(words[v], words[v + 1], words[v + Side], false);
                    faces[f + 1] = MakeFace(words[v + Side + 1], words[v + Side], words[v + 1], true);
                }
        }

        public static Vector2 SwatchCenter(int id)
        {
            if (id < 0 || id >= MeshControlContract.LegacyMaterialCount) throw new ArgumentOutOfRangeException(nameof(id));
            return new Vector2(SwatchOrigin + (id % SwatchCount + .5f) * SwatchCellSize,
                SwatchOrigin + (id / SwatchCount + .5f) * SwatchCellSize);
        }

        public static bool TrySwatch(int worldX, int worldZ, out int id)
        {
            int x = worldX - SwatchOrigin, z = worldZ - SwatchOrigin;
            if (x >= 0 && z >= 0 && x < SwatchCellSize * SwatchCount && z < SwatchCellSize * SwatchCount)
            {
                id = x / SwatchCellSize + z / SwatchCellSize * SwatchCount;
                return true;
            }
            id = -1;
            return false;
        }

        public ushort Sample(int worldX, int worldZ)
        {
            if (faces == null) throw new ObjectDisposedException(nameof(MeshControlLandscape));
            if (TrySwatch(worldX, worldZ, out int id)) return Pure(id);
            worldX = Math.Max(0, Math.Min(MeshControlContract.WorldSize, worldX));
            worldZ = Math.Max(0, Math.Min(MeshControlContract.WorldSize, worldZ));
            int x = Math.Min(worldX / LandscapeSpacing, Cells - 1), z = Math.Min(worldZ / LandscapeSpacing, Cells - 1);
            float u = (worldX - x * LandscapeSpacing) * (1f / LandscapeSpacing);
            float v = (worldZ - z * LandscapeSpacing) * (1f / LandscapeSpacing);
            Face face = faces[(z * Cells + x) * 2 + (u + v <= 1 ? 0 : 1)];
            return Pack(face.id0, face.id1, face.id2,
                Math.Max(0, face.a + face.ax * u + face.az * v),
                Math.Max(0, face.b + face.bx * u + face.bz * v),
                Math.Max(0, face.c + face.cx * u + face.cz * v));
        }

        public void Dispose() { faces = null; }

        static ushort[] ReadHeightRectangle(string directory)
        {
            // The source is sampled at the actual feature lattice, without the
            // old hundreds-of-metres blur that erased small drainage features.
            const int level = 3;
            int width = (MeshControlContract.WorldSize >> level) + 1;
            var values = new ushort[width * width];
            using (var source = new MeshHeightSource(directory))
                source.CopyGridRectangle(level, 0, 0, width, width, values);
            return values;
        }

        static float[] ReadHeights(string directory)
        {
            ushort[] values = ReadHeightRectangle(directory);
            int width = (MeshControlContract.WorldSize >> 3) + 1;
            var metadata = JsonUtility.FromJson<MeshHeightMetadata>(System.IO.File.ReadAllText(
                System.IO.Path.Combine(directory, MeshHeightLayout.MetadataName)));
            var result = new float[Side * Side];
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    int sx = Math.Min(x, width - 1), sz = Math.Min(z, width - 1);
                    result[z * Side + x] = values[sz * width + sx] * (metadata.heightScale / 65535f);
                }
            return result;
        }

        static int At(int x, int z) { return Math.Max(0, Math.Min(Side - 1, z)) * Side + Math.Max(0, Math.Min(Side - 1, x)); }

        static float[] SmoothFeatures(float[] source)
        {
            // Only the offline cover rules use this filtered feature field.
            // Use 96m tap spacing to suppress both the synthetic 50m relief and
            // 300m sinusoidal details in cover rules, retaining broad landforms.
            // This feature filter never changes the rendered height source.
            int[] kernel = { 1, 8, 28, 56, 70, 56, 28, 8, 1 };
            var horizontal = new float[source.Length];
            var result = new float[source.Length];
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    float value = 0;
                    for (int k = -4; k <= 4; ++k) value += source[At(x + k * 3, z)] * kernel[k + 4];
                    horizontal[z * Side + x] = value * (1f / 256f);
                }
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    float value = 0;
                    for (int k = -4; k <= 4; ++k) value += horizontal[At(x, z + k * 3)] * kernel[k + 4];
                    result[z * Side + x] = value * (1f / 256f);
                }
            return result;
        }

        static void TerrainFeatures(float[] height, float[] slope, float[] hollow)
        {
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    int index = z * Side + x;
                    float dx = (height[At(x + 1, z)] - height[At(x - 1, z)]) / (x == 0 || x == Side - 1 ? LandscapeSpacing : 2f * LandscapeSpacing);
                    float dz = (height[At(x, z + 1)] - height[At(x, z - 1)]) / (z == 0 || z == Side - 1 ? LandscapeSpacing : 2f * LandscapeSpacing);
                    slope[index] = Mathf.Sqrt(dx * dx + dz * dz);
                    float neighbourhood = 0;
                    for (int oz = -3; oz <= 3; oz += 3)
                        for (int ox = -3; ox <= 3; ox += 3)
                            neighbourhood += height[At(x + ox, z + oz)];
                    hollow[index] = (neighbourhood / 9 - height[index]) / (LandscapeSpacing * .5f);
                }
        }

        static void Drainage(float[] height, float[] channel)
        {
            var order = new int[height.Length];
            var downstream = new int[height.Length];
            var catchment = new float[height.Length];
            for (int z = 0; z < Side; ++z)
                for (int x = 0; x < Side; ++x)
                {
                    int index = z * Side + x, next = -1;
                    float steepest = 0;
                    for (int dz = -1; dz <= 1; ++dz)
                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            if (dx == 0 && dz == 0 || x + dx < 0 || x + dx >= Side || z + dz < 0 || z + dz >= Side) continue;
                            int other = (z + dz) * Side + x + dx;
                            float drop = (height[index] - height[other]) * (dx != 0 && dz != 0 ? .70710678f : 1f);
                            if (drop > steepest) { steepest = drop; next = other; }
                        }
                    order[index] = index; downstream[index] = next; catchment[index] = 1;
                }
            Array.Sort(order, (a, b) => height[b].CompareTo(height[a]));
            foreach (int index in order)
                if (downstream[index] >= 0) catchment[downstream[index]] += catchment[index];
            for (int i = 0; i < channel.Length; ++i)
                channel[i] = Smooth(5.5f, 9.5f, Mathf.Log(catchment[i], 2));
        }

        static ushort Cover(int x, int z, float height, float slope, float hollow, float stream, float bank, float[] score)
        {
            Array.Clear(score, 0, score.Length);
            float climate = Noise(x, z, 1f / 1800, 71);
            float geology = Noise(x, z, 1f / 900, 193);
            float patch = Noise(x, z, 1f / 320, 443);
            float dry = Mathf.Clamp01(.25f + climate * .65f + Smooth(280, 600, height) * .3f - hollow * .25f - bank * .5f);
            float rock = Mathf.Clamp01(Smooth(.2f, .62f, slope) + Smooth(450, 750, height) * .55f);
            float talus = Smooth(.1f, .24f, slope) * (1 - Smooth(.3f, .55f, slope)) * Smooth(-.1f, .8f, hollow);
            float lowland = (1 - Smooth(170, 330, height)) * (1 - Smooth(.12f, .28f, slope));
            float forest = (1 - dry) * Smooth(.25f, .72f, climate) * (1 - rock);
            float vegetation = (1 - rock) * (1 - bank * .8f) * (1 - talus * .5f);

            // Open meadow, leafy woodland, dry grass and forest litter.
            score[0] = vegetation * (1 - dry) * (1 - forest) * (1.2f - patch * .4f);
            score[1] = vegetation * (1 - dry) * (.3f + patch * .8f);
            score[2] = vegetation * dry * (.65f + patch * .5f);
            score[3] = vegetation * forest * 2.2f;
            score[4] = (1 - rock) * dry * (.15f + geology * .35f) + talus * .15f;

            // Damp low ground and floodplain deposits; no water mesh is implied.
            score[5] = lowland * dry * (.2f + bank * .4f);
            score[6] = lowland * (1 - dry) * (.1f + bank * 1.5f);
            score[7] = stream * (1 - Smooth(.08f, .24f, slope)) * (1 - dry) * 2.6f;

            // Exposed rock follows slope/elevation, scree collects below it.
            score[8] = rock * Smooth(.35f, .75f, slope) * 3;
            score[9] = rock * (.45f + geology) * (1 - Smooth(.45f, .8f, slope)) * 1.7f;
            score[10] = rock * (1.2f - geology) * (1 - Smooth(.5f, .8f, slope));
            score[11] = talus * (.5f + geology) * 1.9f;
            score[12] = stream * (.4f + Smooth(.03f, .2f, slope)) * (1.3f - geology) * 1.8f;
            score[13] = stream * (.4f + Smooth(.02f, .16f, slope)) * (.4f + geology) * 1.5f;
            score[14] = bank * lowland * dry * (.25f + patch) * 1.4f;
            score[15] = bank * lowland * (1 - dry) * (.35f + geology) * 1.4f;

            int primary = 0;
            for (int id = 1; id < score.Length; ++id)
                if (Better(id, score[id], primary, score[primary])) primary = id;

            // Paint a meaningful local pair rather than ranking two unrelated
            // biome scores. This is authored coverage, not shader compensation.
            int secondary;
            switch (primary)
            {
                case 0: secondary = dry > .55f ? 4 : 5; break; // Meadow / soil.
                case 1: secondary = 3; break;                // Leafy grass / forest ground.
                case 2: secondary = patch > .5f ? 5 : 3; break;
                case 3: secondary = climate > .5f ? 1 : 0; break;
                case 4:
                case 5: secondary = 2; break;                // Dry soil / withered grass.
                case 6: secondary = 15; break;               // Wet mud / damp sand.
                case 7: secondary = geology > .5f ? 12 : 15; break;
                case 8:
                case 9: secondary = 10; break;               // Exposed rock / broken rock.
                case 10: secondary = slope > .32f ? 9 : 11; break;
                case 11: secondary = 10; break;
                case 12:
                case 13: secondary = bank > .5f ? 6 : 15; break;
                case 14: secondary = 13; break;              // Dry sand / river pebbles.
                case 15: secondary = 6; break;
                default: throw new InvalidOperationException("Unknown landscape material.");
            }
            float secondaryCoverage = Mathf.Lerp(.35f, .45f,
                Mathf.Clamp01(.65f * patch + .35f * (1 - dry)));
            // 上面的score/primary/secondary沿用原始素材编号，便于核对Natural16名称。
            // 编码前先转换为有意安排的覆盖ID；运行时ID就是重排后数组的slice。
            // 例如源草0→全局草14，源泥5→全局泥9：必须Bottom=9、Top=14。
            // Pack会随Bottom/Top身份选择正确的Top覆盖率，不能只换ID而沿用旧泥覆盖率。
            primary = MeshMaterialLayerOrder.OrderedId(primary);
            secondary = MeshMaterialLayerOrder.OrderedId(secondary);
            return Pack(primary, secondary, primary, 1 - secondaryCoverage, secondaryCoverage, 0);
        }

        // Authored snow-free alpine cover, NOT measured land-cover classes.
        // Heights are real DEM data; these continuous masks are an explicitly
        // procedural interpretation using the existing ordered material IDs.
        static ushort DemCover(int x, int z, float elevation, float slope, float hollow, float stream, float bank, float[] score)
        {
            Array.Clear(score, 0, score.Length);
            float geology = Noise(x, z, 1f/900, 193);
            float patch = Noise(x, z, 1f/90, 443);
            float dry = Noise(x, z, 1f/600, 71);
            float steep = Smooth(.35f, .95f, slope);
            float alpine = Smooth(2350, 3000, elevation);
            float hollowMask = Smooth(-.3f, 1.1f, hollow);
            float scree = Smooth(.12f, .4f, slope) * (1-Smooth(.75f,1.2f,slope)) * (.3f+.7f*hollowMask);
            float meadow = (1-Smooth(.28f,.75f,slope)) * (1-Smooth(2250,2850,elevation));
            score[0] = steep * (1.2f+.5f*geology);
            score[1] = (.08f+.7f*alpine+.35f*steep) * (.7f+.6f*geology);
            score[2] = scree * (.5f+.7f*patch) + alpine * (1-steep) * .3f;
            score[3] = scree * (.3f+.6f*(1-patch));
            score[4] = stream * (1-steep) * .6f;
            score[5] = stream * (1-steep) * (.25f+.4f*dry);
            score[8] = meadow * (.35f+.3f*dry);
            score[9] = bank * meadow * (.2f+.4f*hollowMask);
            score[13] = meadow * (.2f+.9f*dry);
            score[14] = meadow * (.5f+.8f*(1-dry));
            score[15] = meadow * (1-Smooth(1800,2200,elevation)) * (1-dry) * .4f;
            int a=0,b=1;
            if (Better(b,score[b],a,score[a])) Swap(ref a,ref b);
            for(int id=2;id<score.Length;++id)
                if(Better(id,score[id],a,score[a])) { b=a;a=id; }
                else if(Better(id,score[id],b,score[b])) b=id;
            float second=score[b] < score[a]*.08f ? 0 : score[b];
            return Pack(a,b,a,score[a],second,0);
        }

        static Face MakeFace(ushort p0, ushort p1, ushort p2, bool upper)
        {
            int mask = Mask(p0) | Mask(p1) | Mask(p2), count = 0, a = 0, b = 0, c = 0;
            for (int id = 0; id < MeshControlContract.LegacyMaterialCount; ++id)
                if ((mask & 1 << id) != 0)
                {
                    if (count == 0) a = id;
                    else if (count == 1) b = id;
                    else if (count == 2) c = id;
                    else throw new InvalidOperationException("Landscape interpolation face exceeds three materials after repair.");
                    ++count;
                }
            var face = new Face { id0 = a, id1 = count > 1 ? b : a, id2 = count > 2 ? c : a };
            Plane(Weight(p0, a), Weight(p1, a), Weight(p2, a), upper, out face.a, out face.ax, out face.az);
            if (count > 1) Plane(Weight(p0, b), Weight(p1, b), Weight(p2, b), upper, out face.b, out face.bx, out face.bz);
            if (count > 2) Plane(Weight(p0, c), Weight(p1, c), Weight(p2, c), upper, out face.c, out face.cx, out face.cz);
            return face;
        }

        static int Mask(ushort word) { return 1 << (word & 15) | 1 << ((word >> 4) & 15); }
        static float Weight(ushort word, int id)
        {
            int bottom = word & 15, top = (word >> 4) & 15;
            float opacity = (word >> 8) * (1f / 255f);
            return (bottom == id ? 1 - opacity : 0) + (top == id ? opacity : 0);
        }
        static void Plane(float a, float b, float c, bool upper, out float value, out float dx, out float dz)
        {
            if (upper) { value = -a + b + c; dx = a - b; dz = a - c; }
            else { value = a; dx = b - a; dz = c - a; }
        }

        static ushort Pack(int a, int b, int c, float wa, float wb, float wc)
        {
            if (Better(b, wb, a, wa)) { Swap(ref a, ref b); Swap(ref wa, ref wb); }
            if (Better(c, wc, b, wb)) { Swap(ref b, ref c); Swap(ref wb, ref wc); }
            if (Better(b, wb, a, wa)) { Swap(ref a, ref b); Swap(ref wa, ref wb); }
            if (wb <= 0 || a == b) return Pure(a);
            // a/b已经是按覆盖关系安排的全局ID，min/max此时才有Bottom/Top含义。
            // wa/wb描述各自材质的覆盖量。Top是谁，就取谁的覆盖量再归一化、量化。
            // 草土组合必为土在下、草在上；与两张Height图谁的灰度更大无关。
            int bottom = Math.Min(a, b), top = Math.Max(a, b);
            float opacity = (top == a ? wa : wb) / (wa + wb);
            int code = Math.Min(255, Math.Max(0, (int)(opacity * 255 + .5f)));
            if (code == 0) return Pure(bottom);
            if (code == 255) return Pure(top);
            return (ushort)(bottom | top << 4 | code << 8);
        }

        static ushort Pure(int id) { return (ushort)(id | id << 4); }
        static bool Better(int a, float wa, int b, float wb) { return wa > wb || wa == wb && a < b; }
        static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }
        static void Swap(ref float a, ref float b) { float t = a; a = b; b = t; }
        static float Smooth(float low, float high, float value)
        { float t = Mathf.Clamp01((value - low) / (high - low)); return t * t * (3 - 2 * t); }
        static float Noise(int x, int z, float frequency, int seed)
        {
            float gx = x * frequency, gz = z * frequency;
            int ix = Mathf.FloorToInt(gx), iz = Mathf.FloorToInt(gz);
            float u = Smooth(0, 1, gx - ix), v = Smooth(0, 1, gz - iz);
            return Mathf.Lerp(Mathf.Lerp(Hash(ix, iz, seed), Hash(ix + 1, iz, seed), u),
                Mathf.Lerp(Hash(ix, iz + 1, seed), Hash(ix + 1, iz + 1, seed), u), v);
        }
        static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(seed * 83492791);
                h ^= h >> 13; h *= 0x85ebca6bu; h ^= h >> 16;
                return (h & 65535) * (1f / 65535f);
            }
        }
    }
}
