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

        private Material m_mat;
        public MaxScreenSpaceShadowMapPass(Shader cs)
        {
            m_mat = new Material(cs);
            m_ssShadowMap = new RenderTexture(Screen.width / 4, Screen.height / 4, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, 0);
            m_ssShadowMap.filterMode = FilterMode.Trilinear;
            m_ssShadowMap.wrapMode = TextureWrapMode.Clamp;
        }

        public void Execute(ScriptableRenderContext context)
        {
            CommandBuffer cmd = CommandBufferPool.Get("ScreenSpaceShadowMapPass");

            // clear 
            cmd.SetRenderTarget(m_ssShadowMap);
            cmd.ClearRenderTarget(false, true, Color.white);

            // draw
            cmd.Blit(null, m_ssShadowMap, m_mat);

            // submit
            cmd.SetGlobalTexture(ShaderProperties._ScreenSpaceShadowMap, m_ssShadowMap);

            context.ExecuteCommandBuffer(cmd);
        }

        ~MaxScreenSpaceShadowMapPass()
        {
            if(m_mat != null)
                GameObject.DestroyImmediate(m_mat);

            if(m_ssShadowMap != null)
                GameObject.DestroyImmediate(m_ssShadowMap);
        }

        private static class ShaderProperties
        {
            public static int _ScreenSpaceShadowMap = Shader.PropertyToID("_ScreenSpaceShadowMap");
        }
    }
}