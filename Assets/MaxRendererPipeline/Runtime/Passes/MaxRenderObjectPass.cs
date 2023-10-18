﻿using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderOpaquePass
    {
        private ShaderTagId m_shaderTag = new ShaderTagId("MaxDeferred");

        public void Execute(ScriptableRenderContext context, Camera camera, ref CullingResults cullingResults)
        {
            var drawingSetting = CreateDrawSettings(camera);
            //根据_isTransparent，利用RenderQueueRange来过滤出透明物体，或者非透明物体
            var filterSetting = new FilteringSettings(RenderQueueRange.opaque);
            //绘制物体
            context.DrawRenderers(cullingResults, ref drawingSetting, ref filterSetting);
        }

        private DrawingSettings CreateDrawSettings(Camera camera)
        {
            var sortingSetting = new SortingSettings(camera);
            //设置物体渲染排序标准
            sortingSetting.criteria = SortingCriteria.CommonOpaque;
            var drawingSetting = new DrawingSettings(m_shaderTag, sortingSetting);
            drawingSetting.perObjectData |= PerObjectData.None;
            return drawingSetting;
        }
    }
}