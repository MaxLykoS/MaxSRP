using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public enum MaxRenderPlatform { PC = 0, Mobile = 1 }

    // Read the active asset instead of writing platform state into shared configurations.
    public static class MaxRenderQuality
    {
        public static MaxRenderPlatform Platform =>
            GraphicsSettings.currentRenderPipeline is MaxRendererPipelineAsset asset
                ? asset.Platform : MaxRenderPlatform.PC;

#if UNITY_EDITOR
        private static bool s_notificationPending;
#endif
        internal static void NotifyChanged(Object configuration)
        {
#if UNITY_EDITOR
            // OnValidate can run while importing. Touch the pipeline on the next
            // editor update; several edited profiles coalesce into one notification.
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
