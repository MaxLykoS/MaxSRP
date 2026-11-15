using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// 一条可编辑的长方形道路。Transform 的世界 X 轴长度是宽度，Z 轴长度是长度，
    /// 水平旋转决定走向。Y 只决定编辑框的位置；道路始终覆盖源地形，不改几何高度。
    /// Capture 把矩形转换成现有两点 Road 契约，Direct/RVT/SVT 继续共用原覆盖算法。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("DirectDelta Mesh/Rectangle Road")]
    public sealed class MeshWorldVtRoad : MonoBehaviour
    {
        [Tooltip("接收道路的地形；必须在同一个场景。制作场景可编辑，发布场景读取烘焙副本。")]
        public MeshComparison targetTerrain;
        [Tooltip("同一地形内与其他静态覆盖唯一的 ID。复制静态道路后请生成新 ID。")]
        public string stableId = Guid.NewGuid().ToString("N");
        [Tooltip("与静态贴花共用覆盖顺序；较大的值后画。")]
        public int order;
        [Range(0, 15)] public int materialId = 3;
        [ColorUsage(false, true), Tooltip("线性底色乘数，允许大于 1；Alpha 不参与覆盖。")]
        public Color tint = Color.white;
        [Range(0, 1)] public float opacity = 1;
        [Tooltip("道路横向/沿长度方向每次纹理重复的米数。")]
        public Vector2 uvMetres = new Vector2(3, 3);
        public Vector2 uvOffset;
        [Tooltip("可选线性透明度纹理；需可读、未压缩、有完整 mip。")]
        public Texture2D maskTexture;
        [Range(0, 3)] public int maskChannel;
        [Tooltip("遮罩 UV 缩放 XY、偏移 ZW。沿路 UV 使用从起点累计的米数。")]
        public Vector4 maskUv = new Vector4(1, 1, 0, 0);
        public MeshWorldVtOverlayChannels channels = MeshWorldVtOverlayChannels.Full;
        [Min(0), Tooltip("道路左右两侧向内渐隐的米数，必须小于半宽；端部保持矩形边界。")]
        public float feather = .5f;

        // 与贴花使用相同的显式地形/场景归属。禁用 Domain Reload 时不清空注册表；
        // 收集时移除已经销毁的 Unity 引用，不逐帧扫描整个场景。
        // 注册时保持稳定实例顺序，Collect 只过滤当前归属，不逐帧重新排序。
        static readonly List<MeshWorldVtRoad> active = new List<MeshWorldVtRoad>();
        static readonly IComparer<MeshWorldVtRoad> activeOrder = Comparer<MeshWorldVtRoad>.Create(
            (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        void OnEnable()
        {
            active.RemoveAll(item => !item);
            int index = active.BinarySearch(this, activeOrder);
            if (index < 0) active.Insert(~index, this);
        }
        void OnDisable() => active.Remove(this);
        void OnDestroy() => active.Remove(this);
        void Reset()
        {
            stableId = Guid.NewGuid().ToString("N");
            targetTerrain = GetComponentInParent<MeshComparison>();
            transform.localScale = new Vector3(10, 1, 100);
        }

        /// <summary>只收集同一场景、同一所属地形中的启用道路。</summary>
        public static MeshWorldVtRoad[] Collect(MeshComparison target)
        {
            if (!target) return Array.Empty<MeshWorldVtRoad>();
            active.RemoveAll(item => !item);
            var result = new List<MeshWorldVtRoad>();
            foreach (MeshWorldVtRoad item in active)
                if (item.isActiveAndEnabled && item.targetTerrain == target &&
                    item.gameObject.scene == target.gameObject.scene)
                    result.Add(item);
            return result.ToArray();
        }

        /// <summary>保持原 Road 数据结构；两点分别对应矩形局部 -Z/+Z 的中点。</summary>
        public MeshWorldVtOverlaySet.Road Capture()
        {
            ReadRectangle(out Vector3 centre, out _, out Vector3 forward, out float width, out float length);
            Vector3 start = centre - forward * (length * .5f);
            Vector3 end = centre + forward * (length * .5f);
            // 端点保留 float 世界坐标，不把整个世界量化到作者尺寸精度；
            // Y 不写入 Road，因此移动编辑框高度不会改变地图内容。
            return new MeshWorldVtOverlaySet.Road
            {
                stableId = stableId, order = order, materialId = materialId, tint = tint,
                opacity = opacity, uvMetres = uvMetres, uvOffset = uvOffset,
                maskTexture = maskTexture, maskChannel = maskChannel, maskUv = maskUv,
                channels = channels, feather = feather, width = width,
                centreLine = new[] { new Vector2(start.x, start.z), new Vector2(end.x, end.z) }
            };
        }

        /// <summary>
        /// 读取真实世界矩形，拒绝镜像、剪切或倾斜，防止非均匀父缩放把长方形变成平行四边形。
        /// 世界宽长和 yaw 规范到 1e-4 米/度，只消除 Transform 往返噪声。
        /// </summary>
        public void ReadRectangle(out Vector3 centre, out Vector3 right, out Vector3 forward,
            out float width, out float length)
        {
            for (Transform ancestor = transform; ancestor; ancestor = ancestor.parent)
            {
                Vector3 scale = ancestor.localScale;
                if (!Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0)
                    throw Invalid("自身和父级的缩放必须为有限正数");
            }
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            float rawWidth = x.magnitude, rawHeight = y.magnitude, rawLength = z.magnitude;
            centre = transform.position;
            if (!Finite(centre) || !Finite(rawWidth) || !Finite(rawHeight) || !Finite(rawLength) ||
                rawWidth <= 0 || rawHeight <= 0 || rawLength <= 0)
                throw Invalid("位置或尺寸无效");
            x /= rawWidth; y /= rawHeight; z /= rawLength;
            const float tolerance = 1e-5f;
            if (Mathf.Abs(Vector3.Dot(x, y)) > tolerance || Mathf.Abs(Vector3.Dot(x, z)) > tolerance ||
                Mathf.Abs(Vector3.Dot(y, z)) > tolerance)
                throw Invalid("父级缩放与旋转产生剪切，请使用独立根对象或调整父级变换");
            if (Mathf.Abs(x.y) > tolerance || Mathf.Abs(z.y) > tolerance ||
                y.y < 1 - tolerance || Vector3.Dot(Vector3.Cross(z, x), Vector3.up) < 1 - tolerance)
                throw Invalid("道路覆盖框只能绕世界 Y 轴旋转；请将俯仰和侧倾复位");

            float yaw = Canonical(Mathf.Repeat(Mathf.Atan2(z.x, z.z) * Mathf.Rad2Deg, 360));
            if (yaw >= 360) yaw = 0;
            Quaternion rotation = Quaternion.Euler(0, yaw, 0);
            right = rotation * Vector3.right;
            forward = rotation * Vector3.forward;
            width = Canonical(rawWidth); length = Canonical(rawLength);
            if (width <= 0 || length <= 0) throw Invalid("尺寸低于 1e-4 米作者精度");
        }

        /// <summary>用世界米数调整宽长；保持中心、方向和 Y scale，支持无剪切的父级缩放。</summary>
        public void SetWorldSize(float width, float length)
        {
            if (!Finite(width) || !Finite(length) || width <= 0 || length <= 0)
                throw Invalid("宽度和长度必须为有限正数");
            ReadRectangle(out _, out _, out _, out _, out _);
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 x = matrix.GetColumn(0), z = matrix.GetColumn(2);
            Vector3 scale = transform.localScale;
            scale.x *= width / x.magnitude;
            scale.z *= length / z.magnitude;
            transform.localScale = scale;
        }

        [ContextMenu("生成新的道路 ID")]
        public void RegenerateStableId() => stableId = Guid.NewGuid().ToString("N");

        InvalidDataException Invalid(string reason) => new InvalidDataException("矩形道路「" + name + "」：" + reason + "。");
        static float Canonical(float value) => (float)Math.Round((double)value, 4, MidpointRounding.AwayFromZero);
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        void OnDrawGizmosSelected()
        {
            try
            {
                ReadRectangle(out Vector3 c, out Vector3 r, out Vector3 f, out float w, out float l);
                Color previous = Gizmos.color;
                Vector3 a = c - r * (w * .5f) - f * (l * .5f), b = c + r * (w * .5f) - f * (l * .5f);
                Vector3 d = c - r * (w * .5f) + f * (l * .5f), e = c + r * (w * .5f) + f * (l * .5f);
                Gizmos.color = isActiveAndEnabled ? new Color(1, .65f, .05f, 1) : Color.gray;
                Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, e); Gizmos.DrawLine(e, d); Gizmos.DrawLine(d, a);
                Gizmos.DrawLine(c - f * (l * .5f), c + f * (l * .5f));
                Gizmos.color = previous;
            }
            catch (InvalidDataException) { } // Inspector 显示具体错误；Gizmo 不向 Console 逐帧刷异常。
        }
    }
}
