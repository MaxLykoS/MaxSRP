using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    /// <summary>
    /// 渲染扩展负责记录几何和材质命令。
    /// MaxSRP 负责相机矩阵、渲染目标和各通道的执行顺序。
    /// 扩展包通过这些接口接入，管线无需引用扩展的具体实现。
    /// </summary>
    public interface IMaxRenderParticipant
    {
        // 判断此参与者是否为指定相机提供几何。
        bool RendersCamera(Camera camera);
        // 在绘制阴影和 GBuffer 前，准备与相机相关的几何和资源。
        void PrepareCamera(ScriptableRenderContext context, Camera camera);
        // 将不透明几何的绘制命令追加到当前绑定的 GBuffer。
        void RecordGBuffer(CommandBuffer commands, Camera camera);
        // 为写入同一相机深度缓冲的几何记录运动向量命令。
        void RecordMotion(CommandBuffer commands, Camera camera);
        // 根据给定的阴影视角和剔除平面记录阴影投射物。
        void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 shadowView,
            Matrix4x4 shadowProjection, Plane[] shadowPlanes);
        // 在同一渲染上下文中安排必须晚于相机绘制的工作。
        void EndCamera(ScriptableRenderContext context, Camera camera);
    }

    // 每个相机可选用管线已有的线性遮罩输出查看材质诊断。
    // 此输出跳过曝光、色调映射和泛光。
    public interface IMaxRenderDiagnosticParticipant
    {
        bool UsesMaterialDiagnostics { get; }
    }

    public static class MaxRenderBridge
    {
        private static readonly List<IMaxRenderParticipant> s_participants =
            new List<IMaxRenderParticipant>();

        // 地形显式切换相机时，由管线统一重置 TAA、曝光和阴影缓存。
        // 地形程序集不另外维护这份历史。
        public static event Action<Camera> TemporalHistoryInvalidated;

        // 注册一个参与者，使其接收后续相机和渲染通道的回调。
        public static void Register(IMaxRenderParticipant participant)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (s_participants.Contains(participant)) return;

            s_participants.Add(participant);
        }

        // 注销参与者，停止向它发送渲染回调。
        public static void Unregister(IMaxRenderParticipant participant)
        {
            s_participants.Remove(participant);
        }

        // 通知管线重置指定相机的共享历史。
        public static void InvalidateTemporalHistory(Camera camera) => TemporalHistoryInvalidated?.Invoke(camera);

        // 检查是否有参与者为指定相机提供绘制命令。
        public static bool HasParticipants(Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) return true;
            return false;
        }

        // 判断此相机是否需要输出线性材质诊断遮罩。
        public static bool HasMaterialDiagnostics(Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera) &&
                    s_participants[i] is IMaxRenderDiagnosticParticipant diagnostic && diagnostic.UsesMaterialDiagnostics)
                    return true;
            return false;
        }

        // 为参与当前相机渲染的已注册对象执行准备工作。
        public static void PrepareCamera(ScriptableRenderContext context, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].PrepareCamera(context, camera);
        }

        // 在管线绑定相机的 GBuffer 后，调用参与者记录绘制命令。
        public static void RecordGBuffer(CommandBuffer commands, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].RecordGBuffer(commands, camera);
        }

        // 调用参与者为指定相机记录运动向量。
        public static void RecordMotion(CommandBuffer commands, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].RecordMotion(commands, camera);
        }

        // 调用此相机的参与者记录当前级联的阴影投射物。
        // GPU 投影矩阵已适配纹理目标，剔除平面包含级联保护区。
        public static void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 shadowView,
            Matrix4x4 shadowProjection, Plane[] shadowPlanes)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera))
                    s_participants[i].RecordShadow(commands, camera, shadowView, shadowProjection, shadowPlanes);
        }

        // 在此相机调用 context.Submit 前，通过同一渲染上下文安排反馈压缩和回读。
        // 这样 GPU 会先完成地形多目标绘制，再处理反馈。
        public static void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].EndCamera(context, camera);
        }
    }
}
