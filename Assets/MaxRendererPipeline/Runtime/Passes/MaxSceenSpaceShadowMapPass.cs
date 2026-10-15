using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace MaxSRP
{
    public sealed class MaxScreenSpaceShadowMapPass : IDisposable
    {
        private RenderTexture m_ssShadowMap;
        private RenderTexture m_ssShadowMapTemporary;
        private RenderTexture m_ssShadowMapBlur;
        private Material m_matSSSM;

        public MaxScreenSpaceShadowMapPass(Shader shader)
        {
            m_matSSSM = new Material(shader);
        }

        public void EnsureSize(int cameraWidth, int cameraHeight)
        {
            int width = Mathf.Max(1, (cameraWidth + 3) / 4);
            int height = Mathf.Max(1, (cameraHeight + 3) / 4);
            if (m_ssShadowMap != null && m_ssShadowMap.width == width && m_ssShadowMap.height == height && m_ssShadowMap.IsCreated())
                return;
            ReleaseTargets();
            try
            {
                m_ssShadowMap = CreateTarget(width, height, "ScreenSpaceShadowMap");
                m_ssShadowMapTemporary = CreateTarget(width, height, "ScreenSpaceShadowMapTemporary");
                m_ssShadowMapBlur = CreateTarget(width, height, "ScreenSpaceShadowMapBlur");
            }
            catch
            {
                ReleaseTargets();
                throw;
            }
        }

        private static RenderTexture CreateTarget(int width, int height, string name)
        {
            var texture = new RenderTexture(width, height, 0, GraphicsFormat.R32_SFloat)
            { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (!texture.Create())
            {
                MaxRenderResources.Destroy(ref texture);
                throw new InvalidOperationException("Unable to create " + name);
            }
            return texture;
        }

        public void Execute(ScriptableRenderContext context, Camera camera, bool hasShadows)
        {
            // Dedicated per-camera inputs are independent of Unity's utility-draw matrices.
            Matrix4x4 view = camera.worldToCameraMatrix;
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true);
            m_matSSSM.SetMatrix("_ShadowCameraInverseVP", (projection * view).inverse);
            m_matSSSM.SetMatrix("_ShadowCameraView", view);
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            float width = renderSize.x, height = renderSize.y;
            m_matSSSM.SetVector("_ShadowScreenSize", new Vector4(width, height, 1.0f / width, 1.0f / height));
            CommandBuffer cmd = CommandBufferPool.Get("ScreenSpaceShadowMapPass");
            try
            {
                if (!hasShadows)
                {
                    cmd.SetRenderTarget(m_ssShadowMapBlur);
                    cmd.SetViewport(new Rect(0, 0, m_ssShadowMapBlur.width, m_ssShadowMapBlur.height));
                    cmd.ClearRenderTarget(false, true, Color.white);
                }
                else
                {
                    DrawFullscreen(cmd, m_ssShadowMap, 0);
                    cmd.SetGlobalVector("_ScreenSpaceShadowMapBeforeBlur_TexelSize",
                        new Vector4(1f / m_ssShadowMap.width, 1f / m_ssShadowMap.height, m_ssShadowMap.width, m_ssShadowMap.height));
                    cmd.SetGlobalTexture("_ScreenSpaceShadowMapBeforeBlur", m_ssShadowMap);
                    DrawFullscreen(cmd, m_ssShadowMapTemporary, 1);
                    cmd.SetGlobalTexture("_ScreenSpaceShadowMapBeforeBlur", m_ssShadowMapTemporary);
                    DrawFullscreen(cmd, m_ssShadowMapBlur, 2);
                }
                cmd.SetGlobalTexture("_ScreenSpaceShadowMapBlur", m_ssShadowMapBlur);
                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        private void DrawFullscreen(CommandBuffer cmd, RenderTexture target, int pass)
        {
            cmd.SetRenderTarget(target);
            cmd.SetViewport(new Rect(0, 0, target.width, target.height));
            cmd.DrawProcedural(Matrix4x4.identity, m_matSSSM, pass, MeshTopology.Triangles, 3);
        }

        private void ReleaseTargets()
        {
            MaxRenderResources.UnbindTexture("_ScreenSpaceShadowMapBlur", m_ssShadowMapBlur);
            MaxRenderResources.UnbindTexture("_ScreenSpaceShadowMapBeforeBlur", m_ssShadowMap);
            MaxRenderResources.UnbindTexture("_ScreenSpaceShadowMapBeforeBlur", m_ssShadowMapTemporary);
            MaxRenderResources.Destroy(ref m_ssShadowMap);
            MaxRenderResources.Destroy(ref m_ssShadowMapTemporary);
            MaxRenderResources.Destroy(ref m_ssShadowMapBlur);
        }

        public void Dispose()
        {
            ReleaseTargets();
            MaxRenderResources.Destroy(ref m_matSSSM);
        }
    }
}