using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/MaxRendererPipelineAsset")]
    public class MaxRendererPipelineAsset : RenderPipelineAsset
    {
        [SerializeField]
        private ShadowSetting _shadowSetting = new ShadowSetting();

        public ShadowSetting ShadowSetting
        {
            get
            {
                return _shadowSetting;
            }
        }

        protected override RenderPipeline CreatePipeline()
        {
            return new MaxRenderPipeline(this);
        }
    }
}
