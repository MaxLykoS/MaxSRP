using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxLightPass
    {
        private RenderTargetIdentifier[] m_GBuffers;
        private RenderTargetIdentifier m_Depth;
        private static Material s_Material;
        public MaxLightPass(RenderTargetIdentifier[] gBuffers, RenderTargetIdentifier depth)
        {
            this.m_GBuffers = gBuffers;
            this.m_Depth = depth;

            if (s_Material == null)
                s_Material = new Material(Shader.Find("MaxSRP/PBRLitPass"));
        }

        public void Execute(ScriptableRenderContext context, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get("LightPass");

            cmd.Blit(m_GBuffers[0], BuiltinRenderTextureType.CameraTarget, s_Material);
            context.ExecuteCommandBuffer(cmd);
        }
    }
}