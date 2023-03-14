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
        private bool _offlineRendering = false;

        [SerializeField]
        private bool _srpBatcher = true;

        [SerializeField]
        private ShadowSetting _shadowSetting = new ShadowSetting();

        public bool IsSrpBatcherOn
        {
            get
            {
                return _srpBatcher;
            }
        }
        public ShadowSetting ShadowSetting
        {
            get
            {
                return _shadowSetting;
            }
        }

        public bool IsOfflineRenderingOn
        {
            get { return _offlineRendering; }
        }

        protected override RenderPipeline CreatePipeline()
        {
            return new MaxRenderPipeline(this);
        }
    }


    public class MaxRenderPipeline : RenderPipeline
    {
        private ShaderTagId m_shaderTag = new ShaderTagId("MaxForwardBase");
        private MaxLightConfigurator m_lightConfigurator = new MaxLightConfigurator();

        private MaxRenderObjectPass m_opaquePass = new MaxRenderObjectPass(false);
        private MaxRenderObjectPass m_transparentPass = new MaxRenderObjectPass(true);

        private MaxShadowCasterPass m_shadowCastPass = new MaxShadowCasterPass();
        private CommandBuffer m_command = new CommandBuffer();

        private MaxOfflineRenderer m_offlineRenderer;

        private MaxRendererPipelineAsset m_setting;
        public MaxRenderPipeline(MaxRendererPipelineAsset setting)
        {
            GraphicsSettings.useScriptableRenderPipelineBatching = setting.IsSrpBatcherOn;
            m_command.name = "RenderCamera";
            this.m_setting = setting;
        }

        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            //遍历摄像机，进行渲染
            foreach(var camera in cameras)
            {
                if (m_setting.IsOfflineRenderingOn && camera.cameraType == CameraType.Game)
                {
                    m_offlineRenderer ??= new MaxOfflineRenderer();

                    m_offlineRenderer.Render(context, camera);
                }
                else
                    RenderPerCamera(context, camera);
            }
            //提交渲染命令
            context.Submit();
        }


        private void ClearCameraTarget(ScriptableRenderContext context,Camera camera)
        {
            m_command.Clear();
            m_command.SetRenderTarget(BuiltinRenderTextureType.CameraTarget,BuiltinRenderTextureType.CameraTarget);
            m_command.ClearRenderTarget(true, true, camera.backgroundColor);
            context.ExecuteCommandBuffer(m_command);
        }

        private void RenderPerCamera(ScriptableRenderContext context,Camera camera)
        {

            //设置摄像机参数
            context.SetupCameraProperties(camera);
            //对场景进行裁剪
            camera.TryGetCullingParameters(out var cullingParams);
            cullingParams.shadowDistance = Mathf.Min(m_setting.ShadowSetting.shadowDistance, camera.farClipPlane - camera.nearClipPlane);
            CullingResults cullingResults = context.Cull(ref cullingParams);
            LightData lightData = m_lightConfigurator.SetupMultiShaderLightingParams(ref cullingResults);

            MaxShadowCasterPass.ShadowCasterSetting casterSetting = new MaxShadowCasterPass.ShadowCasterSetting
            {
                cullingResults = cullingResults,
                lightData = lightData,
                shadowSetting = m_setting.ShadowSetting
            };

            //投影Pass
            m_shadowCastPass.Execute(context, casterSetting);

            //重设摄像机参数
            context.SetupCameraProperties(camera);

            //清除摄像机背景
            ClearCameraTarget(context, camera);

            //非透明物体渲染
            m_opaquePass.Execute(context, camera, ref cullingResults);

            context.DrawSkybox(camera);

            //透明物体渲染
            m_transparentPass.Execute(context, camera, ref cullingResults);

        }
    }
}
