using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace MaxSRP
{
    public class MaxShadowCasterPass : IDisposable
    {
        public const int MaxAtlasSize = 4096;
        public const int TileBorder = 2;
        private RenderTexture m_shadowAtlas;
        private readonly RectInt[] m_tileRects = new RectInt[4];
        private Cascade m_cascade;
        private Camera m_shadowCamera;
        private readonly MaxProceduralSkyboxPass.AtmosphereSettings m_atmosphereSettings;
        private readonly CascadeSettings m_settings;
        private ShaderTagId m_shaderTagID = new ShaderTagId("ShadowCaster");
        public MaxShadowCasterPass(CascadeSettings cascadeSettings, MaxProceduralSkyboxPass.AtmosphereSettings atmosphereSettings)
        {
            m_atmosphereSettings = atmosphereSettings;
            m_settings = cascadeSettings;
            try
            {
                m_cascade = new Cascade(cascadeSettings);
                EnsureShadowAtlas();
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        private void EnsureShadowAtlas()
        {
            int resolution = Mathf.Min(MaxAtlasSize, Mathf.ClosestPowerOfTwo(SystemInfo.maxTextureSize));
            // Never round above the actual hardware limit.
            if (resolution > SystemInfo.maxTextureSize) resolution /= 2;
            RenderTexture texture = m_shadowAtlas;
            if (texture == null || texture.width != resolution || !texture.IsCreated())
            {
                var descriptor = new RenderTextureDescriptor(resolution, resolution, RenderTextureFormat.Depth, 24)
                    { shadowSamplingMode = ShadowSamplingMode.CompareDepths, msaaSamples = 1, sRGB = false };
                var replacement = new RenderTexture(descriptor) {
                    name = "Directional Shadow Atlas", wrapMode = TextureWrapMode.Clamp,
                    // Point blocker reads and hardware comparison PCF use separate samplers.
                    filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
                };
                if (!replacement.Create())
                {
                    MaxRenderResources.Destroy(ref replacement);
                    throw new InvalidOperationException("Cannot allocate directional shadow atlas");
                }
                MaxRenderResources.UnbindTexture("_MainShadowAtlas", texture);
                MaxRenderResources.Destroy(ref texture);
                m_shadowAtlas = replacement;
            }
            int cellSize = resolution / 2;
            for (int i = 0; i < 4; ++i)
            {
                int requested = (int)m_settings.GetCascade(i).MapResolution;
                int tileSize = Mathf.Min(requested >= 2048 ? 2048 : requested >= 1024 ? 1024 : 512, cellSize);
                var rect = new RectInt((i % 2) * cellSize + TileBorder, (i / 2) * cellSize + TileBorder,
                    tileSize - 2 * TileBorder, tileSize - 2 * TileBorder);
                m_tileRects[i] = rect;
                // Viewport placement and texture addressing use the same native
                // target coordinates. ShadowClipToUV handles LOCAL projection Y;
                // flipping the atlas row again would select a different cascade.
                Shader.SetGlobalVector("_ShadowAtlasTile" + i,
                    new Vector4((float)rect.width / resolution, (float)rect.height / resolution,
                        (float)rect.x / resolution, (float)rect.y / resolution));
                Shader.SetGlobalFloat("_ShadowResolution" + i, rect.width);
            }
            Shader.SetGlobalTexture("_MainShadowAtlas", m_shadowAtlas);
            Shader.SetGlobalInteger("_PcssCascadeCount", Mathf.Clamp(m_settings.PcssCascadeCount, 0, 4));
        }
        public bool Execute(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            if (!sun.CastsShadows) return false;
            EnsureShadowAtlas();
            // The atmosphere parameter is an angular RADIUS in degrees.
            // Keep tangent finite for invalid/extreme Inspector input.
            float angularRadius = Mathf.Clamp(m_atmosphereSettings._SunDiskAngle, 0.001f, 89.0f) * Mathf.Deg2Rad;
            Shader.SetGlobalFloat("_ShadowLightTanAngularRadius", Mathf.Tan(angularRadius));
            Vector3 lightDir = -(Vector3)sun.Direction;
            if (!m_cascade.UpdateCascade(camera, ref lightDir)) return false;

            // Assigning Camera.aspect disables its automatic viewport tracking, even
            // when the old numeric value is subsequently restored. Keep all shadow
            // camera changes off the rendering camera, including custom matrices.
            if (m_shadowCamera == null)
            {
                var shadowObject = new GameObject("MaxSRP Shadow Camera") { hideFlags = HideFlags.HideAndDontSave };
                m_shadowCamera = shadowObject.AddComponent<Camera>();
            }
            m_shadowCamera.CopyFrom(camera);
            m_shadowCamera.enabled = false;
            m_shadowCamera.targetTexture = null;
            m_shadowCamera.rect = new Rect(0, 0, 1, 1);
            m_shadowCamera.orthographic = true;
            m_shadowCamera.ResetProjectionMatrix();
            m_shadowCamera.ResetWorldToCameraMatrix();
            m_shadowCamera.ResetCullingMatrix();
            bool renderedAny = false;

            // Clear the entire shared depth target ONCE. A per-cascade clear may
            // ignore the viewport and erase previously rendered atlas tiles.
            CommandBuffer clear = CommandBufferPool.Get("Shadow Atlas Clear");
            try
            {
                clear.SetRenderTarget(m_shadowAtlas);
                clear.SetViewport(new Rect(0, 0, m_shadowAtlas.width, m_shadowAtlas.height));
                clear.ClearRenderTarget(true, false, Color.clear);
                context.ExecuteCommandBuffer(clear);
            }
            finally { CommandBufferPool.Release(clear); }

            for (int i = 0; i < 4; ++i)
            {
                RectInt tile = m_tileRects[i];
                int resolution = tile.width;
                // Legacy Editor quad comparisons use the first cascade's texel scale.
                if (i == 0) Shader.SetGlobalFloat("_ShadowTexelResolution", resolution);
                float halfDepthRange = m_cascade.ConfigCameraToShadowSpace(m_shadowCamera, lightDir, i, m_cascade.m_OrthoDistance, resolution);
                Matrix4x4 v = m_shadowCamera.worldToCameraMatrix;
                Matrix4x4 p = GL.GetGPUProjectionMatrix(m_shadowCamera.projectionMatrix, true);
                Matrix4x4 shadowVP = p * v;
                Shader.SetGlobalMatrix("_ShadowVPMatrix" + i, shadowVP);
                Shader.SetGlobalFloat("_ShadowDepthHalfRange" + i, halfDepthRange);
                Shader.SetGlobalFloat("_OrthoWidth" + i, m_cascade.m_OrthoWidths[i]);
                CommandBuffer cmd = CommandBufferPool.Get("ShadowMap");
                try
                {
                    context.SetupCameraProperties(m_shadowCamera);
                    cmd.SetGlobalMatrix("_ShadowCasterVP", shadowVP);
                    cmd.SetRenderTarget(m_shadowAtlas);
                    cmd.SetViewport(new Rect(tile.x, tile.y, tile.width, tile.height));
                    context.ExecuteCommandBuffer(cmd);
                    if (!m_shadowCamera.TryGetCullingParameters(out var cullingParameters)) continue;
                    var cullingResults = context.Cull(ref cullingParameters);
                    var sortingSettings = new SortingSettings(m_shadowCamera);
                    var drawingSettings = new DrawingSettings(m_shaderTagID, sortingSettings);
                    var filteringSettings = FilteringSettings.defaultValue;
                    context.DrawRenderers(cullingResults, ref drawingSettings, ref filteringSettings);
                    renderedAny = true;
                    context.Submit();
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }
            return renderedAny;
        }

        public void Dispose()
        {
            if (m_shadowCamera != null)
            {
                GameObject shadowObject = m_shadowCamera.gameObject;
                m_shadowCamera = null;
                MaxRenderResources.Destroy(ref shadowObject);
            }
            MaxRenderResources.UnbindTexture("_MainShadowAtlas", m_shadowAtlas);
            MaxRenderResources.Destroy(ref m_shadowAtlas);
        }
    }

    public class Cascade
    {
        private readonly float[] m_splitEnds = new float[4];
        public float[] m_OrthoWidths = new float[4];
        public float m_OrthoDistance = 500;

        // 主相机视锥体
        private Vector3[] m_farCorners = new Vector3[4];
        private Vector3[] m_nearCorners = new Vector3[4];

        // 主相机划分四个视锥体
        private Vector3[] m_f0Near = new Vector3[4], m_f0Far = new Vector3[4];
        private Vector3[] m_f1Near = new Vector3[4], m_f1Far = new Vector3[4];
        private Vector3[] m_f2Near = new Vector3[4], m_f2Far = new Vector3[4];
        private Vector3[] m_f3Near = new Vector3[4], m_f3Far = new Vector3[4];

        // 主相机视锥体包围盒
        private readonly Vector4[] m_cascadeSpheres = new Vector4[4];

        private CascadeSettings m_cascadeSettings;

        public Cascade(CascadeSettings settings)
        { 
            this.m_cascadeSettings = settings;

            m_OrthoDistance = settings.ShadowDistance;

            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
        }

        public bool UpdateCascade(Camera mainCamera, ref Vector3 lightDir)
        {
            // ShadowDistance is the maximum camera-forward depth, capped by the camera far plane.
            m_OrthoDistance = m_cascadeSettings.ShadowDistance;
            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
            float shadowNear = mainCamera.nearClipPlane;
            float shadowFar = Mathf.Min(mainCamera.farClipPlane, m_OrthoDistance);
            float span = shadowFar - shadowNear;
            Shader.SetGlobalVector(ShaderProperties._CascadeDepthRange,
                new Vector4(shadowNear, shadowFar, span > 0 ? 1.0f / span : 0.0f, 0.0f));
            if (span <= 0) return false;

            // UE-style geometric slice lengths: 1, exponent, exponent^2, ...
            float exponent = Mathf.Clamp(m_cascadeSettings.DistributionExponent, 1.0f, 4.0f);
            float total = 0.0f;
            float weight = 1.0f;
            for (int i = 0; i < 4; ++i) { total += weight; weight *= exponent; }
            float cumulative = 0.0f;
            weight = 1.0f;
            for (int i = 0; i < 4; ++i)
            {
                cumulative += weight / total;
                m_splitEnds[i] = cumulative;
                weight *= exponent;
            }
            m_splitEnds[3] = 1.0f;
            Shader.SetGlobalFloat(ShaderProperties._Split0, m_splitEnds[0]);
            Shader.SetGlobalFloat(ShaderProperties._Split1, m_splitEnds[1]);
            Shader.SetGlobalFloat(ShaderProperties._Split2, m_splitEnds[2]);
            Shader.SetGlobalFloat(ShaderProperties._Split3, m_splitEnds[3]);
            float blend0 = CascadeTransitionWidth(0.0f, m_splitEnds[0], m_splitEnds[1], m_cascadeSettings.CascadeBlendFraction);
            float blend1 = CascadeTransitionWidth(m_splitEnds[0], m_splitEnds[1], m_splitEnds[2], m_cascadeSettings.CascadeBlendFraction);
            float blend2 = CascadeTransitionWidth(m_splitEnds[1], m_splitEnds[2], 1.0f, m_cascadeSettings.CascadeBlendFraction);
            Shader.SetGlobalVector("_CascadeBlendWidths", new Vector4(blend0, blend1, blend2, 0.0f));
            float fadeWidth = span * (1.0f - m_splitEnds[2]) * Mathf.Clamp(m_cascadeSettings.DistanceFadeFraction, 0.0f, 0.5f);
            Shader.SetGlobalVector("_ShadowDistanceFade", new Vector4(shadowFar - fadeWidth, shadowFar, 0.0f, 0.0f));
            // 获取主相机视锥体
            FillFrustumPlaneCornersView(mainCamera, shadowNear, m_nearCorners);
            FillFrustumPlaneCornersView(mainCamera, shadowFar, m_farCorners);

            // 按照比例划分相机视锥体
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = m_farCorners[i] - m_nearCorners[i];

                m_f0Near[i] = m_nearCorners[i];
                m_f0Far[i] = m_nearCorners[i] + dir * m_splitEnds[0];

                m_f1Near[i] = m_nearCorners[i] + dir * (m_splitEnds[0] - blend0);
                m_f1Far[i] = m_nearCorners[i] + dir * m_splitEnds[1];

                m_f2Near[i] = m_nearCorners[i] + dir * (m_splitEnds[1] - blend1);
                m_f2Far[i] = m_nearCorners[i] + dir * m_splitEnds[2];

                m_f3Near[i] = m_nearCorners[i] + dir * (m_splitEnds[2] - blend2);
                m_f3Far[i] = m_farCorners[i];
            }

            // 计算旋转不变的级联包围球
            // Fit in view space: camera rotation/translation cannot change the radius.
            Matrix4x4 cameraToWorld = mainCamera.worldToCameraMatrix.inverse;
            m_cascadeSpheres[0] = StableCascadeSphere(m_f0Near, m_f0Far, cameraToWorld);
            m_cascadeSpheres[1] = StableCascadeSphere(m_f1Near, m_f1Far, cameraToWorld);
            m_cascadeSpheres[2] = StableCascadeSphere(m_f2Near, m_f2Far, cameraToWorld);
            m_cascadeSpheres[3] = StableCascadeSphere(m_f3Near, m_f3Far, cameraToWorld);

            m_cascadeSettings.Set();
            return true;
        }

        private static Quaternion LightRotation(Vector3 lightDir)
        {
            Vector3 up = Mathf.Abs(lightDir.y) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(lightDir, up);
        }

        public static float CascadeTransitionWidth(float leftStart, float boundary, float rightEnd, float fraction)
        {
            return Mathf.Max(0.0f, Mathf.Min(boundary - leftStart, rightEnd - boundary)) * Mathf.Clamp(fraction, 0.0f, 0.3f);
        }
        // Intersect each actual projection ray with the requested view-depth plane.
        // This supports Scene View orthographic/custom projections and custom view
        // matrices, without assuming that the Transform exactly matches the view.
        private static void FillFrustumPlaneCorners(Camera camera, float depth, Vector3[] corners)
        {
            FillFrustumPlaneCornersView(camera, depth, corners);
            Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
            for (int i = 0; i < 4; ++i) corners[i] = cameraToWorld.MultiplyPoint(corners[i]);
        }

        private static void FillFrustumPlaneCornersView(Camera camera, float depth, Vector3[] corners)
        {
            Matrix4x4 inverseProjection = CameraViewport.GetProjection(camera).inverse;
            for (int i = 0; i < 4; ++i)
            {
                float x = i < 2 ? -1.0f : 1.0f;
                float y = i == 0 || i == 3 ? -1.0f : 1.0f;
                // Unity's CPU projection uses -1..1 depth. Use two finite points;
                // depth zero also works for a projection with an infinite far plane.
                Vector3 a = inverseProjection.MultiplyPoint(new Vector3(x, y, -1.0f));
                Vector3 b = inverseProjection.MultiplyPoint(new Vector3(x, y, 0.0f));
                float t = (-depth - a.z) / (b.z - a.z);
                corners[i] = a + (b - a) * t;
            }
        }

        // 齐次坐标矩阵乘法变换
        Vector3 matTransform(Matrix4x4 m, Vector3 v, float w)
        {
            Vector4 v4 = new Vector4(v.x, v.y, v.z, w);
            v4 = m * v4;
            return new Vector3(v4.x, v4.y, v4.z);
        }

        // 从相机空间分段计算球半径，仅把球心转换到世界空间。
        private static Vector4 StableCascadeSphere(Vector3[] nearCorners, Vector3[] farCorners, Matrix4x4 cameraToWorld)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < 4; ++i) center += nearCorners[i] + farCorners[i];
            center *= 0.125f;
            float radius = 0.0f;
            for (int i = 0; i < 4; ++i)
            {
                radius = Mathf.Max(radius, Vector3.Distance(center, nearCorners[i]));
                radius = Mathf.Max(radius, Vector3.Distance(center, farCorners[i]));
            }
            Vector3 worldCenter = cameraToWorld.MultiplyPoint(center);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, radius);
        }

        // Rotation-invariant extent plus a world-anchored texel grid.
        public float ConfigCameraToShadowSpace(Camera camera, Vector3 lightDir, int cascade, float distance, float resolution)
        {
            Vector4 sphere = m_cascadeSpheres[cascade];
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            float diameter = 2.0f * sphere.w;
            // Include kernel support and normal offset before snapping the center.
            ShadowSettings settings = m_cascadeSettings.GetCascade(cascade);
            float padding = Mathf.Max(settings.MaxPenumbraRadius, 0.0f) + Mathf.Abs(settings.ShadingPointNormalBias);
            float len = (Mathf.Max(0.001f, diameter) + 2.0f * padding) * resolution / (resolution - 2.0f);
            m_OrthoWidths[cascade] = len;
            float disPerPix = len / resolution;

            Matrix4x4 toShadowViewInv = Matrix4x4.TRS(Vector3.zero, LightRotation(lightDir), Vector3.one);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;
            if (cascade == 0)
            {
                Shader.SetGlobalMatrix("_ShadowNoiseWorldToLight", toShadowView);
                // Shadow texels are too coarse as random-rotation cells at close range.
                // Use subpixel cells in close views so a quad does not reuse the same random set.
                // This changes sampling phase density, not the stable shadow projection.
                Shader.SetGlobalFloat("_ShadowNoiseWorldTexelSize", disPerPix / 128.0f);
            }

            // 相机坐标旋转到光源坐标系下取整
            center = matTransform(toShadowView, center, 1.0f);
            for (int i = 0; i < 3; i++)
                center[i] = Mathf.Floor(center[i] / disPerPix) * disPerPix;
            center = matTransform(toShadowViewInv, center, 1.0f);

            // 配置相机
            camera.transform.rotation = LightRotation(lightDir);
            camera.transform.position = center;
            // Fit light-space depth too: wide/orthographic Scene views can have
            // a larger depth extent than the camera-forward shadow distance.
            float halfDepthRange = Mathf.Max(distance, sphere.w + disPerPix + Mathf.Abs(settings.ShadingPointNormalBias));
            camera.nearClipPlane = -halfDepthRange;
            camera.farClipPlane = halfDepthRange;
            camera.aspect = 1.0f;
            camera.orthographicSize = len * 0.5f;
            return halfDepthRange;
        }

        private class ShaderProperties
        {
            public static readonly int _OrthoDistance = Shader.PropertyToID("_OrthoDistance");
            public static readonly int _CascadeDepthRange = Shader.PropertyToID("_CascadeDepthRange");
            public static readonly int _Split0 = Shader.PropertyToID("_Split0");
            public static readonly int _Split1 = Shader.PropertyToID("_Split1");
            public static readonly int _Split2 = Shader.PropertyToID("_Split2");
            public static readonly int _Split3 = Shader.PropertyToID("_Split3");
        }
    }
}
