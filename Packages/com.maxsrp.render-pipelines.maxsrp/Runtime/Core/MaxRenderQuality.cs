using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public enum MaxRenderPlatform { PC = 0, Mobile = 1 }

    // 直接从当前管线资产获取平台档位，使共享配置资产无需保存平台状态。
    public static class MaxRenderQuality
    {
        public static MaxRenderPlatform Platform =>
            GraphicsSettings.currentRenderPipeline is MaxRendererPipelineAsset asset
                ? asset.Platform : MaxRenderPlatform.PC;

#if UNITY_EDITOR
        private static bool s_notificationPending;
#endif
        // 通知当前使用的 MaxSRP 资产重新检查模块配置。
        internal static void NotifyChanged(Object configuration)
        {
#if UNITY_EDITOR
            // OnValidate 可能在导入资产时运行，因此将管线通知推迟到下一次编辑器更新。
            // 同一次更新前的多项修改合并为一次通知。
            if (s_notificationPending) return;

            s_notificationPending = true;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                s_notificationPending = false;
                var asset = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
                if (asset) asset.MarkConfigurationDirty();

                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            };
#else
            var asset = GraphicsSettings.currentRenderPipeline as MaxRendererPipelineAsset;
            if (asset) asset.MarkConfigurationDirty();
#endif
        }
    }
}
