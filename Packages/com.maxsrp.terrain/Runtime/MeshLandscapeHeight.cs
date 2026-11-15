using System;

namespace MaxTerrain
{
    // Offline authored terrain, not measured GIS. A few broad, asymmetric
    // landforms and a continuous curved valley replace the old periodic relief.
    // The 16m construction field is interpolated to the common quantized 1m
    // lattice; every stored height level still samples that SAME lattice.
    public sealed class MeshLandscapeHeight : IDisposable
    {
        public const string Recipe = "smooth-curved-valley-v2";
        public const int ConstructionSpacing = 16;
        const int Cells = MeshHeightLayout.WorldSize / ConstructionSpacing;
        const int NodeSide = Cells + 3; // One ghost node before and after the world.
        float[] coefficients;

        public MeshLandscapeHeight()
        {
            var nodes = new float[NodeSide * NodeSide];
            for (int z = 0; z < NodeSide; ++z)
                for (int x = 0; x < NodeSide; ++x)
                    nodes[z * NodeSide + x] = (float)Landform((x - 1) * ConstructionSpacing,
                        (z - 1) * ConstructionSpacing);

            coefficients = new float[Cells * Cells * 16];
            var rows = new float[16];
            for (int z = 0; z < Cells; ++z)
                for (int x = 0; x < Cells; ++x)
                {
                    for (int row = 0; row < 4; ++row)
                    {
                        int source = (z + row) * NodeSide + x, r = row * 4;
                        Fit(nodes[source], nodes[source + 1], nodes[source + 2], nodes[source + 3],
                            out rows[r], out rows[r + 1], out rows[r + 2], out rows[r + 3]);
                    }
                    int target = (z * Cells + x) * 16;
                    for (int power = 0; power < 4; ++power)
                        Fit(rows[power], rows[4 + power], rows[8 + power], rows[12 + power],
                            out coefficients[target + power], out coefficients[target + 4 + power],
                            out coefficients[target + 8 + power], out coefficients[target + 12 + power]);
                }
        }

        public ushort Sample(int worldX, int worldZ)
        {
            if (coefficients == null) throw new ObjectDisposedException(nameof(MeshLandscapeHeight));
            int x = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, worldX));
            int z = Math.Max(0, Math.Min(MeshHeightLayout.WorldSize, worldZ));
            int cx = Math.Min(x / ConstructionSpacing, Cells - 1), cz = Math.Min(z / ConstructionSpacing, Cells - 1);
            float u = (x - cx * ConstructionSpacing) * (1f / ConstructionSpacing);
            float v = (z - cz * ConstructionSpacing) * (1f / ConstructionSpacing);
            int i = (cz * Cells + cx) * 16;
            float r0 = ((coefficients[i + 3] * u + coefficients[i + 2]) * u + coefficients[i + 1]) * u + coefficients[i];
            float r1 = ((coefficients[i + 7] * u + coefficients[i + 6]) * u + coefficients[i + 5]) * u + coefficients[i + 4];
            float r2 = ((coefficients[i + 11] * u + coefficients[i + 10]) * u + coefficients[i + 9]) * u + coefficients[i + 8];
            float r3 = ((coefficients[i + 15] * u + coefficients[i + 14]) * u + coefficients[i + 13]) * u + coefficients[i + 12];
            float height = ((r3 * v + r2) * v + r1) * v + r0;
            return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(height * (65535.0 / MeshHeightLayout.HeightScale))));
        }

        public void Dispose() { coefficients = null; }

        static void Fit(float p0, float p1, float p2, float p3, out float c0, out float c1, out float c2, out float c3)
        {
            c0 = p1;
            c1 = (p2 - p0) * .5f;
            c2 = p0 - 2.5f * p1 + 2f * p2 - .5f * p3;
            c3 = -.5f * p0 + 1.5f * p1 - 1.5f * p2 + .5f * p3;
        }

        static double Landform(double x, double z)
        {
            // Broad domain warping breaks alignment without adding small hills.
            double wx = x + 180 * Noise(x, z, 2600, 17);
            double wz = z + 140 * Noise(x + 700, z - 400, 2300, 29);
            double height = 100 + .004 * z + 38 * Noise(wx, wz, 1900, 43);

            // Western foothills and one quieter northern shoulder.
            height += 175 * Hill(wx, wz, 2500, 3150, 1450, 2300, .939372713, -.342897807);
            height += 85 * Hill(wx, wz, 1750, 7600, 1900, 1350, .852524522, .522687229);

            // One irregular eastern mountain spine, with two unequal spurs.
            double spine = 7150 + 360 * Noise(8400, wz, 2800, 61) + 130 * Noise(9600, wz, 1500, 73);
            double crest = 490 + 95 * Noise(2800, wz, 1800, 89);
            double width = 1020 + 110 * Noise(5100, wz, 2300, 101);
            height += crest * Gaussian((wx - spine) / width) * Gaussian((wz - 6400) / 3600);
            height += 245 * Hill(wx, wz, 8350, 4100, 1050, 1650, .939372713, -.342897807);
            height += 175 * Hill(wx, wz, 6100, 8750, 1450, 1050, .955336489, .295520207);

            // Nonperiodic, gentle relief only. No 50m or 300m sinusoidal layer.
            height += 12 * Noise(wx, wz, 850, 127) + 3 * Noise(wx, wz, 430, 151);

            // A wide curved drainage corridor. Its floor descends to the south;
            // it is part of the height field, not just a painted river stripe.
            double river = 4750 + 420 * Noise(1234, z, 3400, 173) + 165 * Noise(6400, z + 600, 1600, 197);
            double valleyWidth = 330 + 45 * Noise(3100, z, 2200, 211);
            double floor = 70 + .004 * z + 2 * Noise(4400, z, 2200, 229);
            double valley = Gaussian((x - river) / valleyWidth);
            return height + (floor - height) * valley;
        }

        static double Hill(double x, double z, double centerX, double centerZ,
            double across, double along, double cosine, double sine)
        {
            double dx = x - centerX, dz = z - centerZ;
            double u = (dx * cosine + dz * sine) / across;
            double v = (-dx * sine + dz * cosine) / along;
            return Math.Exp(-u * u - v * v);
        }

        static double Gaussian(double value) { return Math.Exp(-value * value); }

        // Hashed 2D gradient noise with quintic interpolation. No repeating
        // permutation table, and no zero-gradient grid lines from value noise.
        static double Noise(double x, double z, double scale, int seed)
        {
            double gx = x / scale, gz = z / scale;
            int ix = (int)Math.Floor(gx), iz = (int)Math.Floor(gz);
            double u = gx - ix, v = gz - iz, fu = Fade(u), fv = Fade(v);
            double a = Gradient(Hash(ix, iz, seed), u, v);
            double b = Gradient(Hash(ix + 1, iz, seed), u - 1, v);
            double c = Gradient(Hash(ix, iz + 1, seed), u, v - 1);
            double d = Gradient(Hash(ix + 1, iz + 1, seed), u - 1, v - 1);
            double low = a + (b - a) * fu, high = c + (d - c) * fu;
            return (low + (high - low) * fv) * 1.4142135623730951;
        }

        static double Fade(double t) { return t * t * t * (t * (t * 6 - 15) + 10); }

        static double Gradient(uint hash, double x, double z)
        {
            const double diagonal = .7071067811865476;
            switch (hash & 7u)
            {
                case 0: return x;
                case 1: return -x;
                case 2: return z;
                case 3: return -z;
                case 4: return (x + z) * diagonal;
                case 5: return (x - z) * diagonal;
                case 6: return (-x + z) * diagonal;
                default: return (-x - z) * diagonal;
            }
        }

        static uint Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint value = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(seed * 83492791);
                value ^= value >> 13; value *= 0x85ebca6bu; value ^= value >> 16;
                return value;
            }
        }
    }
}
