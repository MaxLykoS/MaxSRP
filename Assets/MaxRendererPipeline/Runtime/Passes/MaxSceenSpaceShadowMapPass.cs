using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Screen Space Shadow Map
    public class MaxScreenSpaceShadowMapPass
    {
        private RenderTexture m_ssShadowMap;
        private RenderTexture m_ssShadowMapBlur;

        private Material m_matSSSM;
        public MaxScreenSpaceShadowMapPass(Shader screenSpaceShadowmapShader)
        {
            m_matSSSM = new Material(screenSpaceShadowmapShader);
            m_ssShadowMap = new RenderTexture(Screen.width / 4, Screen.height / 4, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, 0);
            m_ssShadowMap.filterMode = FilterMode.Trilinear;
            m_ssShadowMap.wrapMode = TextureWrapMode.Clamp;
            m_ssShadowMap.name = "ScreenSpaceShadowMap";

            m_ssShadowMapBlur = new RenderTexture(m_ssShadowMap.width, m_ssShadowMap.height, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, 0);
            m_ssShadowMapBlur.filterMode = FilterMode.Trilinear;
            m_ssShadowMapBlur.wrapMode = TextureWrapMode.Clamp;
            m_ssShadowMapBlur.name = "ScreenSpaceShadowMapBlur";
        }

        public void Execute(ScriptableRenderContext context)
        {
            CommandBuffer cmd = CommandBufferPool.Get("ScreenSpaceShadowMapPass");

            // clear 
            cmd.SetRenderTarget(m_ssShadowMap);
            cmd.ClearRenderTarget(false, true, Color.white);

            // draw screenspace shadow map
            cmd.Blit(null, m_ssShadowMap, m_matSSSM, 0);

            // blur
            cmd.SetRenderTarget(m_ssShadowMapBlur);
            cmd.SetGlobalTexture(ShaderProperties._ScreenSpaceShadowMapBeforeBlur, m_ssShadowMap);
            cmd.Blit(null, m_ssShadowMapBlur, m_matSSSM, 1);
            cmd.Blit(null, m_ssShadowMapBlur, m_matSSSM, 2);

            // submit
            cmd.SetGlobalTexture(ShaderProperties._ScreenSpaceShadowMap, m_ssShadowMapBlur);

            context.ExecuteCommandBuffer(cmd);
        }

        ~MaxScreenSpaceShadowMapPass()
        {
            if(m_matSSSM != null)
                GameObject.DestroyImmediate(m_matSSSM);

            if(m_ssShadowMap != null)
                GameObject.DestroyImmediate(m_ssShadowMap);
        }

        private static class ShaderProperties
        {
            public static readonly int _ScreenSpaceShadowMap = Shader.PropertyToID("_ScreenSpaceShadowMapBlur");
            public static readonly int _ScreenSpaceShadowMapBeforeBlur = Shader.PropertyToID("_ScreenSpaceShadowMapBeforeBlur");
        }
    }
}