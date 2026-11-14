using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // UE 5.8 mobile SSAO scheduling, using raster targets instead of compute UAVs.
    // Each camera owns one instance; AO is consumed before the shared scene TAA.
    internal sealed class MaxSSAOPass : IDisposable
    {
        private static readonly int DepthId = Shader.PropertyToID("_GDepth");
        private static readonly int NormalsId = Shader.PropertyToID("_GBuffer1");
        private static readonly int FullSizeId = Shader.PropertyToID("_SSAOFullSize");
        private static readonly int SizeId = Shader.PropertyToID("_SSAOSize");
        private static readonly int HZBSizeId = Shader.PropertyToID("_SSAOHZBSize");
        private static readonly int InvProjectionId = Shader.PropertyToID("_SSAOInvProjection");
        private static readonly int ProjectionScaleId = Shader.PropertyToID("_SSAOProjectionScale");
        private static readonly int ViewId = Shader.PropertyToID("_SSAOView");
        private static readonly int ParametersId = Shader.PropertyToID("_SSAOParameters");
        private static readonly int FadeRadiusId = Shader.PropertyToID("_SSAOFadeRadius");
        private static readonly int ScreenRadiusId = Shader.PropertyToID("_SSAOScreenRadius");
        private static readonly int WorldRadiusId = Shader.PropertyToID("_SSAORadiusInWorldSpace");
        private static readonly int QualityId = Shader.PropertyToID("_SSAOQuality");
        private static readonly int NoiseId = Shader.PropertyToID("_SSAONoiseIndex");
        private static readonly int UpsampleQualityId = Shader.PropertyToID("_SSAOUpsampleQuality");
        private static readonly int FilterModeId = Shader.PropertyToID("_SSAOFilterMode");
        private static readonly int HZBId = Shader.PropertyToID("_SSAOSetupTexture");
        private static readonly int InputId = Shader.PropertyToID("_SSAOInput");

        private Shader m_shader;
        private Material m_material;
        private RenderTexture m_hzb, m_raw, m_filtered, m_visibility;

        internal RenderTexture Execute(ScriptableRenderContext context, Camera camera, MaxAOSettings settings,
            RenderTexture depth, RenderTexture normals, RenderTexture colorTarget, MaxTemporalAAPass.CameraHistory temporal)
        {
            CommandBuffer cmd = CommandBufferPool.Get("UE Mobile SSAO");
            try
            {
                RenderTexture result = Record(cmd, camera, settings, depth, normals, colorTarget, temporal);
                context.ExecuteCommandBuffer(cmd);
                return result;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        // The production path also records into a supplied command buffer so a
        // one-shot GPU check need not fabricate a ScriptableRenderContext.
        internal RenderTexture Record(CommandBuffer cmd, Camera camera, MaxAOSettings settings,
            RenderTexture depth, RenderTexture normals, RenderTexture colorTarget, MaxTemporalAAPass.CameraHistory temporal)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!camera || !settings || !settings.AOEnabled || settings.Platform != AOPlatform.Mobile ||
                !settings.SSAOShader || !settings.SSAOShader.isSupported || !(settings.Strength > 0f) ||
                !depth || !normals || camera.orthographic || depth.width < 2 || depth.height < 2)
            {
                Dispose();
                return null;
            }

            SSAOProfile profile = settings.Configuration.MobileProfile;
            if (!(profile.RadiusInWorldSpace ? profile.Radius > 0f : profile.ScreenRadius > 0f))
            {
                Dispose();
                return null;
            }
            GraphicsFormat visibilityFormat = CanRender(GraphicsFormat.R8_UNorm)
                ? GraphicsFormat.R8_UNorm : GraphicsFormat.R16_SFloat;
            // UE stores half-resolution AO in R and PackR24F depth bytes in
            // GBA. Floating-point fallback keeps that same normalized encoding.
            GraphicsFormat packedFormat = CanRender(GraphicsFormat.R8G8B8A8_UNorm) &&
                SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, FormatUsage.Linear)
                    ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R16G16B16A16_SFloat;
            GraphicsFormat workingFormat = profile.HalfResolution ? packedFormat : visibilityFormat;
            if (!CanRender(visibilityFormat) || !CanRender(workingFormat) || !CanRender(GraphicsFormat.R32_SFloat) ||
                (profile.HalfResolution && !SystemInfo.IsFormatSupported(workingFormat, FormatUsage.Linear)))
            {
                Dispose();
                return null;
            }

            EnsureMaterial(settings.SSAOShader);
            if (m_material.passCount < 4) { Dispose(); return null; }

            int width = depth.width, height = depth.height;
            int halfWidth = (width + 1) / 2, halfHeight = (height + 1) / 2;
            int aoWidth = profile.HalfResolution ? halfWidth : width;
            int aoHeight = profile.HalfResolution ? halfHeight : height;
            int quality = Mathf.Clamp((int)profile.Quality, 0, 4);
            // The base raster variant supports Shader Model 3.5 / GLES3 without
            // compute. Fine derivatives use the conditional SM4.5 variant on
            // APIs that support them; otherwise filter the quad explicitly.
            bool quadFilter = UsesExplicitQuadFilter && (quality == 1 || quality == 2);
            // UE mobile smooths only full resolution without temporal AA.
            // The normal-aware quad filter above is part of quality 1/2 itself.
            bool smooth = profile.Blur && temporal == null && !profile.HalfResolution;
            bool intermediate = profile.HalfResolution || quadFilter || smooth;
            bool secondIntermediate = quadFilter && (profile.HalfResolution || smooth);

            EnsureTarget(ref m_hzb, halfWidth, halfHeight, GraphicsFormat.R32_SFloat, "SSAO HZB mip 0", FilterMode.Point);
            EnsureTarget(ref m_visibility, width, height, visibilityFormat, "SSAO visibility", FilterMode.Point);
            if (intermediate)
                EnsureTarget(ref m_raw, aoWidth, aoHeight, workingFormat, "SSAO raw", FilterMode.Bilinear);
            else
                ReleaseTarget(ref m_raw);
            if (secondIntermediate)
                EnsureTarget(ref m_filtered, aoWidth, aoHeight, workingFormat, "SSAO filtered", FilterMode.Bilinear);
            else
                ReleaseTarget(ref m_filtered);

            Matrix4x4 projection = temporal == null
                ? GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true)
                : temporal.Projection;
            cmd.BeginSample("UE Mobile SSAO");
            // Detach the GBuffer depth attachment before sampling it. Every
            // following draw targets an AO-owned texture and leaves HDR alone.
            if (colorTarget)
                cmd.SetRenderTarget(colorTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            else
                cmd.SetRenderTarget(m_hzb, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetGlobalTexture(DepthId, new RenderTargetIdentifier(depth), RenderTextureSubElement.Depth);
            cmd.SetGlobalTexture(NormalsId, normals);
            cmd.SetGlobalVector(FullSizeId, Size(width, height));
            cmd.SetGlobalVector(SizeId, Size(aoWidth, aoHeight));
            cmd.SetGlobalVector(HZBSizeId, Size(halfWidth, halfHeight));
            cmd.SetGlobalMatrix(InvProjectionId, projection.inverse);
            // Include the off-centre/jitter terms when projecting sample points.
            cmd.SetGlobalVector(ProjectionScaleId, new Vector4(projection.m00, projection.m11, projection.m02, projection.m12));
            cmd.SetGlobalMatrix(ViewId, camera.worldToCameraMatrix);
            cmd.SetGlobalVector(ParametersId, new Vector4(
                Mathf.Max(0.001f, Finite(profile.Radius, 2f)), Mathf.Max(0f, Finite(profile.Bias, 0.03f)),
                Mathf.Max(0.1f, Finite(profile.Power, 2f)), Mathf.Max(0f, Finite(profile.FadeDistance, 80f))));
            cmd.SetGlobalFloat(FadeRadiusId, Mathf.Max(0.01f, Finite(profile.FadeRadius, 50f)));
            cmd.SetGlobalFloat(ScreenRadiusId, Mathf.Max(0.001f, Finite(profile.ScreenRadius, 200f)));
            cmd.SetGlobalFloat(WorldRadiusId, profile.RadiusInWorldSpace ? 1f : 0f);
            cmd.SetGlobalInt(QualityId, quality);
            cmd.SetGlobalInt(NoiseId, temporal == null ? 0 : temporal.Sample & 7);
            cmd.SetGlobalInt(UpsampleQualityId, Mathf.Clamp(profile.UpsampleQuality, 0, 2));

#if UNITY_EDITOR
            UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
            Draw(cmd, m_hzb, 0);
            cmd.SetGlobalTexture(HZBId, m_hzb);
            RenderTexture current = intermediate ? m_raw : m_visibility;
            Draw(cmd, current, 1);
            if (quadFilter)
            {
                cmd.SetGlobalTexture(InputId, current);
                cmd.SetGlobalInt(FilterModeId, 0);
                current = secondIntermediate ? m_filtered : m_visibility;
                Draw(cmd, current, 2);
            }
            if (smooth)
            {
                cmd.SetGlobalTexture(InputId, current);
                cmd.SetGlobalInt(FilterModeId, 1);
                current = m_visibility;
                Draw(cmd, current, 2);
            }
            if (profile.HalfResolution)
            {
                cmd.SetGlobalTexture(InputId, current);
                Draw(cmd, m_visibility, 3);
            }
#if UNITY_EDITOR
            UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
            cmd.EndSample("UE Mobile SSAO");
            return m_visibility;
        }

        private void EnsureMaterial(Shader shader)
        {
            if (m_material && m_shader == shader) return;
            MaxRenderResources.Destroy(ref m_material);
            m_shader = shader;
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (UsesExplicitQuadFilter) m_material.DisableKeyword("MAX_SSAO_INLINE_QUAD");
            else m_material.EnableKeyword("MAX_SSAO_INLINE_QUAD");
        }

        private static Vector4 Size(int width, int height) => new Vector4(width, height, 1f / width, 1f / height);
        private static bool UsesExplicitQuadFilter
        {
            get
            {
                GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
                bool fineDerivatives = api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
                    api == GraphicsDeviceType.Metal || api == GraphicsDeviceType.Vulkan;
                return !fineDerivatives || SystemInfo.graphicsShaderLevel < 45 || !SystemInfo.supportsComputeShaders;
            }
        }
        private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        private static bool CanRender(GraphicsFormat format) =>
            SystemInfo.IsFormatSupported(format, FormatUsage.Render) && SystemInfo.IsFormatSupported(format, FormatUsage.Sample);

        private void Draw(CommandBuffer cmd, RenderTexture target, int pass)
        {
            cmd.SetRenderTarget(target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewport(new Rect(0f, 0f, target.width, target.height));
            cmd.DrawProcedural(Matrix4x4.identity, m_material, pass, MeshTopology.Triangles, 3);
        }

        private static void EnsureTarget(ref RenderTexture target, int width, int height, GraphicsFormat format,
            string label, FilterMode filtering)
        {
            if (target && target.width == width && target.height == height && target.graphicsFormat == format)
            {
                if (target.IsCreated() || target.Create()) return;
                throw new InvalidOperationException("Unable to recreate " + label);
            }
            ReleaseTarget(ref target);
            var descriptor = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1,
                useMipMap = false, autoGenerateMips = false, enableRandomWrite = false
            };
            target = new RenderTexture(descriptor)
            {
                name = "MaxSRP " + label, hideFlags = HideFlags.HideAndDontSave,
                filterMode = filtering, wrapMode = TextureWrapMode.Clamp
            };
            if (target.Create()) return;
            ReleaseTarget(ref target);
            throw new InvalidOperationException("Unable to create " + label);
        }

        private static void ReleaseTarget(ref RenderTexture target)
        {
            MaxRenderResources.UnbindTexture("_SSAOSetupTexture", target);
            MaxRenderResources.UnbindTexture("_SSAOInput", target);
            MaxRenderResources.Destroy(ref target);
        }

        public void Dispose()
        {
            ReleaseTarget(ref m_hzb);
            ReleaseTarget(ref m_raw);
            ReleaseTarget(ref m_filtered);
            ReleaseTarget(ref m_visibility);
            MaxRenderResources.Destroy(ref m_material);
            m_shader = null;
        }
    }
}
