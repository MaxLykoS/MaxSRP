using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderObjectPass
    {
        private ShaderTagId m_shaderTag = new ShaderTagId("MaxForwardBase");
        private bool m_isTransparent = false;
        public MaxRenderObjectPass(bool transparent)
        {
            m_isTransparent = transparent;
        }

        public void Execute(ScriptableRenderContext context, Camera camera, ref CullingResults cullingResults)
        {
            var drawSetting = CreateDrawSettings(camera);
            //根据_isTransparent，利用RenderQueueRange来过滤出透明物体，或者非透明物体
            var filterSetting = new FilteringSettings(m_isTransparent ? RenderQueueRange.transparent : RenderQueueRange.opaque);
            //绘制物体
            context.DrawRenderers(cullingResults, ref drawSetting, ref filterSetting);
        }

        private DrawingSettings CreateDrawSettings(Camera camera)
        {
            var sortingSetting = new SortingSettings(camera);
            //设置物体渲染排序标准
            sortingSetting.criteria = m_isTransparent ? SortingCriteria.CommonTransparent : SortingCriteria.CommonOpaque;
            var drawSetting = new DrawingSettings(m_shaderTag, sortingSetting);
            drawSetting.perObjectData |= PerObjectData.LightData;
            drawSetting.perObjectData |= PerObjectData.LightIndices;
            return drawSetting;
        }
    }
}