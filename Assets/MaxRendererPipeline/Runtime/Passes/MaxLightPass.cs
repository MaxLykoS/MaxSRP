using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxLightPass
    {
        private RenderTargetIdentifier[] m_gBuffers;
        private static Material s_mat;
        public MaxLightPass(RenderTargetIdentifier[] gBuffers)
        {
            this.m_gBuffers = gBuffers;

            if (s_mat == null)
                s_mat = new Material(Shader.Find("MaxSRP/PBRLitPass"));
        }

        public void Execute(ScriptableRenderContext context, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get("LightPass");

            cmd.Blit(m_gBuffers[0], BuiltinRenderTextureType.CameraTarget, s_mat);
            context.ExecuteCommandBuffer(cmd);
        }

        ~MaxLightPass()
        {
            if(s_mat != null)
                GameObject.DestroyImmediate(s_mat);
            s_mat = null;
        }
    }
}