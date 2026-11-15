using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaxTerrain
{
    /// <summary>
    /// 每个相机自己的静态表面覆盖预览输入，供 Direct 与 Delta 直接着色/缺页回退使用。
    /// Delta 命中 VT 时已经包含静态覆盖，不再执行此合成器。这里不提交 VT 工作。
    /// Demo 的 Direct baseline 优先精度：原始 mask mip 保存成 float4 Buffer，
    /// 不统一尺寸、不重采样，也不经过页面 RGBA8/BC3 中间量化。
    /// </summary>
    public sealed class MeshSurfaceOverlays : IDisposable
    {
        const int MaximumMaskPixels = 4 * 1024 * 1024; // float4 64 MiB hard limit, separate from VT pool.
        [StructLayout(LayoutKind.Sequential)]
        struct GpuEntry
        {
            public Vector4 row0, row1, row2, shape, uv, maskUv, tintOpacity;
            public Vector4 info, flags, uv0, uv1, uv2, tangent0, tangent1, tangent2, bounds, maskInfo;
        }
        readonly MeshWorldVtOverlaySet staticSet;
        readonly Func<MeshWorldVtDecal[]> staticProvider;
        readonly Func<MeshWorldVtRoad[]> roadProvider;
        readonly bool immutable;
        readonly List<MeshWorldVtOverlaySet.Entry> inputs = new List<MeshWorldVtOverlaySet.Entry>();
        readonly Dictionary<int, Vector4> maskInfos = new Dictionary<int, Vector4>();
        ComputeBuffer entries, maskPixels;
        string inputStamp, maskStamp;
        bool disposed, prepared;
        public int Count { get; private set; }
        /// <summary>输入扫描次数；发布态成功初始化后不再扫描。</summary>
        public int PrepareCount { get; private set; }
        /// <summary>内容变化后执行排序的次数；不变的制作预览帧不排序。</summary>
        public int SortCount { get; private set; }
        /// <summary>描述缓冲发布次数，包含空输入的有效占位缓冲。</summary>
        public int UploadCount { get; private set; }
        public int MaskUploadCount { get; private set; }
        public long GpuBytes => (entries == null ? 0L : (long)entries.count * entries.stride) +
            (maskPixels == null ? 0L : (long)maskPixels.count * maskPixels.stride);

        public MeshSurfaceOverlays(MeshWorldVtOverlaySet staticSet = null,
            Func<MeshWorldVtDecal[]> staticProvider = null, Func<MeshWorldVtRoad[]> roadProvider = null,
            bool immutable = false)
        {
            this.staticSet = staticSet;
            this.staticProvider = staticProvider;
            this.roadProvider = roadProvider;
            this.immutable = immutable;
        }

        /// <summary>相机准备时更新并绑定自己的 Material；不设置全局 Shader 状态。</summary>
        public void Bind(Material target)
        {
            if (disposed) throw new ObjectDisposedException(nameof(MeshSurfaceOverlays));
            if (!target) throw new ArgumentNullException(nameof(target));
            // 发布记录在创建本实例后固定。保留同一批静态预览/回退 Buffer，
            // 后续相机帧只绑定，不再扫描对象、读取 mask 状态或排序。
            if (!prepared || !immutable) Prepare();
            if (Count == 0)
            {
                target.DisableKeyword("DD_LIVE_OVERLAYS");
                target.SetInt("_DDLiveOverlayCount", 0);
                return;
            }
            target.EnableKeyword("DD_LIVE_OVERLAYS");
            target.SetBuffer("_DDLiveOverlays", entries);
            target.SetBuffer("_DDLiveMaskPixels", maskPixels);
            target.SetInt("_DDLiveOverlayCount", Count);
        }

        void Prepare()
        {
            PrepareCount++;
            inputs.Clear();
            string newInputStamp = CaptureInputs();
            if (prepared && inputStamp == newInputStamp) return;
            // 只有输入变化才复用 writer 的完整验证（道路折叠/自交、ID、mask 契约）。
            using (MeshWorldVtOverlaySet.CreateSnapshot(staticSet, staticProvider, roadProvider)) { }
            inputs.Sort((a, b) =>
            {
                if (a.order != b.order) return a.order.CompareTo(b.order);
                return StringComparer.Ordinal.Compare(a.stableId, b.stableId);
            });
            SortCount++;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (MeshWorldVtOverlaySet.Entry input in inputs)
            {
                Validate(input);
                if (!ids.Add(input.stableId))
                    throw new InvalidDataException("Surface overlay stable IDs must be unique: " + input.stableId);
            }
            // 以下步骤可能改变 GPU 资源。失败后必须重新准备，即使 Undo 恢复到了
            // 上一次有效指纹，也不能误用一次失败上传留下的中间状态。
            prepared = false;
            UpdateMasks();
            var data = new List<GpuEntry>();
            int group = 0;
            foreach (MeshWorldVtOverlaySet.Entry input in inputs)
            {
                GpuEntry common = Common(input, ++group);
                if (input is MeshWorldVtOverlaySet.Road road) BuildRoad(road, common, data);
                else BuildBox((MeshWorldVtOverlaySet.Decal)input, common, data);
            }
            int stride = Marshal.SizeOf(typeof(GpuEntry));
            if (entries == null || entries.count != Math.Max(data.Count, 1))
            {
                entries?.Release();
                entries = new ComputeBuffer(Math.Max(data.Count, 1), stride, ComputeBufferType.Structured);
                entries.name = "Terrain live overlay descriptions";
            }
            if (data.Count != 0) entries.SetData(data);
            Count = data.Count;
            inputStamp = newInputStamp;
            UploadCount++;
            prepared = true;
        }

        string CaptureInputs()
        {
            using (var bytes = new MemoryStream())
            using (var stamp = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                stamp.Write(staticSet ? staticSet.GetInstanceID() : 0);
                if (staticSet)
                {
                    if (staticSet.roads == null || staticSet.decals == null)
                        throw new InvalidDataException("Surface overlay arrays cannot be null.");
                    stamp.Write(staticSet.writerShader ? staticSet.writerShader.GetInstanceID() : 0);
                    stamp.Write(staticSet.repackShader ? staticSet.repackShader.GetInstanceID() : 0);
                    stamp.Write(staticSet.roads.Length);
                    foreach (var road in staticSet.roads) Add(road, stamp);
                    stamp.Write(staticSet.decals.Length);
                    foreach (var decal in staticSet.decals) Add(decal, stamp);
                }
                Capture(stamp);
                CaptureRoads(stamp);
                stamp.Flush();
                // 仅作本实例逐帧变化比较，不进入存档身份。复用 writer 的二进制字段编码，
                // 保留完整浮点位型及 mask updateCount，非法值不会被 JSON 舍入成有效旧值。
                return Convert.ToBase64String(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            }
        }

        void Capture(BinaryWriter stamp)
        {
            if (staticProvider == null) { stamp.Write(0); return; }
            MeshWorldVtDecal[] components = staticProvider();
            if (components == null) throw new InvalidDataException("Surface decal provider returned null.");
            stamp.Write(components.Length);
            foreach (MeshWorldVtDecal component in components)
            {
                if (!component) continue;
                Add(component.Capture(), stamp);
            }
        }

        void Add(MeshWorldVtOverlaySet.Entry entry, BinaryWriter stamp)
        {
            if (entry == null) throw new InvalidDataException("Surface overlay entries cannot be null.");
            MeshWorldVtOverlaySet.WriteEntry(stamp, entry, true, null);
            inputs.Add(entry);
        }

        void CaptureRoads(BinaryWriter stamp)
        {
            if (roadProvider == null) { stamp.Write(0); return; }
            MeshWorldVtRoad[] components = roadProvider();
            if (components == null) throw new InvalidDataException("Surface road provider returned null.");
            stamp.Write(components.Length);
            foreach (MeshWorldVtRoad component in components)
            {
                if (!component) continue;
                // 与资产道路共用 Road DTO，仍按全局 order/stableId 排序。
                // 旋转/拖动/缩放只更新描述 Buffer，不引入单独的道路 shader。
                Add(component.Capture(), stamp);
            }
        }

        static void Validate(MeshWorldVtOverlaySet.Entry e)
        {
            if (string.IsNullOrWhiteSpace(e.stableId) || e.materialId < 0 || e.materialId >= 16 ||
                !Finite(e.opacity) || e.opacity < 0 || e.opacity > 1 ||
                !Finite(e.uvMetres.x) || !Finite(e.uvMetres.y) || e.uvMetres.x <= 0 || e.uvMetres.y <= 0 ||
                !Finite(1 / e.uvMetres.x) || !Finite(1 / e.uvMetres.y) ||
                !Finite(e.uvOffset.x) || !Finite(e.uvOffset.y) ||
                !Finite(e.tint.r) || !Finite(e.tint.g) || !Finite(e.tint.b) ||
                e.tint.r < 0 || e.tint.g < 0 || e.tint.b < 0 ||
                e.maskChannel < 0 || e.maskChannel > 3 ||
                (e.channels != MeshWorldVtOverlayChannels.Albedo && e.channels != MeshWorldVtOverlayChannels.Full))
                throw new InvalidDataException("Invalid surface overlay parameters: " + e.stableId);
            for (int c = 0; c < 4; c++)
                if (!Finite(e.maskUv[c])) throw new InvalidDataException("Non-finite mask UV: " + e.stableId);
            if (e is MeshWorldVtOverlaySet.Decal box &&
                (!Finite(box.position.x) || !Finite(box.position.y) || !Finite(box.position.z) ||
                 !Finite(box.rotationEuler.x) || !Finite(box.rotationEuler.y) || !Finite(box.rotationEuler.z) ||
                 !Finite(box.size.x) || !Finite(box.size.y) || !Finite(box.size.z) ||
                 box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0 || !Finite(box.feather) || box.feather < 0 ||
                 box.feather >= .5f * Mathf.Min(box.size.x, box.size.z)))
                throw new InvalidDataException("Invalid live projection box: " + e.stableId);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        void UpdateMasks()
        {
            var unique = new List<Texture2D>();
            var ids = new HashSet<int>();
            var stamp = new StringBuilder();
            foreach (MeshWorldVtOverlaySet.Entry input in inputs)
            {
                Texture2D texture = input.maskTexture;
                if (!texture || !ids.Add(texture.GetInstanceID())) continue;
                if (!texture.isReadable || GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat) ||
                    GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) || texture.width > 1024 || texture.height > 1024 ||
                    texture.mipmapCount != MeshMaterialSet.CountMips(Math.Max(texture.width, texture.height)))
                    throw new InvalidDataException("Live masks must be readable linear uncompressed textures with full mips, at most 1024 per side.");
                unique.Add(texture);
                stamp.Append(texture.GetInstanceID()).Append(':').Append(texture.updateCount).Append(':')
                    .Append(texture.width).Append('x').Append(texture.height).Append(':').Append(texture.mipmapCount).Append(':')
                    .Append((int)texture.graphicsFormat).Append(':').Append(texture.isReadable).Append(':')
                    .Append((int)texture.wrapModeU).Append(',').Append((int)texture.wrapModeV).Append(';');
            }
            string newStamp = stamp.ToString();
            if (maskPixels != null && maskStamp == newStamp) return;
            maskStamp = null;
            maskInfos.Clear();
            var pixels = new List<Vector4>();
            foreach (Texture2D texture in unique)
            {
                maskInfos.Add(texture.GetInstanceID(), new Vector4(texture.width, texture.height, texture.mipmapCount, pixels.Count));
                for (int mip = 0; mip < texture.mipmapCount; mip++)
                {
                    int mipPixels = Math.Max(texture.width >> mip, 1) * Math.Max(texture.height >> mip, 1);
                    if (pixels.Count + mipPixels > MaximumMaskPixels)
                        throw new InvalidDataException("Live mask float-pixel buffer exceeds 64 MiB. Use fewer/smaller local masks.");
                    foreach (Color color in texture.GetPixels(mip)) pixels.Add(new Vector4(color.r, color.g, color.b, color.a));
                }
            }
            // 空 mask 也绑定一个有效 SRV。shader 通过 width=0 返回不透明，不读这个元素。
            if (pixels.Count == 0) pixels.Add(Vector4.one);
            maskPixels?.Release();
            maskPixels = new ComputeBuffer(pixels.Count, 16, ComputeBufferType.Structured) { name = "Terrain live mask original mip pixels" };
            maskPixels.SetData(pixels);
            maskStamp = newStamp;
            MaskUploadCount++;
        }

        GpuEntry Common(MeshWorldVtOverlaySet.Entry e, int group)
        {
            Texture2D mask = e.maskTexture;
            return new GpuEntry
            {
                uv = new Vector4(1 / e.uvMetres.x, 1 / e.uvMetres.y, e.uvOffset.x, e.uvOffset.y),
                maskUv = e.maskUv,
                tintOpacity = new Vector4(e.tint.r, e.tint.g, e.tint.b, e.opacity),
                info = new Vector4(0, e.materialId, e.maskChannel, group),
                // 与静态 writer 共用最近 mip 的双线性遮罩过滤。
                flags = new Vector4(e.channels == MeshWorldVtOverlayChannels.Full ? 1 : 0,
                    mask ? (int)mask.wrapModeU : 1, mask ? (int)mask.wrapModeV : 1, 0),
                maskInfo = mask ? maskInfos[mask.GetInstanceID()] : Vector4.zero
            };
        }

        static void BuildBox(MeshWorldVtOverlaySet.Decal box, GpuEntry value, List<GpuEntry> output)
        {
            Matrix4x4 transform = Matrix4x4.TRS(box.position, Quaternion.Euler(box.rotationEuler), box.size);
            Matrix4x4 inverse = transform.inverse;
            if (!Finite(transform.determinant) || Mathf.Abs(transform.determinant) < 1e-12f)
                throw new InvalidDataException("Live decal transform is singular: " + box.stableId);
            value.row0 = inverse.GetRow(0); value.row1 = inverse.GetRow(1); value.row2 = inverse.GetRow(2);
            value.shape = new Vector4(box.size.x, box.size.y, box.size.z, box.feather);
            float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = transform.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -.5f : .5f,
                    (i & 2) == 0 ? -.5f : .5f, (i & 4) == 0 ? -.5f : .5f));
                minX = Mathf.Min(minX, corner.x); maxX = Mathf.Max(maxX, corner.x);
                minZ = Mathf.Min(minZ, corner.z); maxZ = Mathf.Max(maxZ, corner.z);
            }
            value.bounds = new Vector4(minX, minZ, maxX, maxZ);
            output.Add(value);
        }

        static void BuildRoad(MeshWorldVtOverlaySet.Road road, GpuEntry common, List<GpuEntry> output)
        {
            // 与冻结 writer 相同的 miter<=4 三角带。每条道路的三角片共享 group；
            // shader 每位置只接受其中一个三角片，避免共边被混合两次。
            int count = road.centreLine.Length;
            var direction = new Vector2[count - 1];
            var distance = new float[count];
            for (int i = 0; i < count - 1; i++)
            {
                Vector2 delta = road.centreLine[i + 1] - road.centreLine[i];
                direction[i] = delta.normalized;
                distance[i + 1] = distance[i] + delta.magnitude;
            }
            var vertices = new Vector2[count * 2];
            var uv = new Vector2[count * 2];
            var tangent = new Vector4[count * 2];
            float half = road.width * .5f;
            for (int i = 0; i < count; i++)
            {
                Vector2 previous = direction[Math.Max(i - 1, 0)], next = direction[Math.Min(i, count - 2)];
                Vector2 right0 = new Vector2(previous.y, -previous.x), right1 = new Vector2(next.y, -next.x);
                Vector2 miter = (right0 + right1).normalized;
                Vector2 offset = miter * (half / Vector2.Dot(miter, right1));
                vertices[2 * i] = road.centreLine[i] - offset;
                vertices[2 * i + 1] = road.centreLine[i] + offset;
                uv[2 * i] = new Vector2(-half, distance[i]);
                uv[2 * i + 1] = new Vector2(half, distance[i]);
                tangent[2 * i] = tangent[2 * i + 1] = new Vector4(miter.x, 0, miter.y, 1);
            }
            for (int i = 0; i < count - 1; i++)
            {
                int a = i * 2;
                Triangle(a, a + 2, a + 1);
                Triangle(a + 1, a + 2, a + 3);
            }
            void Triangle(int a, int b, int c)
            {
                GpuEntry value = common;
                value.info.x = 1;
                Vector2 origin = vertices[a], ab = vertices[b] - origin, ac = vertices[c] - origin;
                float determinant = ab.x * ac.y - ac.x * ab.y;
                value.row0 = new Vector4(ac.y / determinant, -ac.x / determinant, 0, 0);
                value.row1 = new Vector4(-ab.y / determinant, ab.x / determinant, 0, 0);
                value.row2 = new Vector4(origin.x, origin.y, 0, 0);
                value.uv0 = new Vector4(uv[a].x, uv[a].y, 0, 0);
                value.uv1 = new Vector4(uv[b].x, uv[b].y, 0, 0);
                value.uv2 = new Vector4(uv[c].x, uv[c].y, 0, 0);
                value.tangent0 = tangent[a]; value.tangent1 = tangent[b]; value.tangent2 = tangent[c];
                value.shape = new Vector4(half, 0, 0, road.feather);
                Vector2 low = Vector2.Min(vertices[a], Vector2.Min(vertices[b], vertices[c]));
                Vector2 high = Vector2.Max(vertices[a], Vector2.Max(vertices[b], vertices[c]));
                value.bounds = new Vector4(low.x, low.y, high.x, high.y);
                output.Add(value);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            entries?.Release(); entries = null;
            maskPixels?.Release(); maskPixels = null;
            Count = 0;
        }
    }
}
