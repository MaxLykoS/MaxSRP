using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public sealed class MaxLightPass : IDisposable
    {
        private Material m_material;
#if UNITY_EDITOR
        public static Camera DiagnosticCamera;
        public static float DiagnosticLightingMode;
#endif
        private readonly MaterialPropertyBlock m_iblBindings = new MaterialPropertyBlock();

        public MaxLightPass(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires a supported Lighting Shader reference on its pipeline asset (MaxSRP/PBRLitPass).");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Execute(ScriptableRenderContext context, Camera camera, RenderTargetIdentifier target,
            RenderTargetIdentifier depth, Rect viewport, MaxIBLGIPass ibl, bool hasShadows,
            MaxClusteredLightingPass.CameraLightResources localLights,
            MaxBakedSolarGI bakedSolar = null, SunLightData sun = default,
            MaxGTAOSettings gtao = null, RenderTexture gtaoVisibility = null)
        {
            CommandBuffer cmd = CommandBufferPool.Get("LightPass");
            try
            {
                cmd.SetGlobalColor("_CameraBackgroundColor", camera.backgroundColor.linear);
                cmd.SetGlobalInteger("_CameraClearFlags", (int)camera.clearFlags);
                cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    depth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(viewport);
                int pass = camera.clearFlags == CameraClearFlags.Nothing ? 2 :
                    camera.clearFlags == CameraClearFlags.Depth ? 1 : 0;
                m_iblBindings.Clear();
                ibl.SetLightingBindings(m_iblBindings);
                localLights.Bind(m_iblBindings);
                MaxBakedSolarGI.Bind(m_iblBindings, bakedSolar, sun);
                Vector4 gtaoParams = new Vector4(0, gtao && gtao.AOOnly ? 1 : 0, 0, 0);
                if (gtao && gtaoVisibility)
                {
                    m_iblBindings.SetTexture("_MaxGTAOTexture", gtaoVisibility);
                    gtaoParams.x = Mathf.Clamp01(gtao.Strength);
                    gtaoParams.z = gtaoVisibility.width;
                    gtaoParams.w = gtaoVisibility.height;
                }
                m_iblBindings.SetVector("_MaxGTAOParams", gtaoParams);
                // Bind camera-local availability explicitly; other views may have left global maps.
                m_iblBindings.SetFloat("_MainLightShadowsAvailable", hasShadows ? 1.0f : 0.0f);
                m_iblBindings.SetMatrix("_ShadowReceiverView", camera.worldToCameraMatrix);
#if UNITY_EDITOR
                m_iblBindings.SetFloat("_MaxLightingDiagnosticMode",
                    camera == DiagnosticCamera ? DiagnosticLightingMode : 0.0f);
                // A loading placeholder must never write scene color/depth.
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                cmd.DrawProcedural(Matrix4x4.identity, m_material, pass, MeshTopology.Triangles, 3, 1, m_iblBindings);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        public void Dispose()
        {
            MaxRenderResources.Destroy(ref m_material);
        }
    }
}
