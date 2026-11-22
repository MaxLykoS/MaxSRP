using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// 地形静态贴花的作者对象，Transform 定义完整世界投影盒。
    /// DirectDelta 制作场景实时预览，发布时冻结为 RVT/SVT 的地图内容。
    /// 本组件不注册普通 Renderer，也不提供运行时移动、发光或生命周期效果。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("DirectDelta Mesh/VT Decal")]
    public sealed class MeshWorldVtDecal : MonoBehaviour
    {
        [Tooltip("接收该贴花的地形。只作用于同一场景中这个对照组的 DirectDelta/Delta。")]
        public MeshComparison targetTerrain;
        [Tooltip("所属地形内唯一的贴花 ID。复制对象后需分配新 ID；该值参与静态内容身份。")]
        public string stableId = Guid.NewGuid().ToString("N");
        [Tooltip("与道路共用的覆盖顺序；较大的值后画。顺序相同时按稳定 ID 排序。")]
        public int order = 100;
        [Range(0, 63), Tooltip("现有 Surface16 材质源中的稳定 ID。")]
        public int materialId;
        [ColorUsage(false, true)]
        [Tooltip("线性 RGB 颜色乘数，允许大于 1。Alpha 不参与覆盖，透明度由下方字段控制。")]
        public Color tint = Color.white;
        [Range(0, 1), Tooltip("整体透明度，会与遮罩及边缘渐隐相乘。")]
        public float opacity = 1;
        [Tooltip("贴花盒本地 X/Z 方向每次纹理重复对应的世界米数。")]
        public Vector2 uvMetres = Vector2.one;
        [Tooltip("材质纹理的 UV 偏移；不改变投影盒的位置。")]
        public Vector2 uvOffset;
        [Tooltip("线性、可读、非压缩且带完整 mip 的局部透明度图；空值表示整个盒内允许覆盖。")]
        public Texture2D maskTexture;
        [Range(0, 3), Tooltip("透明度来源通道：0=R、1=G、2=B、3=A；R8 遮罩应选 0。")]
        public int maskChannel;
        [Tooltip("遮罩 UV 的 X/Y 缩放和 Z/W 偏移。")]
        public Vector4 maskUv = new Vector4(1, 1, 0, 0);
        [Tooltip("Albedo 只改底色；Full 同时覆盖底色、法线、AO 和粗糙度。")]
        public MeshWorldVtOverlayChannels channels = MeshWorldVtOverlayChannels.Full;
        [Tooltip("从投影盒 X/Z 外缘向内渐隐的世界米数。Y 尺寸只限制投影深度。")]
        public float feather = .1f;

        // ExecuteAlways 使加载场景和 batch Editor 都走同一注册路径。
        // 不在进入 Play 时清空：禁用 Domain/Scene Reload 时既有对象可能不会重发 OnEnable。
        // 失效对象在收集时剔除，不需要每帧扫描所有场景。
        // 仅在对象注册时维护实例顺序；逐帧收集仍检查当前归属，避免 targetTerrain
        // 或 Scene 改变后缓存旧成员，也不再为完全不变的作者对象反复排序。
        static readonly List<MeshWorldVtDecal> active = new List<MeshWorldVtDecal>();
        static readonly IComparer<MeshWorldVtDecal> activeOrder = Comparer<MeshWorldVtDecal>.Create(
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
        }

        /// <summary>只收集当前地形的静态作者输入，供制作预览和地图烘焙共用。</summary>
        public static MeshWorldVtDecal[] Collect(MeshComparison target)
        {
            if (!target) return Array.Empty<MeshWorldVtDecal>();
            active.RemoveAll(item => !item);
            var result = new List<MeshWorldVtDecal>();
            foreach (MeshWorldVtDecal item in active)
                if (item.isActiveAndEnabled && item.targetTerrain == target &&
                    item.gameObject.scene == target.gameObject.scene)
                    result.Add(item);
            // 注册表已按实例 ID 排好；此顺序只稳定 watch，不进入持久材质配方。
            return result.ToArray();
        }

        /// <summary>
        /// 将作者对象转换为现有写页契约，不改写原资产。位置保留原浮点值；旋转和完整盒
        /// 尺寸规范到 1e-4 度/米，消除 Unity Euler/轴长往返噪声，不让它误触发旧归档失配。
        /// </summary>
        public MeshWorldVtOverlaySet.Decal Capture()
        {
            ReadBox(out Vector3 position, out Vector3 rotation, out Vector3 size);
            return new MeshWorldVtOverlaySet.Decal
            {
                stableId = stableId, order = order, materialId = materialId, tint = tint,
                opacity = opacity, uvMetres = uvMetres, uvOffset = uvOffset,
                maskTexture = maskTexture, maskChannel = maskChannel, maskUv = maskUv,
                channels = channels, feather = feather,
                position = position, rotationEuler = rotation, size = size
            };
        }

        /// <summary>由旧作者记录迁移到独立对象。迁移工具创建不带父级的根对象后调用。</summary>
        public void ConfigureFrom(MeshWorldVtOverlaySet.Decal value, MeshComparison target)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (transform.parent)
                throw new InvalidOperationException("Configure a migrated VT decal as a root GameObject, then parent it if needed.");
            targetTerrain = target;
            stableId = value.stableId;
            order = value.order;
            materialId = value.materialId;
            tint = value.tint;
            opacity = value.opacity;
            uvMetres = value.uvMetres;
            uvOffset = value.uvOffset;
            maskTexture = value.maskTexture;
            maskChannel = value.maskChannel;
            maskUv = value.maskUv;
            channels = value.channels;
            feather = value.feather;
            transform.SetPositionAndRotation(value.position, Quaternion.Euler(value.rotationEuler));
            transform.localScale = value.size;
        }

        [ContextMenu("生成新的贴花 ID")]
        public void RegenerateStableId()
        {
            stableId = Guid.NewGuid().ToString("N");
        }

        void ReadBox(out Vector3 position, out Vector3 rotation, out Vector3 size)
        {
            // 原写页器描述的是 TRS 盒，不接受镜像或剪切。父级非均匀缩放后再旋转子级
            // 可能产生 shear；仅取 lossyScale 会把这种形变悄悄解释成错误的投影盒。
            for (Transform ancestor = transform; ancestor; ancestor = ancestor.parent)
            {
                Vector3 scale = ancestor.localScale;
                if (!Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0)
                    throw InvalidBox("自身及父级的缩放必须为有限正数");
            }
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            size = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            position = transform.position;
            if (!Finite(position) || !Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0)
                throw InvalidBox("投影盒位置或尺寸无效");
            x /= size.x; y /= size.y; z /= size.z;
            const float orthogonalTolerance = 1e-5f;
            if (Mathf.Abs(Vector3.Dot(x, y)) > orthogonalTolerance ||
                Mathf.Abs(Vector3.Dot(x, z)) > orthogonalTolerance ||
                Mathf.Abs(Vector3.Dot(y, z)) > orthogonalTolerance)
                throw InvalidBox("父级缩放与旋转产生剪切；请使用独立根对象或调整父级变换");
            rotation = Quaternion.LookRotation(z, y).eulerAngles;
            rotation = new Vector3(CanonicalAngle(rotation.x), CanonicalAngle(rotation.y), CanonicalAngle(rotation.z));
            size = new Vector3(Canonical(size.x), Canonical(size.y), Canonical(size.z));
            if (!Finite(rotation) || !Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0)
                throw InvalidBox("投影盒尺寸低于静态作者精度或变换无效");
        }

        InvalidDataException InvalidBox(string reason) =>
            new InvalidDataException("VT 贴花「" + name + "」：" + reason + "。");
        static float Canonical(float value) => (float)Math.Round((double)value, 4, MidpointRounding.AwayFromZero);
        static float CanonicalAngle(float angle)
        {
            float result = Canonical(Mathf.Repeat(angle, 360));
            return result == 0 || result >= 360 ? 0 : result;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        void OnDrawGizmosSelected()
        {
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;
            try
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = new Color(0, .8f, 1, .04f);
                Gizmos.DrawCube(Vector3.zero, Vector3.one);
                Gizmos.color = isActiveAndEnabled ? new Color(0, .8f, 1, 1) : Color.gray;
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                // 盒子的局部 -Y 是投影深度方向，+Z 是遮罩的上方。
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(new Vector3(0, .25f, 0), new Vector3(0, -.25f, 0));
                Gizmos.DrawLine(new Vector3(0, -.25f, 0), new Vector3(.06f, -.15f, 0));
                Gizmos.DrawLine(new Vector3(0, -.25f, 0), new Vector3(-.06f, -.15f, 0));
                Gizmos.color = Color.blue;
                Gizmos.DrawLine(Vector3.zero, new Vector3(0, 0, .45f));
            }
            finally
            {
                Gizmos.matrix = previousMatrix;
                Gizmos.color = previousColor;
            }
        }
    }
}
