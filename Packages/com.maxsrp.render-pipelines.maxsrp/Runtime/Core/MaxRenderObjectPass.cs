using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderOpaquePass : MaxRenderPass
    {
        private readonly ShaderTagId m_shaderTag = new ShaderTagId("MaxDeferred");

        // 用 MaxDeferred 通道将可见的不透明物体绘制到已绑定的 GBuffer。
        public void Execute(ScriptableRenderContext context, Camera camera, ref CullingResults cullingResults)
        {
            var drawingSetting = CreateDrawSettings(camera);
            var filterSetting = new FilteringSettings(RenderQueueRange.opaque);

            context.DrawRenderers(cullingResults, ref drawingSetting, ref filterSetting);
        }

        // 设置不透明物体的排序，并向 Unity 请求逐物体的光照贴图数据。
        private DrawingSettings CreateDrawSettings(Camera camera)
        {
            var sortingSetting = new SortingSettings(camera);
            sortingSetting.criteria = SortingCriteria.CommonOpaque;
            var drawingSetting = new DrawingSettings(m_shaderTag, sortingSetting);

            // Unity 提供光照贴图图集和逐物体 UV 变换。
            // 绘制使用光照贴图的物体时，Unity 负责选择 LIGHTMAP_ON 或 DIRLIGHTMAP_COMBINED 关键字。
            drawingSetting.perObjectData |= PerObjectData.Lightmaps;
            return drawingSetting;
        }

        protected override void ReleaseResources() { }
    }
}
