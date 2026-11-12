using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace MaxSRP.EditorTools
{
    // Pure CPU preparation. No Unity objects, asset refresh, GPU work or scene state
    // are accessed here; the caller can run Build on a worker after extracting meshes.
    internal static class MaxSkyVisibilitySurfaceSampling
    {
        internal struct InputTriangle
        {
            public Vector3 P0, P1, P2, Normal;
            public Vector2 Uv0, Uv1, Uv2, Atlas0, Atlas1, Atlas2;
            public int Renderer, Material, SourceTriangle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Texel
        {
            public uint Pixel, Chart, OriginStart, OriginCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Origin
        {
            public Vector3 Position;
            public uint Material;
            public Vector3 Normal;
            public uint SourceTriangle;
            public Vector2 UV;
        }

        // StructuredBuffer layout: 16 bytes, in the same order as Result.Texels.
        [StructLayout(LayoutKind.Sequential)]
        internal struct FragmentRange
        {
            public uint FirstFragment, FragmentCount;
            public float TotalUvArea, Reserved;
        }

        // StructuredBuffer layout: 88 bytes. These are actual clipped fan
        // triangles, not a cache of a fixed number of random path origins.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Fragment
        {
            public Vector3 P0;
            public uint Material;
            public Vector3 P1;
            public uint SourceTriangle;
            public Vector3 P2;
            public float Cdf;
            public Vector3 Normal;
            public float UvArea;
            public Vector2 UV0, UV1, UV2;
        }

        [Serializable]
        internal sealed class Diagnostics
        {
            public int InputTriangles, Charts, DegenerateUvTriangles, DegenerateWorldTriangles;
            public int CoveredTexels, PaddingTexels, CoveredChartConflicts, PaddingChartConflicts;
            public int ValidCovered, ConflictingFootprints;
            public int OvercoveredTexels, Origins, OriginsPerTexel;
            public int Fragments, MaxFragmentsPerTexel;
            public long FragmentBytes, FragmentRangeBytes;
            public double CoveredAreaInTexels;
            public double MaxFragmentAreaRelativeError;
            public readonly List<int> ConflictPixelExamples = new List<int>();
        }

        internal sealed class Result
        {
            public Texel[] Texels;
            public Origin[] Origins;
            public Fragment[] Fragments;
            public FragmentRange[] FragmentRanges;
            // 0: no owner, >0: chart ID, -1: competing charts. Real covered
            // texels retain their owner when another chart requests padding.
            public int[] ChartOwners;
            // 0: empty, 1: real triangle coverage, 2: owned padding,
            // 3: covered cross-chart conflict, 4: padding conflict.
            public byte[] Coverage;
            public Diagnostics Stats;
        }

        private struct Point
        {
            public double X, Y;
            public Point(double x, double y) { X = x; Y = y; }
        }

        private struct PixelData
        {
            public int Pixel, Chart, OriginStart;
            public int FragmentStart, FragmentCount, FragmentsWritten;
            public double Area;
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            public Vector3 P;
            public Vector2 UV;
            public bool Equals(VertexKey other) => P.Equals(other.P) && UV.Equals(other.UV);
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode() => unchecked(P.GetHashCode() * 397 ^ UV.GetHashCode());
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public VertexKey A, B;
            public int Renderer;
            public bool Equals(EdgeKey other) => Renderer == other.Renderer &&
                ((A.Equals(other.A) && B.Equals(other.B)) || (A.Equals(other.B) && B.Equals(other.A)));
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((A.GetHashCode() ^ B.GetHashCode()) * 397 ^ Renderer);
        }

        internal static Result Build(IReadOnlyList<InputTriangle> triangles, int size,
            int originsPerTexel = 4, int padding = 2, CancellationToken cancellationToken = default)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (size < 1 || size > 16384) throw new ArgumentOutOfRangeException(nameof(size));
            if (originsPerTexel < 1 || originsPerTexel > 16) throw new ArgumentOutOfRangeException(nameof(originsPerTexel));
            if (padding < 0 || padding > 16) throw new ArgumentOutOfRangeException(nameof(padding));
            var stats = new Diagnostics { InputTriangles = triangles.Count, OriginsPerTexel = originsPerTexel };
            int[] charts = BuildCharts(triangles, cancellationToken, out int chartCount);
            stats.Charts = chartCount;
            int count = checked(size * size);
            var result = new Result { ChartOwners = new int[count], Coverage = new byte[count], Stats = stats };
            // Zero means no record. This dense indirection keeps per-pixel heap
            // objects out of the 4096-square production atlas.
            var recordAt = new int[count];
            var pixels = new List<PixelData>();
            var origins = new List<Origin>();
            var polygonA = new Point[10];
            var polygonB = new Point[10];

            for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InputTriangle triangle = triangles[triangleIndex];
                if (!Finite(triangle.P0) || !Finite(triangle.P1) || !Finite(triangle.P2) ||
                    !Finite(triangle.Atlas0) || !Finite(triangle.Atlas1) || !Finite(triangle.Atlas2) ||
                    !Finite(triangle.Uv0) || !Finite(triangle.Uv1) || !Finite(triangle.Uv2))
                    throw new InvalidOperationException("Non-finite sky-visibility receiving triangle " + triangleIndex);
                Vector3 geometric = Vector3.Cross(triangle.P1 - triangle.P0, triangle.P2 - triangle.P0);
                if (geometric.sqrMagnitude <= 1e-20f) { ++stats.DegenerateWorldTriangles; continue; }
                geometric.Normalize();
                // A supplied inverse-transpose normal only decides which side is
                // the receiving side. Smooth/normal-map normals never replace Ng.
                if (Finite(triangle.Normal) && Vector3.Dot(geometric, triangle.Normal) < 0) geometric = -geometric;
                Point a = AtlasPoint(triangle.Atlas0, size), b = AtlasPoint(triangle.Atlas1, size), c = AtlasPoint(triangle.Atlas2, size);
                double signedArea2 = Cross(Subtract(b, a), Subtract(c, a));
                if (Math.Abs(signedArea2) < 1e-18) { ++stats.DegenerateUvTriangles; continue; }
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
                int maxX = Math.Min(size - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
                int maxY = Math.Min(size - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                int chart = charts[triangleIndex];
                for (int y = minY; y <= maxY; ++y)
                {
                    if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    for (int x = minX; x <= maxX; ++x)
                    {
                        polygonA[0] = a; polygonA[1] = b; polygonA[2] = c;
                        int n = Clip(polygonA, 3, polygonB, 0, x, true);
                        n = Clip(polygonB, n, polygonA, 0, x + 1, false);
                        n = Clip(polygonA, n, polygonB, 1, y, true);
                        n = Clip(polygonB, n, polygonA, 1, y + 1, false);
                        double area = PolygonArea(polygonA, n);
                        if (area <= 1e-14) continue;
                        int pixel = x + y * size;
                        int record = recordAt[pixel] - 1;
                        if (record < 0)
                        {
                            record = pixels.Count;
                            recordAt[pixel] = record + 1;
                            result.ChartOwners[pixel] = chart;
                            result.Coverage[pixel] = 1;
                            pixels.Add(new PixelData { Pixel = pixel, Chart = chart, OriginStart = origins.Count });
                            for (int k = 0; k < originsPerTexel; ++k) origins.Add(default);
                        }
                        PixelData data = pixels[record];
                        if (data.Chart != chart)
                        {
                            if (result.Coverage[pixel] != 3)
                            {
                                ++stats.CoveredChartConflicts;
                                if (stats.ConflictPixelExamples.Count < 32) stats.ConflictPixelExamples.Add(pixel);
                            }
                            result.ChartOwners[pixel] = -1;
                            result.Coverage[pixel] = 3;
                            continue;
                        }
                        double totalArea = data.Area + area;
                        for (int fan = 1; fan + 1 < n; ++fan)
                            if (FanArea(polygonA, fan) > 0) data.FragmentCount = checked(data.FragmentCount + 1);
                        // Each slot is an independent area-weighted reservoir.
                        // Thus arbitrarily small intersecting charts still receive
                        // valid interior starts, and a texel crossing several mesh
                        // triangles has at most 16 starts with equal final weights.
                        for (int k = 0; k < originsPerTexel; ++k)
                        {
                            uint seed = Mix(unchecked((uint)pixel * 0x9e3779b9u ^ (uint)triangleIndex * 0x85ebca6bu ^ (uint)k * 0xc2b2ae35u));
                            if (data.Area > 0 && Random01(ref seed) >= area / totalArea) continue;
                            Point point = SamplePolygon(polygonA, n, ref seed);
                            double u = Cross(Subtract(point, a), Subtract(c, a)) / signedArea2;
                            double v = Cross(Subtract(b, a), Subtract(point, a)) / signedArea2;
                            float fu = (float)u, fv = (float)v;
                            origins[data.OriginStart + k] = new Origin
                            {
                                Position = triangle.P0 + (triangle.P1 - triangle.P0) * fu + (triangle.P2 - triangle.P0) * fv,
                                Normal = geometric, Material = checked((uint)triangle.Material),
                                SourceTriangle = unchecked((uint)triangle.SourceTriangle),
                                UV = triangle.Uv0 + (triangle.Uv1 - triangle.Uv0) * fu + (triangle.Uv2 - triangle.Uv0) * fv
                            };
                        }
                        data.Area = totalArea;
                        pixels[record] = data;
                    }
                }
            }

            var texels = new List<Texel>(pixels.Count);
            foreach (PixelData data in pixels)
            {
                stats.CoveredAreaInTexels += data.Area;
                if (data.Area > 1.00001) ++stats.OvercoveredTexels;
                if (result.Coverage[data.Pixel] == 3) continue;
                texels.Add(new Texel { Pixel = (uint)data.Pixel, Chart = (uint)data.Chart,
                    OriginStart = (uint)data.OriginStart, OriginCount = (uint)originsPerTexel });
            }
            stats.CoveredTexels = texels.Count;
            stats.ValidCovered = texels.Count;
            stats.Origins = checked(texels.Count * originsPerTexel);
            PrepareFragments(result, triangles, charts, size, recordAt, pixels, texels, cancellationToken);
            AssignPadding(result, triangles, charts, size, padding, cancellationToken);
            stats.ConflictingFootprints = stats.CoveredChartConflicts + stats.PaddingChartConflicts;
            result.Texels = texels.ToArray();
            result.Origins = origins.ToArray();
            return result;
        }

        private static void PrepareFragments(Result result, IReadOnlyList<InputTriangle> triangles,
            int[] charts, int size, int[] recordAt, List<PixelData> pixels, List<Texel> texels, CancellationToken token)
        {
            // Count/prefix/fill avoids retaining a second full copy of the fan
            // geometry while grouping it by texel. Storage scales with geometric
            // intersections, independently of the requested path sample count.
            var ranges = new FragmentRange[texels.Count];
            int totalFragments = 0;
            for (int i = 0; i < texels.Count; ++i)
            {
                int record = recordAt[(int)texels[i].Pixel] - 1;
                PixelData data = pixels[record];
                if (data.FragmentCount == 0) throw new InvalidOperationException("Covered sky texel has no clipped fragments: " + data.Pixel);
                data.FragmentStart = totalFragments;
                totalFragments = checked(totalFragments + data.FragmentCount);
                pixels[record] = data;
                ranges[i] = new FragmentRange { FirstFragment = (uint)data.FragmentStart, FragmentCount = (uint)data.FragmentCount };
                result.Stats.MaxFragmentsPerTexel = Math.Max(result.Stats.MaxFragmentsPerTexel, data.FragmentCount);
            }
            var fragments = new Fragment[totalFragments];
            var polygonA = new Point[10];
            var polygonB = new Point[10];
            for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
            {
                token.ThrowIfCancellationRequested();
                InputTriangle triangle = triangles[triangleIndex];
                Vector3 geometric = Vector3.Cross(triangle.P1 - triangle.P0, triangle.P2 - triangle.P0);
                if (geometric.sqrMagnitude <= 1e-20f) continue;
                geometric.Normalize();
                if (Finite(triangle.Normal) && Vector3.Dot(geometric, triangle.Normal) < 0) geometric = -geometric;
                Point a = AtlasPoint(triangle.Atlas0, size), b = AtlasPoint(triangle.Atlas1, size), c = AtlasPoint(triangle.Atlas2, size);
                double signedArea2 = Cross(Subtract(b, a), Subtract(c, a));
                if (Math.Abs(signedArea2) < 1e-18) continue;
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
                int maxX = Math.Min(size - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
                int maxY = Math.Min(size - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                for (int y = minY; y <= maxY; ++y)
                {
                    if ((y & 31) == 0) token.ThrowIfCancellationRequested();
                    for (int x = minX; x <= maxX; ++x)
                    {
                        int pixel = x + y * size;
                        int record = recordAt[pixel] - 1;
                        if (record < 0 || result.Coverage[pixel] == 3) continue;
                        PixelData data = pixels[record];
                        if (data.Chart != charts[triangleIndex]) continue;
                        polygonA[0] = a; polygonA[1] = b; polygonA[2] = c;
                        int n = Clip(polygonA, 3, polygonB, 0, x, true);
                        n = Clip(polygonB, n, polygonA, 0, x + 1, false);
                        n = Clip(polygonA, n, polygonB, 1, y, true);
                        n = Clip(polygonB, n, polygonA, 1, y + 1, false);
                        if (PolygonArea(polygonA, n) <= 1e-14) continue;
                        for (int fan = 1; fan + 1 < n; ++fan)
                        {
                            double area = FanArea(polygonA, fan);
                            if (area <= 0) continue;
                            if (data.FragmentsWritten >= data.FragmentCount)
                                throw new InvalidOperationException("Sky fragment counting/fill disagreement at " + pixel);
                            EvaluatePoint(triangle, polygonA[0], a, b, c, signedArea2, out Vector3 p0, out Vector2 uv0);
                            EvaluatePoint(triangle, polygonA[fan], a, b, c, signedArea2, out Vector3 p1, out Vector2 uv1);
                            EvaluatePoint(triangle, polygonA[fan + 1], a, b, c, signedArea2, out Vector3 p2, out Vector2 uv2);
                            fragments[data.FragmentStart + data.FragmentsWritten++] = new Fragment
                            {
                                P0 = p0, P1 = p1, P2 = p2, Normal = geometric,
                                Material = checked((uint)triangle.Material), SourceTriangle = unchecked((uint)triangle.SourceTriangle),
                                UvArea = (float)area, UV0 = uv0, UV1 = uv1, UV2 = uv2
                            };
                        }
                        pixels[record] = data;
                    }
                }
            }
            for (int i = 0; i < texels.Count; ++i)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                FragmentRange range = ranges[i];
                PixelData data = pixels[recordAt[(int)texels[i].Pixel] - 1];
                if (data.FragmentsWritten != data.FragmentCount)
                    throw new InvalidOperationException("Sky fragment fill is incomplete at " + data.Pixel);
                int first = (int)range.FirstFragment, end = checked(first + (int)range.FragmentCount);
                double totalArea = 0;
                for (int j = first; j < end; ++j) totalArea += fragments[j].UvArea;
                if (!(totalArea > 0) || double.IsNaN(totalArea) || double.IsInfinity(totalArea))
                    throw new InvalidOperationException("Invalid sky fragment area at " + data.Pixel);
                double cumulative = 0;
                for (int j = first; j < end; ++j)
                {
                    Fragment fragment = fragments[j];
                    cumulative += fragment.UvArea;
                    fragment.Cdf = j + 1 == end ? 1.0f : (float)(cumulative / totalArea);
                    fragments[j] = fragment;
                }
                range.TotalUvArea = (float)totalArea;
                ranges[i] = range;
                result.Stats.MaxFragmentAreaRelativeError = Math.Max(result.Stats.MaxFragmentAreaRelativeError,
                    Math.Abs(totalArea - data.Area) / data.Area);
            }
            result.FragmentRanges = ranges;
            result.Fragments = fragments;
            result.Stats.Fragments = totalFragments;
            result.Stats.FragmentBytes = (long)totalFragments * Marshal.SizeOf(typeof(Fragment));
            result.Stats.FragmentRangeBytes = (long)ranges.Length * Marshal.SizeOf(typeof(FragmentRange));
        }

        private static double FanArea(Point[] polygon, int fan) =>
            Math.Abs(Cross(Subtract(polygon[fan], polygon[0]), Subtract(polygon[fan + 1], polygon[0]))) * 0.5;

        private static void EvaluatePoint(InputTriangle triangle, Point point, Point a, Point b, Point c,
            double signedArea2, out Vector3 position, out Vector2 uv)
        {
            float u = (float)(Cross(Subtract(point, a), Subtract(c, a)) / signedArea2);
            float v = (float)(Cross(Subtract(b, a), Subtract(point, a)) / signedArea2);
            position = triangle.P0 + (triangle.P1 - triangle.P0) * u + (triangle.P2 - triangle.P0) * v;
            uv = triangle.Uv0 + (triangle.Uv1 - triangle.Uv0) * u + (triangle.Uv2 - triangle.Uv0) * v;
        }

        private static int[] BuildCharts(IReadOnlyList<InputTriangle> triangles, CancellationToken token, out int chartCount)
        {
            var parents = new int[triangles.Count];
            var edges = new Dictionary<EdgeKey, int>();
            for (int i = 0; i < parents.Length; ++i) parents[i] = i;
            for (int i = 0; i < triangles.Count; ++i)
            {
                token.ThrowIfCancellationRequested();
                InputTriangle t = triangles[i];
                var a = new VertexKey { P = t.P0, UV = t.Atlas0 };
                var b = new VertexKey { P = t.P1, UV = t.Atlas1 };
                var c = new VertexKey { P = t.P2, UV = t.Atlas2 };
                UnionEdge(new EdgeKey { A = a, B = b, Renderer = t.Renderer }, i, edges, parents);
                UnionEdge(new EdgeKey { A = b, B = c, Renderer = t.Renderer }, i, edges, parents);
                UnionEdge(new EdgeKey { A = c, B = a, Renderer = t.Renderer }, i, edges, parents);
            }
            var ids = new Dictionary<int, int>();
            var chartAt = new int[triangles.Count];
            chartCount = 0;
            for (int i = 0; i < parents.Length; ++i)
            {
                int root = Root(parents, i);
                if (!ids.TryGetValue(root, out int id)) { id = ++chartCount; ids.Add(root, id); }
                chartAt[i] = id;
            }
            return chartAt;
        }

        private static int Root(int[] parents, int i)
        {
            int root = i;
            while (parents[root] != root) root = parents[root];
            while (parents[i] != i) { int next = parents[i]; parents[i] = root; i = next; }
            return root;
        }

        private static void UnionEdge(EdgeKey key, int triangle, Dictionary<EdgeKey, int> edges, int[] parents)
        {
            if (!edges.TryGetValue(key, out int previous)) { edges.Add(key, triangle); return; }
            int a = Root(parents, previous), b = Root(parents, triangle);
            if (a != b) parents[b] = a;
        }

        private static void AssignPadding(Result result, IReadOnlyList<InputTriangle> triangles, int[] charts, int size, int padding, CancellationToken token)
        {
            if(padding==0)return;
            // Bilinear reconstruction at UV p may access centre i+.5 when
            // |p-(i+.5)|<1. Thus intersect each triangle with the support
            // rectangle [i-.5,i+1.5], rather than dilating every covered
            // pixel by an arbitrary two-pixel square.
            var aPoly=new Point[10];var bPoly=new Point[10];
            for(int i=0;i<triangles.Count;++i)
            {
                token.ThrowIfCancellationRequested();var t=triangles[i];int chart=charts[i];
                Point a=AtlasPoint(t.Atlas0,size),b=AtlasPoint(t.Atlas1,size),c=AtlasPoint(t.Atlas2,size);
                int minX=Math.Max(0,(int)Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X))-.5));
                int maxX=Math.Min(size-1,(int)Math.Floor(Math.Max(a.X,Math.Max(b.X,c.X))+.5));
                int minY=Math.Max(0,(int)Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y))-.5));
                int maxY=Math.Min(size-1,(int)Math.Floor(Math.Max(a.Y,Math.Max(b.Y,c.Y))+.5));
                for(int y=minY;y<=maxY;++y)for(int x=minX;x<=maxX;++x)
                {
                    aPoly[0]=a;aPoly[1]=b;aPoly[2]=c;
                    int n=Clip(aPoly,3,bPoly,0,x-.5,true);n=Clip(bPoly,n,aPoly,0,x+1.5,false);
                    n=Clip(aPoly,n,bPoly,1,y-.5,true);n=Clip(bPoly,n,aPoly,1,y+1.5,false);
                    if(PolygonArea(aPoly,n)<=1e-14)continue;
                    int pixel=x+y*size;byte coverage=result.Coverage[pixel];
                    if(result.ChartOwners[pixel]==chart || coverage==3 || coverage==4)continue;
                    if(coverage==0){result.ChartOwners[pixel]=chart;result.Coverage[pixel]=2;++result.Stats.PaddingTexels;}
                    else {
                        result.ChartOwners[pixel]=-1;result.Coverage[pixel]=4;
                        if(coverage==2)--result.Stats.PaddingTexels;
                        ++result.Stats.PaddingChartConflicts;
                    }
                }
            }
        }

        private static Point AtlasPoint(Vector2 uv, int size) => new Point((double)uv.x * size, (double)uv.y * size);
        private static Point Subtract(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
        private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
        private static bool Finite(Vector2 p) => Finite(p.x) && Finite(p.y);
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        private static int Clip(Point[] input, int count, Point[] output, int axis, double boundary, bool minimum)
        {
            if (count == 0) return 0;
            int written = 0;
            Point previous = input[count - 1];
            double previousDistance = (axis == 0 ? previous.X : previous.Y) - boundary;
            bool previousInside = minimum ? previousDistance >= 0 : previousDistance <= 0;
            for (int i = 0; i < count; ++i)
            {
                Point current = input[i];
                double distance = (axis == 0 ? current.X : current.Y) - boundary;
                bool inside = minimum ? distance >= 0 : distance <= 0;
                if (inside != previousInside)
                {
                    double t = previousDistance / (previousDistance - distance);
                    output[written++] = new Point(previous.X + (current.X - previous.X) * t, previous.Y + (current.Y - previous.Y) * t);
                }
                if (inside) output[written++] = current;
                previous = current; previousDistance = distance; previousInside = inside;
            }
            return written;
        }

        private static double PolygonArea(Point[] polygon, int count)
        {
            if (count < 3) return 0;
            double area = 0;
            // Translating first avoids catastrophic cancellation for tiny UV
            // fragments near the far edge of a large atlas.
            for (int i = 1; i + 1 < count; ++i)
                area += Math.Abs(Cross(Subtract(polygon[i], polygon[0]), Subtract(polygon[i + 1], polygon[0]))) * 0.5;
            return area;
        }

        private static Point SamplePolygon(Point[] polygon, int count, ref uint seed)
        {
            double target = Random01(ref seed) * PolygonArea(polygon, count);
            int selected = count - 2;
            for (int i = 1; i + 1 < count; ++i)
            {
                double area = Math.Abs(Cross(Subtract(polygon[i], polygon[0]), Subtract(polygon[i + 1], polygon[0]))) * 0.5;
                if (target < area) { selected = i; break; }
                target -= area;
            }
            double u = Math.Sqrt(Random01(ref seed)), v = Random01(ref seed);
            Point a = polygon[0], b = polygon[selected], c = polygon[selected + 1];
            return new Point(a.X * (1 - u) + b.X * (u * (1 - v)) + c.X * (u * v),
                a.Y * (1 - u) + b.Y * (u * (1 - v)) + c.Y * (u * v));
        }

        private static uint Mix(uint x)
        {
            unchecked { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); }
        }
        private static double Random01(ref uint state)
        {
            state = Mix(unchecked(state + 0x9e3779b9u));
            return (state + 0.5) / 4294967296.0;
        }
    }
}
