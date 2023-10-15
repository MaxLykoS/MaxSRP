using MaxSRP;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderPipeline : RenderPipeline
    {
        private ShaderTagId m_shaderTag = new ShaderTagId("MaxDeferred");
        private MaxLightConfigurator m_lightConfigurator = new MaxLightConfigurator();

        private MaxRenderObjectPass m_opaquePass = new MaxRenderObjectPass(false);
        private MaxRenderObjectPass m_transparentPass = new MaxRenderObjectPass(true);
        private MaxLightPass m_LightPass;
        private MaxIBLGIPass m_iblGIPass;

        private MaxShadowCasterPass m_shadowCastPass = new MaxShadowCasterPass();

        private MaxRendererPipelineAsset m_setting;

        private RenderTexture m_GDepthBuffer;  // depth buffer
        private RenderTexture[] m_GBuffers = new RenderTexture[4]; // color buffer
        private RenderTargetIdentifier[] m_GBufferIDs = new RenderTargetIdentifier[4];  // gbuffer ID
        private RenderTexture m_LightPassTexture; // �洢light pass������

        public MaxRenderPipeline(MaxRendererPipelineAsset setting)
        {
            // ����SRP Batch
            GraphicsSettings.useScriptableRenderPipelineBatching = true;
            this.m_setting = setting;

            // ����Gbuffer
            m_GDepthBuffer = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
            m_GBuffers[0] = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            m_GBuffers[1] = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear);
            m_GBuffers[2] = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB64, RenderTextureReadWrite.Linear);
            m_GBuffers[3] = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            m_LightPassTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

            m_GDepthBuffer.filterMode = FilterMode.Point;
            for (int i = 0; i < 4; ++i)
            {
                m_GBufferIDs[i] = m_GBuffers[i];
                m_GBuffers[i].filterMode = FilterMode.Point;
            }

            m_LightPass = new MaxLightPass(m_GBufferIDs, m_GDepthBuffer);
            m_iblGIPass = new MaxIBLGIPass(setting.ENVMap, setting.IBLCS);  // ���캯����bake���ύ
        }

        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            //RecreateRTWhenScreenModified();
            // ������
            ShaderBindings.SetPerFrameShaderVariables(context);
            //�����������������Ⱦ
            foreach (var camera in cameras)
            {
#if UNITY_EDITOR
                if (camera.cameraType == CameraType.SceneView)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif
                ShaderBindings.SetPerCameraShaderVariables(context, camera);
                if (camera.cameraType == CameraType.Game)
                    RenderMainCamera(context, camera);
                else
                    RenderMainCamera(context, camera);  // scene �����ʱ�������ͬ�߼�����RT��temp
            }
            //�ύ��Ⱦ����
            context.Submit();
        }

        private void RenderMainCamera(ScriptableRenderContext context, Camera camera)
        {
            //�������������
            context.SetupCameraProperties(camera);
            //�Գ������вü�
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

            
            //ͶӰPass
            m_shadowCastPass.Execute(context, casterSetting);

            //�������������
            context.SetupCameraProperties(camera);

            // ���gbuffer
            using (CommandBuffer clearGBufferCmd = CommandBufferPool.Get("ClearGBuffer"))
            {
                clearGBufferCmd.SetRenderTarget(m_GBufferIDs, m_GDepthBuffer);
                clearGBufferCmd.ClearRenderTarget(true, true, Color.clear);
                context.ExecuteCommandBuffer(clearGBufferCmd);
            }

            CommandBuffer cmd = CommandBufferPool.Get("Draw Opaque");
            // ����gbuffer
            cmd.SetGlobalTexture("_GDepth", m_GDepthBuffer);
            cmd.SetGlobalTexture("_GBuffer0", m_GBuffers[0]);
            cmd.SetGlobalTexture("_GBuffer1", m_GBuffers[1]);
            cmd.SetGlobalTexture("_GBuffer2", m_GBuffers[2]);
            cmd.SetGlobalTexture("_GBuffer3", m_GBuffers[3]);

            //��͸��������Ⱦ
            m_opaquePass.Execute(context, camera, ref cullingResults);

            context.ExecuteCommandBuffer(cmd);

            m_LightPass.Execute(context, camera);

            // Renders skybox if required
            if (camera.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null)
                context.DrawSkybox(camera);

            //͸��������Ⱦ
            //m_transparentPass.Execute(context, camera, ref cullingResults);
        }
    }
}