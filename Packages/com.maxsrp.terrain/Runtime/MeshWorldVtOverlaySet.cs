using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // The first writer supports complete PBR overlays or albedo-only overlays.
    // Other bit combinations need separate MRT blend rules and are not exposed.
    public enum MeshWorldVtOverlayChannels { Albedo = 1, Full = 15 }

    [CreateAssetMenu(menuName = "MaxTerrain/World VT Overlay Set", fileName = "WorldVtOverlays")]
    public sealed class MeshWorldVtOverlaySet : ScriptableObject
    {
        public const string AlgorithmVersion = "Surface16-static-overlays-v1-ribbon-miter4-box";
        public const int MaximumMaskSide = 1024;
        public const long MaximumMaskBytes = 8 * 1024 * 1024;

        [Serializable]
        public abstract class Entry
        {
            public string stableId = "overlay";
            public int order;
            [Range(0, 15)] public int materialId;
            [Tooltip("Linear RGB multiplier. Alpha is ignored; use opacity.")]
            public Color tint = Color.white;
            [Range(0, 1)] public float opacity = 1;
            [Tooltip("World metres per material repeat: road transverse/longitudinal, box X/Z.")]
            public Vector2 uvMetres = Vector2.one;
            public Vector2 uvOffset;
            public Texture2D maskTexture;
            [Range(0, 3)] public int maskChannel;
            public Vector4 maskUv = new Vector4(1, 1, 0, 0);
            public MeshWorldVtOverlayChannels channels = MeshWorldVtOverlayChannels.Full;
        }

        [Serializable]
        public sealed class Road : Entry
        {
            public Vector2[] centreLine = { Vector2.zero, new Vector2(10, 0) };
            [Tooltip("Full outer width, including the feather at each edge.")]
            public float width = 6;
            [Tooltip("Fade inwards from each outer edge, in world metres.")]
            public float feather = .5f;
        }

        [Serializable]
        public sealed class Decal : Entry
        {
            public Vector3 position;
            public Vector3 rotationEuler;
            public Vector3 size = new Vector3(12, 12, 12);
            [Tooltip("Fade inwards from the box X/Z edges. Y only limits projection depth.")]
            public float feather = .5f;
        }

        public Road[] roads = Array.Empty<Road>();
        public Decal[] decals = Array.Empty<Decal>();
        public Shader writerShader, repackShader;
        [NonSerialized] int revision;
        void OnValidate() { unchecked { revision++; } }

        public Snapshot CreateSnapshot() => new Snapshot(this);

        // 场景贴花、矩形道路和旧资产内道路汇入同一份静态快照。ContentHash只记录有效绘制
        // 数据，不记录它来自资产还是GameObject；原样迁移无需更换SVT配方。
        public static Snapshot CreateSnapshot(MeshWorldVtOverlaySet set,
            Func<MeshWorldVtDecal[]> sceneDecals, Func<MeshWorldVtRoad[]> sceneRoads = null)
        {
            bool ownsSource = !set;
            if (ownsSource)
                set = CreateInstance<MeshWorldVtOverlaySet>();
            if (ownsSource) set.hideFlags = HideFlags.HideAndDontSave;
            return new Snapshot(set, sceneDecals, ownsSource, sceneRoads);
        }

        /// <summary>
        /// Immutable authoring snapshot. Meshes and private mask copies belong to this
        /// object; the overlay material and source PBR pair belong to the producer.
        /// </summary>
        public sealed class Snapshot : IDisposable
        {
            sealed class Item
            {
                public string Id;
                public int Order, Pass;
                public bool Visible;
                public Rect Bounds;
                public Mesh Mesh;
                public MaterialPropertyBlock Properties;
            }

            sealed class Mask
            {
                public Texture2D Texture;
                public string Hash;
            }

            readonly MeshWorldVtOverlaySet source;
            readonly Func<MeshWorldVtDecal[]> sceneDecals;
            readonly Func<MeshWorldVtRoad[]> sceneRoads;
            readonly int sourceRevision;
            readonly string watchHash;
            readonly List<Item> items = new List<Item>();
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly Dictionary<int, Mask> masks = new Dictionary<int, Mask>();
            bool disposed;

            public string ContentHash { get; }
            public int Count => items.Count;
            public int RoadCount { get; private set; }
            public int DecalCount { get; private set; }
            public long MaskBytes { get; private set; }

            internal Snapshot(MeshWorldVtOverlaySet set, Func<MeshWorldVtDecal[]> sceneDecals = null,
                bool ownsSource = false, Func<MeshWorldVtRoad[]> sceneRoads = null)
            {
                source = set ? set : throw new ArgumentNullException(nameof(set));
                this.sceneDecals = sceneDecals;
                this.sceneRoads = sceneRoads;
                if (ownsSource) owned.Add(set);
                sourceRevision = set.revision;
                try
                {
                    if (set.roads == null || set.decals == null)
                        throw new InvalidDataException("Overlay arrays cannot be null.");
                    var entries = new List<Entry>(set.roads.Length + set.decals.Length);
                    entries.AddRange(set.roads);
                    entries.AddRange(set.decals);
                    // 矩形作者对象转换成原有两点 Road 契约。来源不会写入内容 hash，
                    // 后续写页、排序、材质采样及压缩仍走同一条已经验证的道路路径。
                    if (sceneRoads != null)
                    {
                        MeshWorldVtRoad[] components = sceneRoads();
                        if (components == null) throw new InvalidDataException("Scene road provider returned null.");
                        foreach (MeshWorldVtRoad component in components)
                        {
                            if (!component) throw new InvalidDataException("A scene road was destroyed while creating the snapshot.");
                            entries.Add(component.Capture());
                        }
                    }
                    // 收集只发生在当前地形所属场景内；之后冻结网格/参数/mask。
                    // provider同时用于监测新增、删除和启停，不能只保存首次找到的数组。
                    if (sceneDecals != null)
                    {
                        MeshWorldVtDecal[] components = sceneDecals();
                        if (components == null) throw new InvalidDataException("Scene decal provider returned null.");
                        foreach (MeshWorldVtDecal component in components)
                        {
                            if (!component) throw new InvalidDataException("A scene decal was destroyed while creating the snapshot.");
                            entries.Add(component.Capture());
                        }
                    }
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Entry entry in entries)
                    {
                        ValidateEntry(entry);
                        if (!ids.Add(entry.stableId))
                            throw new InvalidDataException("Overlay stable IDs must be unique: " + entry.stableId);
                    }
                    entries.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) :
                        StringComparer.Ordinal.Compare(a.stableId, b.stableId));
                    using (var bytes = new MemoryStream())
                    using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                    {
                        writer.Write(AlgorithmVersion);
                        writer.Write(set.writerShader ? set.writerShader.name : "");
                        writer.Write(set.repackShader ? set.repackShader.name : "");
                        writer.Write(entries.Count);
                        foreach (Entry entry in entries)
                        {
                            Mask mask = FreezeMask(entry.maskTexture);
                            WriteEntry(writer, entry, false, mask?.Hash);
                            Item item = entry is Road road ? BuildRoad(road) : BuildDecal((Decal)entry);
                            item.Id = entry.stableId;
                            item.Order = entry.order;
                            item.Visible = entry.opacity > 0;
                            item.Properties = MakeProperties(entry, mask);
                            if (entry is Decal box)
                            {
                                item.Properties.SetMatrix("_DDOverlayWorldToBox",
                                    Matrix4x4.TRS(box.position, Quaternion.Euler(box.rotationEuler), box.size).inverse);
                                item.Properties.SetVector("_DDOverlayBoxSize", new Vector4(box.size.x, box.size.y, box.size.z, 0));
                                item.Properties.SetFloat("_DDOverlayBoxFeather", box.feather);
                            }
                            items.Add(item);
                        }
                        writer.Flush();
                        ContentHash = MeshMaterialResident.Sha256(bytes.ToArray());
                    }
                    // The watch fingerprint includes cheap field/texture update state,
                    // not pixel reads. Full raw mask hashes are computed only above.
                    watchHash = WatchHash(set, sceneDecals, sceneRoads);
                }
                catch { Dispose(); throw; }
            }

            public bool SourceUnchanged
            {
                get
                {
                    if (disposed || !source || source.revision != sourceRevision) return false;
                    try { return WatchHash(source, sceneDecals, sceneRoads) == watchHash; }
                    catch { return false; }
                }
            }

            public void EnsureSourceUnchanged()
            {
                if (!SourceUnchanged)
                    throw new InvalidOperationException("Static VT overlay inputs changed. Recreate the snapshot and use the matching rebaked SVT archive.");
            }

            public bool HasCoverage(Rect interiorWorldRect)
            {
                RequireLive();
                Rect query = PageQuery(interiorWorldRect);
                foreach (Item item in items) if (item.Visible && Intersects(item.Bounds, query)) return true;
                return false;
            }

            /// <summary>Draw only ordered objects touching this page including its border.</summary>
            public int Draw(CommandBuffer commands, Material material, Rect interiorWorldRect)
            {
                RequireLive();
                if (commands == null || !material) throw new ArgumentNullException("Overlay draw needs a command buffer and material.");
                Rect query = PageQuery(interiorWorldRect);
                int count = 0;
                foreach (Item item in items)
                {
                    if (!item.Visible || !Intersects(item.Bounds, query)) continue;
                    commands.DrawMesh(item.Mesh, Matrix4x4.identity, material, 0, item.Pass, item.Properties);
                    count++;
                }
                return count;
            }

            static Rect PageQuery(Rect rect)
            {
                if (!Finite(rect.x) || !Finite(rect.y) || !Finite(rect.width) || !Finite(rect.height) ||
                    rect.width <= 0 || rect.height <= 0 || !Finite(rect.xMax) || !Finite(rect.yMax))
                    throw new ArgumentException("VT page rectangle must be finite and positive.");
                float px = rect.width * MeshWorldVirtualTexture.Border / MeshWorldVirtualTexture.PageInterior;
                float pz = rect.height * MeshWorldVirtualTexture.Border / MeshWorldVirtualTexture.PageInterior;
                return Rect.MinMaxRect(Mathf.Clamp(rect.xMin - px, 0, MeshControlContract.WorldSize),
                    Mathf.Clamp(rect.yMin - pz, 0, MeshControlContract.WorldSize),
                    Mathf.Clamp(rect.xMax + px, 0, MeshControlContract.WorldSize),
                    Mathf.Clamp(rect.yMax + pz, 0, MeshControlContract.WorldSize));
            }

            static bool Intersects(Rect a, Rect b) => a.xMin <= b.xMax && a.xMax >= b.xMin &&
                a.yMin <= b.yMax && a.yMax >= b.yMin;

            Mask FreezeMask(Texture2D texture)
            {
                if (!texture) return null;
                if (masks.TryGetValue(texture.GetInstanceID(), out Mask existing)) return existing;
                if (!texture.isReadable || texture.width < 1 || texture.height < 1 ||
                    texture.width > MaximumMaskSide || texture.height > MaximumMaskSide ||
                    GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat) ||
                    GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) ||
                    texture.mipmapCount != MeshMaterialSet.CountMips(Math.Max(texture.width, texture.height)))
                    throw new InvalidDataException("Overlay masks must be readable linear uncompressed textures, at most 1024 per side, with complete mip chains.");
                byte[] raw = texture.GetRawTextureData<byte>().ToArray();
                if (raw.Length == 0 || MaskBytes + raw.LongLength > MaximumMaskBytes)
                    throw new InvalidDataException("Overlay mask snapshot exceeds the 8 MiB local-mask budget.");
                var clone = new Texture2D(texture.width, texture.height, texture.format, texture.mipmapCount > 1, true)
                {
                    name = "VT overlay frozen mask " + texture.name,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 1,
                    wrapModeU = texture.wrapModeU,
                    wrapModeV = texture.wrapModeV
                };
                owned.Add(clone);
                clone.LoadRawTextureData(raw);
                clone.Apply(false, true);
                if (clone.graphicsFormat != texture.graphicsFormat || clone.mipmapCount != texture.mipmapCount)
                    throw new InvalidDataException("Unity changed the frozen overlay mask format or mip layout.");
                var result = new Mask { Texture = clone, Hash = MeshMaterialResident.Sha256(raw) };
                masks.Add(texture.GetInstanceID(), result);
                MaskBytes += raw.LongLength;
                return result;
            }

            static MaterialPropertyBlock MakeProperties(Entry entry, Mask mask)
            {
                var properties = new MaterialPropertyBlock();
                properties.SetInt("_DDOverlayMaterialId", entry.materialId);
                properties.SetVector("_DDOverlayTint", new Vector4(entry.tint.r, entry.tint.g, entry.tint.b, 1)); // Linear RGB, no Color-property conversion.
                properties.SetFloat("_DDOverlayOpacity", entry.opacity);
                properties.SetVector("_DDOverlayUv", new Vector4(1 / entry.uvMetres.x, 1 / entry.uvMetres.y,
                    entry.uvOffset.x, entry.uvOffset.y));
                properties.SetTexture("_DDOverlayMask", mask == null ? Texture2D.whiteTexture : mask.Texture);
                properties.SetInt("_DDOverlayMaskChannel", entry.maskChannel);
                properties.SetVector("_DDOverlayMaskUv", entry.maskUv);
                return properties;
            }

            Item BuildRoad(Road road)
            {
                Vector2[] points = (Vector2[])road.centreLine.Clone();
                int n = points.Length;
                var directions = new Vector2[n - 1];
                var distance = new float[n];
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 delta = points[i + 1] - points[i];
                    float length = delta.magnitude;
                    if (length < 1e-4f) throw new InvalidDataException("Road centre-line segments must have distinct endpoints.");
                    directions[i] = delta / length;
                    distance[i + 1] = distance[i] + length;
                    if (!Finite(distance[i + 1])) throw new InvalidDataException("Road cumulative length is not finite.");
                }
                for (int i = 0; i < n - 1; i++)
                    for (int j = i + 2; j < n - 1; j++)
                        if (SegmentsIntersect(points[i], points[i + 1], points[j], points[j + 1]))
                            throw new InvalidDataException("Road centre line intersects or touches itself: " + road.stableId);

                var vertices = new Vector3[checked(n * 2)];
                var uv = new Vector2[vertices.Length];
                var shape = new Vector2[vertices.Length];
                var tangents = new Vector4[vertices.Length];
                var normals = new Vector3[vertices.Length];
                var boundary = new Vector2[vertices.Length];
                float half = road.width * .5f;
                for (int i = 0; i < n; i++)
                {
                    Vector2 previous = directions[Math.Max(i - 1, 0)], next = directions[Math.Min(i, n - 2)];
                    Vector2 right0 = new Vector2(previous.y, -previous.x), right1 = new Vector2(next.y, -next.x);
                    Vector2 miter = (right0 + right1).normalized;
                    float denominator = Vector2.Dot(miter, right1);
                    if (denominator <= .25f)
                        throw new InvalidDataException("Road bend needs a wider turn; miter length exceeds four half-widths: " + road.stableId);
                    Vector2 offset = miter * (half / denominator);
                    Vector2 negative = points[i] - offset, positive = points[i] + offset;
                    if (!Finite(negative) || !Finite(positive)) throw new InvalidDataException("Road ribbon coordinates are not finite.");
                    vertices[2 * i] = new Vector3(negative.x, 0, negative.y);
                    vertices[2 * i + 1] = new Vector3(positive.x, 0, positive.y);
                    uv[2 * i] = new Vector2(-half, distance[i]);
                    uv[2 * i + 1] = new Vector2(half, distance[i]);
                    shape[2 * i] = shape[2 * i + 1] = new Vector2(half, road.feather);
                    tangents[2 * i] = tangents[2 * i + 1] = new Vector4(miter.x, 0, miter.y, 1);
                    normals[2 * i] = normals[2 * i + 1] = Vector3.up;
                    boundary[i] = negative;
                    boundary[2 * n - 1 - i] = positive;
                }
                ValidateBoundary(boundary, road.stableId);
                var triangles = new int[checked((n - 1) * 6)];
                for (int i = 0; i < n - 1; i++)
                {
                    int a = i * 2, index = i * 6;
                    triangles[index] = a; triangles[index + 1] = a + 2; triangles[index + 2] = a + 1;
                    triangles[index + 3] = a + 1; triangles[index + 4] = a + 2; triangles[index + 5] = a + 3;
                    float area0 = Vector3.Cross(vertices[a + 2] - vertices[a], vertices[a + 1] - vertices[a]).y;
                    float area1 = Vector3.Cross(vertices[a + 2] - vertices[a + 1], vertices[a + 3] - vertices[a + 1]).y;
                    if (!Finite(area0) || !Finite(area1) || area0 <= 0 || area1 <= 0)
                        throw new InvalidDataException("Road width folds the ribbon at a bend: " + road.stableId);
                }
                Mesh mesh = OwnMesh("VT road " + road.stableId, vertices, triangles);
                mesh.uv = uv; mesh.uv2 = shape; mesh.tangents = tangents; mesh.normals = normals;
                RoadCount++;
                return new Item { Mesh = mesh, Bounds = WorldBounds(vertices),
                    Pass = road.channels == MeshWorldVtOverlayChannels.Full ? 0 : 2 };
            }

            Item BuildDecal(Decal decal)
            {
                Matrix4x4 box = Matrix4x4.TRS(decal.position, Quaternion.Euler(decal.rotationEuler), decal.size);
                Matrix4x4 inverse = box.inverse;
                for (int i = 0; i < 16; i++)
                    if (!Finite(box[i]) || !Finite(inverse[i])) throw new InvalidDataException("Decal box transform is singular or non-finite.");
                if (Mathf.Abs(box.determinant) < 1e-12f) throw new InvalidDataException("Decal box transform is singular.");
                var corners = new Vector3[8];
                for (int i = 0; i < 8; i++)
                    corners[i] = box.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -.5f : .5f,
                        (i & 2) == 0 ? -.5f : .5f, (i & 4) == 0 ? -.5f : .5f));
                Rect raw = BoundsOf(corners);
                if (raw.width <= 0 || raw.height <= 0)
                    throw new InvalidDataException("Decal projection collapses at world-coordinate float precision.");
                var vertices = new[] { new Vector3(raw.xMin, 0, raw.yMin), new Vector3(raw.xMax, 0, raw.yMin),
                    new Vector3(raw.xMin, 0, raw.yMax), new Vector3(raw.xMax, 0, raw.yMax) };
                Mesh mesh = OwnMesh("VT box decal " + decal.stableId, vertices, new[] { 0, 2, 1, 1, 2, 3 });
                DecalCount++;
                return new Item { Mesh = mesh, Bounds = ClampBounds(raw),
                    Pass = decal.channels == MeshWorldVtOverlayChannels.Full ? 1 : 3 };
            }

            Mesh OwnMesh(string name, Vector3[] vertices, int[] triangles)
            {
                var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                owned.Add(mesh);
                mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateBounds();
                return mesh;
            }

            static Rect WorldBounds(Vector3[] vertices) => ClampBounds(BoundsOf(vertices));
            static Rect BoundsOf(Vector3[] vertices)
            {
                float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
                foreach (Vector3 v in vertices)
                {
                    if (!Finite(v)) throw new InvalidDataException("Overlay projection bounds are not finite.");
                    minX = Mathf.Min(minX, v.x); minZ = Mathf.Min(minZ, v.z);
                    maxX = Mathf.Max(maxX, v.x); maxZ = Mathf.Max(maxZ, v.z);
                }
                if (!Finite(maxX - minX) || !Finite(maxZ - minZ))
                    throw new InvalidDataException("Overlay projection extents overflow float precision.");
                return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            }

            static Rect ClampBounds(Rect bounds)
            {
                const int world = MeshControlContract.WorldSize;
                if (bounds.xMax < 0 || bounds.yMax < 0 || bounds.xMin > world || bounds.yMin > world)
                    throw new InvalidDataException("Overlay projection must meet the terrain world.");
                return Rect.MinMaxRect(Mathf.Clamp(bounds.xMin, 0, world), Mathf.Clamp(bounds.yMin, 0, world),
                    Mathf.Clamp(bounds.xMax, 0, world), Mathf.Clamp(bounds.yMax, 0, world));
            }

            static void ValidateBoundary(Vector2[] polygon, string id)
            {
                for (int i = 0; i < polygon.Length; i++)
                for (int j = i + 1; j < polygon.Length; j++)
                {
                    int nextI = (i + 1) % polygon.Length, nextJ = (j + 1) % polygon.Length;
                    if (j == nextI || nextJ == i) continue;
                    if (SegmentsIntersect(polygon[i], polygon[nextI], polygon[j], polygon[nextJ]))
                        throw new InvalidDataException("Road width produces an intersecting ribbon: " + id);
                }
            }

            static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            {
                const double epsilon = 1e-7;
                if (Math.Max(a.x, b.x) + epsilon < Math.Min(c.x, d.x) || Math.Max(c.x, d.x) + epsilon < Math.Min(a.x, b.x) ||
                    Math.Max(a.y, b.y) + epsilon < Math.Min(c.y, d.y) || Math.Max(c.y, d.y) + epsilon < Math.Min(a.y, b.y)) return false;
                double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
                return ((abC >= -epsilon && abD <= epsilon) || (abD >= -epsilon && abC <= epsilon)) &&
                    ((cdA >= -epsilon && cdB <= epsilon) || (cdB >= -epsilon && cdA <= epsilon));
            }
            static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
                ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

            void RequireLive() { if (disposed) throw new ObjectDisposedException(nameof(Snapshot)); }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                foreach (UnityEngine.Object value in owned) MeshResidentHeight.Destroy(value);
                owned.Clear(); masks.Clear(); items.Clear();
            }
        }

        static void ValidateEntry(Entry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.stableId) || entry.stableId != entry.stableId.Trim() ||
                entry.stableId.Length > 128) throw new InvalidDataException("Overlay needs a stable nonempty ID, at most 128 characters.");
            foreach (char c in entry.stableId) if (char.IsControl(c)) throw new InvalidDataException("Overlay stable ID contains a control character.");
            if (entry.materialId < 0 || entry.materialId >= MeshMaterialSet.Capacity || !Finite(entry.tint.r) ||
                !Finite(entry.tint.g) || !Finite(entry.tint.b) || !Finite(entry.tint.a) || entry.tint.r < 0 || entry.tint.g < 0 || entry.tint.b < 0 ||
                !Finite(entry.opacity) || entry.opacity < 0 || entry.opacity > 1 || !Finite(entry.uvMetres) ||
                entry.uvMetres.x <= 0 || entry.uvMetres.y <= 0 || !Finite(1 / entry.uvMetres.x) || !Finite(1 / entry.uvMetres.y) ||
                !Finite(entry.uvOffset) || !Finite(entry.maskUv) || entry.maskChannel < 0 || entry.maskChannel > 3 ||
                (entry.channels != MeshWorldVtOverlayChannels.Full && entry.channels != MeshWorldVtOverlayChannels.Albedo))
                throw new InvalidDataException("Overlay material, mask, UV, tint or opacity is invalid: " + entry.stableId);
            if (entry is Road road)
            {
                if (road.centreLine == null || road.centreLine.Length < 2 || !Finite(road.width) || road.width <= 0 ||
                    !Finite(road.feather) || road.feather < 0 || road.feather >= road.width * .5f)
                    throw new InvalidDataException("Road needs a centre line, positive width and feather smaller than half width.");
                foreach (Vector2 point in road.centreLine)
                    if (!Finite(point) || point.x < 0 || point.y < 0 || point.x > MeshControlContract.WorldSize || point.y > MeshControlContract.WorldSize)
                        throw new InvalidDataException("Road centre line must lie in the finite terrain XZ world.");
            }
            else if (entry is Decal box)
            {
                if (!Finite(box.position) || !Finite(box.rotationEuler) || !Finite(box.size) ||
                    box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0 || !Finite(box.feather) ||
                    box.feather < 0 || box.feather >= .5f * Mathf.Min(box.size.x, box.size.z))
                    throw new InvalidDataException("Decal needs a finite positive box and valid XZ feather.");
            }
            else throw new InvalidDataException("Unknown overlay type.");
        }

        static string WatchHash(MeshWorldVtOverlaySet set, Func<MeshWorldVtDecal[]> sceneDecals,
            Func<MeshWorldVtRoad[]> sceneRoads)
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                writer.Write(set.writerShader ? set.writerShader.name : "");
                writer.Write(set.repackShader ? set.repackShader.name : "");
                writer.Write(set.roads?.Length ?? -1);
                if (set.roads != null) foreach (Road road in set.roads) WriteEntry(writer, road, true, null);
                writer.Write(set.decals?.Length ?? -1);
                if (set.decals != null) foreach (Decal decal in set.decals) WriteEntry(writer, decal, true, null);
                MeshWorldVtDecal[] components = sceneDecals == null ? Array.Empty<MeshWorldVtDecal>() : sceneDecals();
                if (components == null) throw new InvalidDataException("Scene decal provider returned null.");
                writer.Write(components.Length);
                foreach (MeshWorldVtDecal component in components)
                {
                    if (!component) throw new InvalidDataException("A scene decal was destroyed.");
                    // 对象身份只参与运行期变更检测，不进入持久内容hash。
                    writer.Write(component.GetInstanceID());
                    WriteEntry(writer, component.Capture(), true, null);
                }
                MeshWorldVtRoad[] roads = sceneRoads == null ? Array.Empty<MeshWorldVtRoad>() : sceneRoads();
                if (roads == null) throw new InvalidDataException("Scene road provider returned null.");
                writer.Write(roads.Length);
                foreach (MeshWorldVtRoad component in roads)
                {
                    if (!component) throw new InvalidDataException("A scene road was destroyed.");
                    // 身份仅用于 watch，材质内容 hash 仍只记录 Capture 得到的有效道路数据。
                    writer.Write(component.GetInstanceID());
                    WriteEntry(writer, component.Capture(), true, null);
                }
                writer.Flush();
                return MeshMaterialResident.Sha256(bytes.ToArray());
            }
        }

        internal static void WriteEntry(BinaryWriter writer, Entry entry, bool watch, string maskHash)
        {
            writer.Write(entry != null);
            if (entry == null) return;
            writer.Write(entry is Road ? 0 : 1);
            writer.Write(entry.stableId ?? ""); writer.Write(entry.order); writer.Write(entry.materialId);
            writer.Write(entry.tint.r); writer.Write(entry.tint.g); writer.Write(entry.tint.b);
            if (watch) writer.Write(entry.tint.a); // Alpha is ignored by shading, but watch invalid source edits too.
            writer.Write(entry.opacity); Write(writer, entry.uvMetres); Write(writer, entry.uvOffset);
            writer.Write((int)entry.channels); writer.Write(entry.maskChannel); Write(writer, entry.maskUv);
            Texture2D mask = entry.maskTexture;
            writer.Write(mask != null);
            if (mask)
            {
                writer.Write(mask.width); writer.Write(mask.height); writer.Write((int)mask.format);
                writer.Write((int)mask.graphicsFormat); writer.Write(mask.mipmapCount);
                writer.Write((int)mask.wrapModeU); writer.Write((int)mask.wrapModeV);
                if (watch) { writer.Write(mask.GetInstanceID()); writer.Write((long)mask.updateCount); writer.Write(mask.isReadable); }
                else writer.Write(maskHash);
            }
            if (entry is Road road)
            {
                writer.Write(road.width); writer.Write(road.feather); writer.Write(road.centreLine?.Length ?? -1);
                if (road.centreLine != null) foreach (Vector2 point in road.centreLine) Write(writer, point);
            }
            else if (entry is Decal box)
            {
                Write(writer, box.position); Write(writer, box.rotationEuler); Write(writer, box.size); writer.Write(box.feather);
            }
        }

        static void Write(BinaryWriter writer, Vector2 value) { writer.Write(value.x); writer.Write(value.y); }
        static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        static void Write(BinaryWriter writer, Vector4 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }
}
