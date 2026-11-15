using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// Independent mesh-only CDLOD forest. Adapted from the existing project's
    /// TerrainCdlodTree/Patch, which port fstrugar/CDLOD (ea8195b3).
    /// No TerrainData, material cache, feedback, RVT or SVT dependency.
    /// </summary>
    public sealed class MeshCdlod : IDisposable
    {
        // 1m finest spacing; node sizes and stored height
        // lattices are independent of patch tessellation.
        public const int GridQuads = 32;
        // Geometry gains two coarse parents; height storage remains five levels.
        public const int RootSize = 2048;
        public const int RootLod = 6;
        public const int HeightMaxLod = MeshHeightLayout.ClipLevels - 1;
        public const int RootsPerAxis = MeshHeightLayout.PaddedSize / RootSize;
        public static int HeightLevel(int geometryLod) => Mathf.Min(geometryLod,HeightMaxLod);
        public const int AllQuarters = 15;
        public const int LeafAxis = MeshHeightLayout.LeafAxis;
        const float MorphEndFudge = .01f;
        // Distances in the terrain metric, from 1m LOD0 to 64m LOD6.
        static readonly float[] BudgetRangeEnds = { 64f, 151f, 296f, 556f, 1050f, 2003f, 3872f };

        public struct Selection
        {
            public Vector4 Patch;
            public Vector4 Parameters;
            public Bounds Bounds;
            public float RawMorphEnd, MaximumDistance, NextMorphStart;
            public float VerticalDistanceScale;
            public int Lod => Mathf.RoundToInt(Parameters.z);
            public int Mask => Mathf.RoundToInt(Parameters.w);
        }

        struct Node
        {
            public int X, Z, Size, Lod, FirstChild;
            public Bounds Bounds;
            public Vector2 HeightRange;
            public bool Valid;
        }

        enum Traversal { OutOfRange, Handled, Culled }
        readonly List<Node> nodes;
        readonly List<int> roots;
        readonly float[] ends = new float[RootLod + 1];
        readonly float[] starts = new float[RootLod + 1];
        readonly ushort[] leafRanges;
        readonly float heightScale;
        readonly bool useBudgetRanges;
        readonly bool ownsForest;
        float cachedBase = -1, cachedRatio = -1;
        bool disposed;

        public Mesh Patch { get; private set; }
        public List<Selection> Selections { get; } = new List<Selection>(4096);
        public float BaseDistance;
        public float MorphRatio;
        public float VerticalDistanceScale { get; }
        public int RootCount => roots.Count;
        public int NodeCount => nodes.Count;
        public int LastVisitedNodes { get; private set; }
        public int RangeViolations { get; private set; }
        public long MetadataBytes => (long)leafRanges.Length * sizeof(ushort);
        public float MaximumRange => ends[RootLod];
        public float RangeEnd(int lod) => ends[lod];
        public Vector4 ObserverParameters(Vector3 observer) =>
            new Vector4(observer.x, observer.y, observer.z, VerticalDistanceScale);

        MeshCdlod(bool useBudgetRanges)
        {
            this.useBudgetRanges = useBudgetRanges;
            BaseDistance = useBudgetRanges ? BudgetRangeEnds[0] : 140f;
            MorphRatio = useBudgetRanges ? .84f : .66f;
            VerticalDistanceScale = useBudgetRanges ? .25f : 1f;
        }

        public MeshCdlod(string datasetDirectory, bool useBudgetRanges = true) : this(useBudgetRanges)
        {
            ownsForest = true;
            nodes = new List<Node>(140000);
            roots = new List<int>(RootsPerAxis * RootsPerAxis);
            heightScale = JsonUtility.FromJson<MeshHeightMetadata>(File.ReadAllText(
                Path.Combine(datasetDirectory, MeshHeightLayout.MetadataName))).heightScale;
            string path = Path.Combine(datasetDirectory, "leaf-bounds.bin");
            byte[] bytes = File.ReadAllBytes(path);
            int words = checked(LeafAxis * LeafAxis * 2);
            if (bytes.Length != words * sizeof(ushort))
                throw new InvalidDataException("DirectDelta mesh leaf-bounds length mismatch: " + path);
            leafRanges = new ushort[words];
            Buffer.BlockCopy(bytes, 0, leafRanges, 0, bytes.Length);
            for (int i = 0; i < leafRanges.Length; i += 2)
                if (leafRanges[i] > leafRanges[i + 1])
                    throw new InvalidDataException("DirectDelta mesh leaf min/max are reversed.");

            for (int z = 0; z < RootsPerAxis; ++z)
                for (int x = 0; x < RootsPerAxis; ++x)
                {
                    int root = nodes.Count;
                    roots.Add(root);
                    nodes.Add(default(Node));
                    BuildNode(root, x * RootSize, z * RootSize, RootSize, RootLod);
                }
            ConfigureRanges(true);
            Patch = BuildPatch(heightScale);
        }

        // The comparison owns the immutable forest and patch. It
        // releases each camera selection before releasing the shared owner.
        MeshCdlod(MeshCdlod owner, bool legacyRanges) : this(!legacyRanges && owner.useBudgetRanges)
        {
            owner.RequireLive();
            nodes = owner.nodes;
            roots = owner.roots;
            leafRanges = owner.leafRanges;
            heightScale = owner.heightScale;
            Patch = owner.Patch;
            ConfigureRanges(true);
        }

        internal MeshCdlod CreateSelection(bool legacyRanges) => new MeshCdlod(this, legacyRanges);

        void BuildNode(int index, int x, int z, int size, int lod)
        {
            var node = new Node { X = x, Z = z, Size = size, Lod = lod, FirstChild = -1,
                Valid = x < MeshHeightLayout.WorldSize && z < MeshHeightLayout.WorldSize };
            if (!node.Valid) { nodes[index] = node; return; }
            if (lod == 0)
            {
                int start = ((z / MeshHeightLayout.LeafIntervals) * LeafAxis + x / MeshHeightLayout.LeafIntervals) * 2;
                node.HeightRange = new Vector2(leafRanges[start], leafRanges[start + 1]) *
                    (heightScale / ushort.MaxValue);
            }
            else
            {
                int half = size / 2, first = nodes.Count;
                node.FirstChild = first;
                for (int q = 0; q < 4; ++q) nodes.Add(default(Node));
                var range = new Vector2(float.PositiveInfinity, float.NegativeInfinity);
                for (int q = 0; q < 4; ++q)
                {
                    BuildNode(first + q, x + (q & 1) * half, z + (q >> 1) * half, half, lod - 1);
                    var child = nodes[first + q];
                    if (!child.Valid) continue;
                    range.x = Mathf.Min(range.x, child.HeightRange.x);
                    range.y = Mathf.Max(range.y, child.HeightRange.y);
                }
                node.HeightRange = range;
            }
            float xmax = Mathf.Min(x + size, MeshHeightLayout.WorldSize);
            float zmax = Mathf.Min(z + size, MeshHeightLayout.WorldSize);
            var minimum = new Vector3(x, node.HeightRange.x, z);
            var maximum = new Vector3(xmax, node.HeightRange.y, zmax);
            node.Bounds = new Bounds((minimum + maximum) * .5f, maximum - minimum + Vector3.one * .04f);
            nodes[index] = node;
        }

        void FillRanges(float baseDistance, float ratio)
        {
            float previousStart = 0;
            for (int lod = 0; lod <= RootLod; ++lod)
            {
                ends[lod] = useBudgetRanges
                    ? BudgetRangeEnds[lod] * (baseDistance / BudgetRangeEnds[0])
                    : baseDistance * ((2 << lod) - 1);
                starts[lod] = previousStart + (ends[lod] - previousStart) * ratio;
                previousStart = starts[lod];
            }
        }

        public float RecommendedBaseDistance(float morphRatio)
        {
            if (!Finite(morphRatio) || morphRatio <= 0 || morphRatio >= 1)
                throw new ArgumentOutOfRangeException(nameof(morphRatio));
            FillRanges(1, morphRatio);
            float recommendation = 1;
            foreach (var node in nodes)
            {
                if (!node.Valid || node.Lod == RootLod) continue;
                float gap = starts[node.Lod + 1] - ends[node.Lod];
                if (gap <= 0) throw new InvalidOperationException("CDLOD ranges have no safe neighbour morph gap.");
                // The same weighted metric is used in selection and vertex morph.
                // Its AABB diagonal bounds the distance variation inside this node.
                Vector3 size = node.Bounds.size;
                size.y *= VerticalDistanceScale;
                recommendation = Mathf.Max(recommendation, size.magnitude / gap);
            }
            // The ranges scratch arrays must be restored by the next ConfigureRanges.
            cachedBase = -1;
            return recommendation * 1.05f;
        }

        void ConfigureRanges(bool raiseToRecommendation = false)
        {
            if (BaseDistance == cachedBase && MorphRatio == cachedRatio) return;
            if (!Finite(BaseDistance) || BaseDistance <= 0 || !Finite(MorphRatio) || MorphRatio <= 0 || MorphRatio >= 1)
                throw new InvalidOperationException("CDLOD needs a positive finite distance and a morph ratio strictly inside (0,1).");
            float minimum = RecommendedBaseDistance(MorphRatio);
            if (raiseToRecommendation) BaseDistance = Mathf.Max(BaseDistance, minimum);
            else if (BaseDistance < minimum)
                throw new InvalidOperationException("CDLOD base distance violates conservative morph safety. Need at least " + minimum);
            FillRanges(BaseDistance, MorphRatio);

            // Origins snap down to 64 sample steps. A finest-point query also
            // needs the next sample for bilinear reconstruction. Require every
            // potentially selected patch to fit even in the worst snap phase.
            for (int lod = 0; lod < HeightMaxLod; ++lod)
            {
                float stride = 1 << lod;
                float safeReach = ((MeshHeightLayout.ClipSize - 1) * .5f - 64 - 1) * stride;
                float requestedReach = ends[lod] + MeshHeightLayout.LeafIntervals * stride;
                if (requestedReach > safeReach)
                    throw new InvalidOperationException("CDLOD LOD" + lod + " can leave its height Clipmap window. " +
                        "Reduce the distance or explicitly enlarge the Clipmap; never silently fall back to a coarser height.");
            }
            cachedBase = BaseDistance; cachedRatio = MorphRatio;
        }

        public void Select(Vector3 observer, Plane[] frustum)
        {
            RequireLive();
            if (!Finite(observer.x) || !Finite(observer.y) || !Finite(observer.z))
                throw new ArgumentException("The CDLOD observer must be finite.");
            if (frustum != null)
            {
                if (frustum.Length != 6) throw new ArgumentException("A camera frustum must have six planes.");
                foreach (var p in frustum)
                    if (!Finite(p.distance) || !Finite(p.normal.x) || !Finite(p.normal.y) || !Finite(p.normal.z) || p.normal.sqrMagnitude < 1e-12f)
                        throw new ArgumentException("Camera frustum planes must be finite and nonzero.");
            }
            ConfigureRanges();
            Selections.Clear(); LastVisitedNodes = 0; RangeViolations = 0;
            foreach (int root in roots) Visit(root, observer, frustum, false);
            if (RangeViolations != 0)
                throw new InvalidOperationException("CDLOD selection crossed " + RangeViolations + " next-level morph safety ranges.");
        }

        Traversal Visit(int index, Vector3 observer, Plane[] planes, bool inside)
        {
            ++LastVisitedNodes;
            Node node = nodes[index];
            if (!node.Valid) return Traversal.Culled;
            if (!inside && planes != null)
            {
                int result = Frustum(node.Bounds, planes);
                if (result < 0) return Traversal.Culled;
                inside = result > 0;
            }
            float end = ends[node.Lod];
            if (node.Lod != RootLod && MinDistanceSquared(node.Bounds, observer) > end * end)
                return Traversal.OutOfRange;
            int mask = AllQuarters;
            bool childHandled = false;
            if (node.Lod > 0 && MinDistanceSquared(node.Bounds, observer) <= ends[node.Lod - 1] * ends[node.Lod - 1])
                for (int q = 0; q < 4; ++q)
                {
                    Traversal child = Visit(node.FirstChild + q, observer, planes, inside);
                    childHandled |= child == Traversal.Handled;
                    if (child != Traversal.OutOfRange) mask &= ~(1 << q);
                }
            if (mask != 0)
            {
                float maximumDistance = Mathf.Sqrt(MaxDistanceSquared(node.Bounds, observer));
                float nextStart = node.Lod < RootLod ? starts[node.Lod + 1] : float.PositiveInfinity;
                if (maximumDistance > nextStart) ++RangeViolations;
                Selections.Add(new Selection {
                    Patch = new Vector4(node.X, node.Z, node.Size, node.Size),
                    Parameters = new Vector4(starts[node.Lod], Mathf.Lerp(end, starts[node.Lod], MorphEndFudge), node.Lod, mask),
                    Bounds = node.Bounds, RawMorphEnd = end, MaximumDistance = maximumDistance, NextMorphStart = nextStart,
                    VerticalDistanceScale = VerticalDistanceScale
                });
            }
            return mask != 0 || childHandled ? Traversal.Handled : Traversal.Culled;
        }

        public void ValidateClipCoverage(Vector4[] origins)
        {
            RequireLive();
            if (origins == null || origins.Length != MeshHeightLayout.ClipLevels)
                throw new ArgumentException("All five Clipmap descriptors must be supplied.");
            // Both paths use level4 for their stable pre-morph distance metric.
            ValidateRectangle(origins[HeightMaxLod], HeightMaxLod, Vector2.zero,
                Vector2.one * MeshHeightLayout.WorldSize);
            foreach (var selection in Selections)
            {
                int lod = selection.Lod;
                Vector2 origin = new Vector2(selection.Patch.x, selection.Patch.y);
                Vector2 maximum = origin + new Vector2(selection.Patch.z, selection.Patch.w);
                maximum.x = Mathf.Min(maximum.x, MeshHeightLayout.WorldSize);
                maximum.y = Mathf.Min(maximum.y, MeshHeightLayout.WorldSize);
                int heightLevel = HeightLevel(lod);
                ValidateRectangle(origins[heightLevel], heightLevel, origin, maximum);
                // Fine vertices now also reach the parent's sampled surface
                // during morph, rather than only the parent's XZ lattice.
                if(lod < RootLod)
                {
                    int parentLevel = HeightLevel(lod+1);
                    ValidateRectangle(origins[parentLevel], parentLevel, origin, maximum);
                }
            }
        }

        static void ValidateRectangle(Vector4 descriptor, int lod, Vector2 minimum, Vector2 maximum)
        {
            int stride = 1 << lod;
            // Include the extra high-side sample read by explicit bilinear Load.
            float xmax = (Mathf.Floor(maximum.x / stride) + 1) * stride;
            float zmax = (Mathf.Floor(maximum.y / stride) + 1) * stride;
            float extent = (MeshHeightLayout.ClipSize - 1) * stride;
            if (descriptor.w < .5f || descriptor.z != stride || minimum.x < descriptor.x || minimum.y < descriptor.y ||
                xmax > descriptor.x + extent || zmax > descriptor.y + extent)
                throw new InvalidOperationException("Height Clipmap does not cover selected LOD" + lod + " geometry and its bilinear support.");
        }

        static Mesh BuildPatch(float heightScale)
        {
            const int half = GridQuads / 2, side = half + 1, perQuarter = side * side;
            var vertices = new Vector3[perQuarter * 4];
            var uv = new Vector2[vertices.Length]; var quarters = new Vector2[vertices.Length];
            var indices = new int[GridQuads * GridQuads * 6]; int write = 0;
            for (int q = 0; q < 4; ++q)
            {
                int start = q * perQuarter, ox = (q & 1) * half, oz = (q >> 1) * half;
                for (int z = 0; z <= half; ++z) for (int x = 0; x <= half; ++x)
                {
                    int i = start + z * side + x;
                    vertices[i] = new Vector3(ox + x, 0, oz + z);
                    uv[i] = new Vector2((ox + x) / (float)GridQuads, (oz + z) / (float)GridQuads);
                    quarters[i] = new Vector2(q, 0);
                }
                for (int z = 0; z < half; ++z) for (int x = 0; x < half; ++x)
                {
                    int a = start + z * side + x, b = a + side, c = a + 1, d = b + 1;
                    indices[write++] = a; indices[write++] = b; indices[write++] = c;
                    indices[write++] = c; indices[write++] = b; indices[write++] = d;
                }
            }
            var patch = new Mesh { name = "DirectDelta shared 32x32 CDLOD patch", hideFlags = HideFlags.HideAndDontSave };
            patch.vertices = vertices; patch.uv = uv; patch.uv2 = quarters; patch.triangles = indices;
            // GPU-instanced vertices cover any selected world node. CPU performs
            // per-node frustum selection; a unit-grid bounds would wrongly cull it.
            patch.bounds = new Bounds(new Vector3(5000, heightScale * .5f, 5000),
                new Vector3(10000.1f, heightScale + .1f, 10000.1f));
            patch.UploadMeshData(false);
            return patch;
        }

        static int Frustum(Bounds bounds, Plane[] planes)
        {
            bool fullyInside = true;
            var center = bounds.center; var extents = bounds.extents;
            foreach (var p in planes)
            {
                float radius = Mathf.Abs(p.normal.x) * extents.x + Mathf.Abs(p.normal.y) * extents.y + Mathf.Abs(p.normal.z) * extents.z;
                float distance = p.GetDistanceToPoint(center);
                if (distance + radius < 0) return -1;
                fullyInside &= distance - radius >= 0;
            }
            return fullyInside ? 1 : 0;
        }

        float MinDistanceSquared(Bounds bounds, Vector3 point)
        {
            var min = bounds.min; var max = bounds.max;
            float dx = Mathf.Max(min.x - point.x, 0, point.x - max.x);
            float dy = Mathf.Max(min.y - point.y, 0, point.y - max.y) * VerticalDistanceScale;
            float dz = Mathf.Max(min.z - point.z, 0, point.z - max.z);
            return dx * dx + dy * dy + dz * dz;
        }

        float MaxDistanceSquared(Bounds bounds, Vector3 point)
        {
            var min = bounds.min; var max = bounds.max;
            float dx = Mathf.Max(Mathf.Abs(point.x - min.x), Mathf.Abs(point.x - max.x));
            float dy = Mathf.Max(Mathf.Abs(point.y - min.y), Mathf.Abs(point.y - max.y)) * VerticalDistanceScale;
            float dz = Mathf.Max(Mathf.Abs(point.z - min.z), Mathf.Abs(point.z - max.z));
            return dx * dx + dy * dy + dz * dz;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        void RequireLive() { if (disposed) throw new ObjectDisposedException(nameof(MeshCdlod)); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (ownsForest)
            {
                nodes.Clear();
                roots.Clear();
                if (Patch)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(Patch);
                    else UnityEngine.Object.DestroyImmediate(Patch);
                }
            }
            Patch = null;
            Selections.Clear();
        }
    }
}
