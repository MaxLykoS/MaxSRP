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
        public const int SHADOWMAP_RESOLUTION = 1024;
        private RenderTexture[] m_shadowTexture;
        private Cascade m_cascade;
        private Camera m_shadowCamera;
        private ShaderTagId m_shaderTagID = new ShaderTagId("ShadowCaster");
        public MaxShadowCasterPass(CascadeSettings cascadeSettings)
        {
            try
            {
                m_cascade = new Cascade(cascadeSettings);
                m_shadowTexture = new RenderTexture[4];
                for (int i = 0; i < m_shadowTexture.Length; ++i)
                {
                    m_shadowTexture[i] = new RenderTexture(SHADOWMAP_RESOLUTION, SHADOWMAP_RESOLUTION,
                        24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
                    m_shadowTexture[i].wrapMode = TextureWrapMode.Clamp;
                    Shader.SetGlobalTexture("_MainShadowMap" + i, m_shadowTexture[i]);
                }
                Shader.SetGlobalInteger(ShaderProperties._ShadowMapResolution, SHADOWMAP_RESOLUTION);
                Shader.SetGlobalTexture(ShaderProperties._BlueNoiseTexture, cascadeSettings.BlueNoiseTexture);
                Shader.SetGlobalInteger(ShaderProperties._BlueNoiseTextureResolution, cascadeSettings.BlueNoiseTexture.width);
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        public bool Execute(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            if (!sun.CastsShadows) return false;
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

            for (int i = 0; i < 4; ++i)
            {
                float halfDepthRange = m_cascade.ConfigCameraToShadowSpace(m_shadowCamera, lightDir, i, m_cascade.m_OrthoDistance, SHADOWMAP_RESOLUTION);
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
                    cmd.SetRenderTarget(m_shadowTexture[i]);
                    cmd.SetViewport(new Rect(0, 0, SHADOWMAP_RESOLUTION, SHADOWMAP_RESOLUTION));
                    cmd.ClearRenderTarget(true, true, Color.clear);
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
            if (m_shadowTexture == null) return;
            for (int i = 0; i < m_shadowTexture.Length; ++i)
            {
                MaxRenderResources.UnbindTexture("_MainShadowMap" + i, m_shadowTexture[i]);
                MaxRenderResources.Destroy(ref m_shadowTexture[i]);
            }
        }
        private static class ShaderProperties
        {
            public static readonly int _ShadowMapResolution = Shader.PropertyToID("_ShadowMapResolution");
            public static readonly int _MainShadowMap0 = Shader.PropertyToID("_MainShadowMap0");
            public static readonly int _MainShadowMap1 = Shader.PropertyToID("_MainShadowMap1");
            public static readonly int _MainShadowMap2 = Shader.PropertyToID("_MainShadowMap2");
            public static readonly int _MainShadowMap3 = Shader.PropertyToID("_MainShadowMap3");

            public static readonly int _BlueNoiseTexture = Shader.PropertyToID("_NoiseTexture");
            public static readonly int _BlueNoiseTextureResolution = Shader.PropertyToID("_NoiseTextureResolution");
        }
    }

    public class Cascade
    {
        // 分割参数
        public float[] m_Splts = { 0.07f, 0.13f, 0.25f, 0.55f };
        private readonly float[] m_splitEnds = new float[4];
        public float[] m_OrthoWidths = new float[4];
        public float m_OrthoDistance = 500;
        public float m_LightSize = 2.0f;

        // 主相机视锥体
        private Vector3[] m_farCorners = new Vector3[4];
        private Vector3[] m_nearCorners = new Vector3[4];

        // 主相机划分四个视锥体
        private Vector3[] m_f0Near = new Vector3[4], m_f0Far = new Vector3[4];
        private Vector3[] m_f1Near = new Vector3[4], m_f1Far = new Vector3[4];
        private Vector3[] m_f2Near = new Vector3[4], m_f2Far = new Vector3[4];
        private Vector3[] m_f3Near = new Vector3[4], m_f3Far = new Vector3[4];

        // 主相机视锥体包围盒
        private Vector3[] m_box0, m_box1, m_box2, m_box3;

        private CascadeSettings m_cascadeSettings;

        public Cascade(CascadeSettings settings)
        { 
            this.m_cascadeSettings = settings;

            m_OrthoDistance = settings.ShadowDistance;

            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
            Shader.SetGlobalFloat(ShaderProperties._LightSize, m_LightSize);
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

            // The four weights define contiguous slices; publish cumulative endpoints.
            float total = 0.0f;
            for (int i = 0; i < 4; ++i) total += Mathf.Max(m_Splts[i], 0.0f);
            float cumulative = 0.0f;
            for (int i = 0; i < 4; ++i)
            {
                cumulative += total > 0 ? Mathf.Max(m_Splts[i], 0.0f) / total : 0.25f;
                m_splitEnds[i] = cumulative;
            }
            m_splitEnds[3] = 1.0f;
            Shader.SetGlobalFloat(ShaderProperties._Split0, m_splitEnds[0]);
            Shader.SetGlobalFloat(ShaderProperties._Split1, m_splitEnds[1]);
            Shader.SetGlobalFloat(ShaderProperties._Split2, m_splitEnds[2]);
            Shader.SetGlobalFloat(ShaderProperties._Split3, m_splitEnds[3]);
            // 获取主相机视锥体
            FillFrustumPlaneCorners(mainCamera, shadowNear, m_nearCorners);
            FillFrustumPlaneCorners(mainCamera, shadowFar, m_farCorners);

            // 按照比例划分相机视锥体
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = m_farCorners[i] - m_nearCorners[i];

                m_f0Near[i] = m_nearCorners[i];
                m_f0Far[i] = m_nearCorners[i] + dir * m_splitEnds[0];

                m_f1Near[i] = m_f0Far[i];
                m_f1Far[i] = m_nearCorners[i] + dir * m_splitEnds[1];

                m_f2Near[i] = m_f1Far[i];
                m_f2Far[i] = m_nearCorners[i] + dir * m_splitEnds[2];

                m_f3Near[i] = m_f2Far[i];
                m_f3Far[i] = m_farCorners[i];
            }

            // 计算包围盒
            m_box0 = LightSpaceAABB(m_f0Near, m_f0Far, lightDir);
            m_box1 = LightSpaceAABB(m_f1Near, m_f1Far, lightDir);
            m_box2 = LightSpaceAABB(m_f2Near, m_f2Far, lightDir);
            m_box3 = LightSpaceAABB(m_f3Near, m_f3Far, lightDir);

            m_cascadeSettings.Set();
            return true;
        }

        private static Quaternion LightRotation(Vector3 lightDir)
        {
            Vector3 up = Mathf.Abs(lightDir.y) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(lightDir, up);
        }
        // Intersect each actual projection ray with the requested view-depth plane.
        // This supports Scene View orthographic/custom projections and custom view
        // matrices, without assuming that the Transform exactly matches the view.
        private static void FillFrustumPlaneCorners(Camera camera, float depth, Vector3[] corners)
        {
            Matrix4x4 inverseProjection = CameraViewport.GetProjection(camera).inverse;
            Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
            for (int i = 0; i < 4; ++i)
            {
                float x = i < 2 ? -1.0f : 1.0f;
                float y = i == 0 || i == 3 ? -1.0f : 1.0f;
                // Unity's CPU projection uses -1..1 depth. Use two finite points;
                // depth zero also works for a projection with an infinite far plane.
                Vector3 a = inverseProjection.MultiplyPoint(new Vector3(x, y, -1.0f));
                Vector3 b = inverseProjection.MultiplyPoint(new Vector3(x, y, 0.0f));
                float t = (-depth - a.z) / (b.z - a.z);
                corners[i] = cameraToWorld.MultiplyPoint(a + (b - a) * t);
            }
        }

        // 齐次坐标矩阵乘法变换
        Vector3 matTransform(Matrix4x4 m, Vector3 v, float w)
        {
            Vector4 v4 = new Vector4(v.x, v.y, v.z, w);
            v4 = m * v4;
            return new Vector3(v4.x, v4.y, v4.z);
        }

        // 计算光源方向包围盒的世界坐标
        Vector3[] LightSpaceAABB(Vector3[] nearCorners, Vector3[] farCorners, Vector3 lightDir)
        {
            Matrix4x4 toShadowViewInv = Matrix4x4.TRS(Vector3.zero, LightRotation(lightDir), Vector3.one);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;

            // 视锥体顶点转光源方向
            for (int i = 0; i < 4; i++)
            {
                farCorners[i] = matTransform(toShadowView, farCorners[i], 1.0f);
                nearCorners[i] = matTransform(toShadowView, nearCorners[i], 1.0f);
            }

            // 计算 AABB 包围盒
            float[] x = new float[8];
            float[] y = new float[8];
            float[] z = new float[8];
            for (int i = 0; i < 4; i++)
            {
                x[i] = nearCorners[i].x; x[i + 4] = farCorners[i].x;
                y[i] = nearCorners[i].y; y[i + 4] = farCorners[i].y;
                z[i] = nearCorners[i].z; z[i + 4] = farCorners[i].z;
            }
            float xmin = Mathf.Min(x), xmax = Mathf.Max(x);
            float ymin = Mathf.Min(y), ymax = Mathf.Max(y);
            float zmin = Mathf.Min(z), zmax = Mathf.Max(z);

            // 包围盒顶点转世界坐标
            Vector3[] points = {
            new Vector3(xmin, ymin, zmin), new Vector3(xmin, ymin, zmax), new Vector3(xmin, ymax, zmin), new Vector3(xmin, ymax, zmax),
            new Vector3(xmax, ymin, zmin), new Vector3(xmax, ymin, zmax), new Vector3(xmax, ymax, zmin), new Vector3(xmax, ymax, zmax)
        };
            for (int i = 0; i < 8; i++)
                points[i] = matTransform(toShadowViewInv, points[i], 1.0f);

            // 视锥体顶还原
            for (int i = 0; i < 4; i++)
            {
                farCorners[i] = matTransform(toShadowViewInv, farCorners[i], 1.0f);
                nearCorners[i] = matTransform(toShadowViewInv, nearCorners[i], 1.0f);
            }

            return points;
        }

        // 将相机配置为第 i 级阴影贴图的绘制模式
        public float ConfigCameraToShadowSpace(Camera camera, Vector3 lightDir, int cascade, float distance, float resolution)
        {
            // 选择第 cascade 级视锥划分
            var box = new Vector3[8];
            if (cascade == 0) { box = m_box0; }
            if (cascade == 1) { box = m_box1; }
            if (cascade == 2) { box = m_box2; }
            if (cascade == 3) { box = m_box3; }

            // 计算 Box 中点, 宽高比
            Vector3 center = (box[3] + box[4]) / 2;
            float w = Vector3.Magnitude(box[0] - box[4]);
            float h = Vector3.Magnitude(box[0] - box[2]);
            // Cover the light-space AABB, including one texel of center snapping per side.
            float len = Mathf.Max(0.001f, Mathf.Max(h, w)) * resolution / (resolution - 2.0f);
            m_OrthoWidths[cascade] = len;
            float disPerPix = len / resolution;

            Matrix4x4 toShadowViewInv = Matrix4x4.TRS(Vector3.zero, LightRotation(lightDir), Vector3.one);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;

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
            float halfDepthRange = Mathf.Max(distance, Vector3.Magnitude(box[0] - box[1]) * 0.5f + disPerPix);
            camera.nearClipPlane = -halfDepthRange;
            camera.farClipPlane = halfDepthRange;
            camera.aspect = 1.0f;
            camera.orthographicSize = len * 0.5f;
            return halfDepthRange;
        }

        private class ShaderProperties
        {
            public static readonly int _OrthoDistance = Shader.PropertyToID("_OrthoDistance");
            public static readonly int _LightSize = Shader.PropertyToID("_LightSize");
            public static readonly int _CascadeDepthRange = Shader.PropertyToID("_CascadeDepthRange");
            public static readonly int _Split0 = Shader.PropertyToID("_Split0");
            public static readonly int _Split1 = Shader.PropertyToID("_Split1");
            public static readonly int _Split2 = Shader.PropertyToID("_Split2");
            public static readonly int _Split3 = Shader.PropertyToID("_Split3");
        }
    }
}
