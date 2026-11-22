using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    internal static class MaxRenderResources
    {
#if UNITY_EDITOR
        private const string PendingSceneObjectsKey = "MaxSRP.PendingSceneObjectDestruction";
        private static bool s_sceneDestructionScheduled;

        // 脚本重载后，如果仍有待处理的场景对象 ID，继续执行延迟销毁。
        [InitializeOnLoadMethod]
        private static void ResumePendingSceneDestruction()
        {
            if (SessionState.GetIntArray(PendingSceneObjectsKey, Array.Empty<int>()).Length > 0)
                ScheduleSceneDestruction();
        }

        // 保存编辑器生成的场景对象 ID，留待后续更新时销毁。
        private static void QueueSceneDestruction(GameObject sceneObject)
        {
            // 脚本域重载前也可能释放管线。
            // 把实例 ID 保存在编辑器会话中，使下次更新仍能找到待销毁的对象。
            var pending = new List<int>(SessionState.GetIntArray(PendingSceneObjectsKey, Array.Empty<int>()));
            int id = sceneObject.GetInstanceID();
            if (!pending.Contains(id)) pending.Add(id);

            SessionState.SetIntArray(PendingSceneObjectsKey, pending.ToArray());
            ScheduleSceneDestruction();
        }

        // 确保只注册一次编辑器更新回调，由该回调处理待销毁对象。
        private static void ScheduleSceneDestruction()
        {
            if (s_sceneDestructionScheduled) return;
            s_sceneDestructionScheduled = true;
            EditorApplication.update += DestroyPendingSceneObjects;
        }

        // 取出待处理的实例 ID，销毁其中仍存在的场景对象。
        private static void DestroyPendingSceneObjects()
        {
            EditorApplication.update -= DestroyPendingSceneObjects;
            s_sceneDestructionScheduled = false;

            int[] pending = SessionState.GetIntArray(PendingSceneObjectsKey, Array.Empty<int>());
            SessionState.SetIntArray(PendingSceneObjectsKey, Array.Empty<int>());

            foreach (int id in pending)
            {
                var sceneObject = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (sceneObject == null) continue;

                if (Application.isPlaying) UnityEngine.Object.Destroy(sceneObject);
                else UnityEngine.Object.DestroyImmediate(sceneObject);
            }
        }
#endif
        internal static bool SupportsPackedHdrTarget(bool requireBlending = false)
        {
            const GraphicsFormat format = GraphicsFormat.B10G11R11_UFloatPack32;
            // Packed HDR is filterable on the target graphics APIs. Unity 2022.3
            // can report Sample/Linear as unsupported while the RT samples correctly.
            return SystemInfo.IsFormatSupported(format, FormatUsage.Render) &&
                (!requireBlending || SystemInfo.IsFormatSupported(format, FormatUsage.Blend));
        }

        internal static bool RequiresSceneAlpha(Camera camera) =>
            camera.targetTexture != null && GraphicsFormatUtility.HasAlphaChannel(camera.targetTexture.graphicsFormat);

        // 清空持有方的引用，并根据资源类型和编辑器状态处理释放。
        public static void Destroy<T>(ref T resource) where T : UnityEngine.Object
        {
            T owned = resource;
            resource = null;
            if (owned == null) return;

            if (owned is GameObject sceneObject)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    // 资产校验可能在 OnValidate 或渲染回调内释放管线。
                    // 这些回调禁止调用 DestroyImmediate 销毁场景对象。
                    // 编辑模式也不能用 Destroy 销毁它们，因此把销毁安排到
                    // 当前回调结束之后的下一次编辑器更新。
                    QueueSceneDestruction(sceneObject);
                    return;
                }
#endif
                UnityEngine.Object.Destroy(sceneObject);
                return;
            }

            if (owned is RenderTexture target) target.Release();
            CoreUtils.Destroy(owned);
        }

        // 按属性名查找 Shader 全局纹理。仅当它仍指向待释放纹理时清除绑定。
        public static void UnbindTexture(string name, Texture owned)
        {
            if (owned != null && Shader.GetGlobalTexture(name) == owned)
                Shader.SetGlobalTexture(name, (Texture)null);
        }

        // 按属性 ID 查找 Shader 全局纹理。仅当它仍指向待释放纹理时清除绑定。
        public static void UnbindTexture(int nameId, Texture owned)
        {
            if (owned != null && Shader.GetGlobalTexture(nameId) == owned)
                Shader.SetGlobalTexture(nameId, (Texture)null);
        }
    }
}
