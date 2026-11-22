using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public sealed class MaxLightPass : MaxRenderPass
    {
        private Material m_material;
        private readonly LocalKeyword m_gtaoUInt;
        private readonly bool m_mobile;
        private readonly MaxDDGIPass m_ddgi;
#if UNITY_EDITOR
        public static Camera DiagnosticCamera;
        public static float DiagnosticLightingMode;

        internal static float GetDiagnosticMode(Camera camera) =>
            camera == DiagnosticCamera ? DiagnosticLightingMode : 0.0f;
#endif
        private readonly MaterialPropertyBlock m_iblBindings = new MaterialPropertyBlock();

        public MaxLightPass(Shader shader, MaxRenderPlatform platform)
            : this(shader, platform, new MaxDDGIPass(platform == MaxRenderPlatform.Mobile
                ? MaxDDGIConfig.ResolveMobileSamplingSpacing(null) : 0f))
        {
        }

        internal MaxLightPass(Shader shader, MaxRenderPlatform platform, MaxDDGIPass ddgi)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires a supported Lighting Shader reference on its pipeline asset (MaxSRP/PBRLitPass).");
            m_mobile = platform == MaxRenderPlatform.Mobile;
            m_ddgi = ddgi;
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_gtaoUInt = shader.keywordSpace.FindKeyword("MAX_GTAO_UINT");
        }

        public void Execute(ScriptableRenderContext context, Camera camera, RenderTargetIdentifier target,
            RenderTargetIdentifier depth, Rect viewport, MaxIBLGIPass ibl, bool hasShadows,
            MaxClusteredLightingPass.CameraLightResources localLights,
            MaxBakedSolarGI bakedSolar = null, CelestialLightData celestial = default,
            MaxAmbientOcclusion gtao = default, RenderTexture gtaoVisibility = null) =>
            Execute(context, camera, target, depth, viewport, ibl, localLights,
                MaxTimeOfDayState.FindForComponent(bakedSolar), celestial, gtao, gtaoVisibility, hasShadows);

        internal void Execute(ScriptableRenderContext context, Camera camera, RenderTargetIdentifier target,
            RenderTargetIdentifier depth, Rect viewport, MaxIBLGIPass ibl,
            MaxClusteredLightingPass.CameraLightResources localLights, MaxTimeOfDayState timeOfDay,
            CelestialLightData celestial, MaxAmbientOcclusion gtao, RenderTexture gtaoVisibility,
            bool? shadowAvailabilityOverride = null)
        {
            using (var commands = new PooledCommandBuffer("LightPass"))
            {
                CommandBuffer cmd = commands.Buffer;
                cmd.SetGlobalColor("_CameraBackgroundColor", camera.backgroundColor.linear);
                cmd.SetGlobalInteger("_CameraClearFlags", (int)camera.clearFlags);
                if (depth == BuiltinRenderTextureType.None)
                    cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                else
                    cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        depth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(viewport);
                int pass = camera.clearFlags == CameraClearFlags.Nothing ? 2 :
                    camera.clearFlags == CameraClearFlags.Depth ? 1 : 0;
                m_iblBindings.Clear();
                ibl.SetLightingBindings(m_iblBindings);
                localLights.Bind(m_iblBindings);
                m_ddgi.BindLighting(m_iblBindings, timeOfDay, celestial);
                bool showAOOnly = gtao.AOEnabled && gtao.AOOnly;
#if UNITY_EDITOR
                showAOOnly &= !(timeOfDay?.IsLightingDebugMode ?? false);
#endif
                Vector4 gtaoParams = new Vector4(0, showAOOnly ? 1 : 0, 0, 0);
                if (gtao.AOEnabled && gtaoVisibility)
                {
                    m_iblBindings.SetTexture("_MaxGTAOTexture", gtaoVisibility);
                    gtaoParams.x = Mathf.Clamp01(gtao.Strength);
                    gtaoParams.z = gtaoVisibility.width;
                    gtaoParams.w = gtaoVisibility.height;
                }
                m_iblBindings.SetVector("_MaxGTAOParams", gtaoParams);
                if (m_gtaoUInt.isValid)
                    cmd.SetKeyword(m_material, m_gtaoUInt,
                        gtaoVisibility && gtaoVisibility.graphicsFormat == GraphicsFormat.R8_UInt);
                if (shadowAvailabilityOverride.HasValue)
                    m_iblBindings.SetFloat(MaxShadowShaderProperties.Available, shadowAvailabilityOverride.Value ? 1.0f : 0.0f);
                m_iblBindings.SetFloat("_ShadowPcfMobile", m_mobile ? 1.0f : 0.0f);
                m_iblBindings.SetMatrix("_ShadowReceiverView", camera.worldToCameraMatrix);
#if UNITY_EDITOR
                m_iblBindings.SetFloat("_MaxLightingDiagnosticMode", GetDiagnosticMode(camera));
                // A loading placeholder must never write scene color/depth.
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                cmd.DrawProcedural(Matrix4x4.identity, m_material, pass, MeshTopology.Triangles, 3, 1, m_iblBindings);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                context.ExecuteCommandBuffer(cmd);
            }
        }

        protected override void ReleaseResources()
        {
            MaxRenderResources.Destroy(ref m_material);
        }
    }
}
