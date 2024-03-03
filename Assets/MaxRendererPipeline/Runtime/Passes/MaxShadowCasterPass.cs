using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace MaxSRP
{
    public class MaxShadowCasterPass
    {
        public const int SHADOWMAP_RESOLUTION = 1024;
        private RenderTexture[] m_shadowTexture;
        private Cascade m_cascade;
        private ShaderTagId m_shaderTagID = new ShaderTagId("ShadowCaster");
        public MaxShadowCasterPass(CascadeSettings cascadeSettings)
        {
            m_cascade = new Cascade(cascadeSettings);

            m_shadowTexture = new RenderTexture[4];
            for (int i = 0; i < 4; i++)
            {
                m_shadowTexture[i] = new RenderTexture(SHADOWMAP_RESOLUTION, SHADOWMAP_RESOLUTION, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
                m_shadowTexture[i].wrapMode = TextureWrapMode.Clamp;
            }

            Shader.SetGlobalTexture(ShaderProperties._MainShadowMap0, m_shadowTexture[0]);
            Shader.SetGlobalTexture(ShaderProperties._MainShadowMap1, m_shadowTexture[1]);
            Shader.SetGlobalTexture(ShaderProperties._MainShadowMap2, m_shadowTexture[2]);
            Shader.SetGlobalTexture(ShaderProperties._MainShadowMap3, m_shadowTexture[3]);
            
            Shader.SetGlobalInteger(ShaderProperties._ShadowMapResolution, SHADOWMAP_RESOLUTION);
            Shader.SetGlobalTexture(ShaderProperties._BlueNoiseTexture, cascadeSettings.BlueNoiseTexture);
            Shader.SetGlobalInteger(ShaderProperties._BlueNoiseTextureResolution, cascadeSettings.BlueNoiseTexture.width);
        }

        public void Execute(ScriptableRenderContext context, Camera camera)
        {
            Light mainLight = RenderSettings.sun;
            Vector3 lightDir = mainLight.transform.rotation * Vector3.forward;

            m_cascade.UpdateCascade(camera, ref lightDir);

            m_cascade.SaveMainCameraSettings(camera);
            for (int i = 0; i < 4; ++i)
            {
                // 将相机移动到光源方向
                m_cascade.ConfigCameraToShadowSpace(camera, lightDir, i, m_cascade.m_OrthoDistance, SHADOWMAP_RESOLUTION);

                // 设置阴影矩阵，视锥分割参数
                Matrix4x4 v = camera.worldToCameraMatrix;
                Matrix4x4 p = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
                Shader.SetGlobalMatrix("_ShadowVPMatrix" + i, p * v);
                Shader.SetGlobalFloat("_OrthoWidth" + i, m_cascade.m_OrthoWidths[i]);

                CommandBuffer cmd = CommandBufferPool.Get("ShadowMap");
                
                // 绘制前准备
                context.SetupCameraProperties(camera);
                cmd.SetRenderTarget(m_shadowTexture[i]);
                cmd.ClearRenderTarget(true, true, Color.clear);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // 剔除
                camera.TryGetCullingParameters(out var cullingParameters);
                var cullingResults = context.Cull(ref cullingParameters);
                // config settings
                SortingSettings sortingSettings = new SortingSettings(camera);
                DrawingSettings drawingSettings = new DrawingSettings(m_shaderTagID, sortingSettings);
                FilteringSettings filteringSettings = FilteringSettings.defaultValue;

                // 绘制
                context.DrawRenderers(cullingResults, ref drawingSettings, ref filteringSettings);
                context.Submit();   // 每次 set camera 之后立即提交
            }
            m_cascade.RevertMainCameraSettings(camera);
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

        struct MainCameraSettings
        {
            public Vector3 position;
            public Quaternion rotation;
            public float nearClipPlane;
            public float farClipPlane;
            public float aspect;
        };
        private MainCameraSettings m_settings;
        private CascadeSettings m_cascadeSettings;

        public Cascade(CascadeSettings settings)
        { 
            this.m_cascadeSettings = settings;

            m_OrthoDistance = settings.ShadowDistance;

            Shader.SetGlobalFloat(ShaderProperties._Split0, m_Splts[0]);
            Shader.SetGlobalFloat(ShaderProperties._Split1, m_Splts[1]);
            Shader.SetGlobalFloat(ShaderProperties._Split2, m_Splts[2]);
            Shader.SetGlobalFloat(ShaderProperties._Split3, m_Splts[3]);
            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
            Shader.SetGlobalFloat(ShaderProperties._LightSize, m_LightSize);
        }

        public void UpdateCascade(Camera mainCamera, ref Vector3 lightDir)
        {
            // 获取主相机视锥体
            mainCamera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), mainCamera.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_farCorners);
            mainCamera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), mainCamera.nearClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_nearCorners);

            // 视锥体顶点转世界坐标
            for (int i = 0; i < 4; i++)
            {
                m_farCorners[i] = mainCamera.transform.TransformVector(m_farCorners[i]) + mainCamera.transform.position;
                m_nearCorners[i] = mainCamera.transform.TransformVector(m_nearCorners[i]) + mainCamera.transform.position;
            }

            // 按照比例划分相机视锥体
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = m_farCorners[i] - m_nearCorners[i];

                m_f0Near[i] = m_nearCorners[i];
                m_f0Far[i] = m_f0Near[i] + dir * m_Splts[0];

                m_f1Near[i] = m_nearCorners[i];
                m_f1Far[i] = m_f1Near[i] + dir * m_Splts[1];

                m_f2Near[i] = m_nearCorners[i];
                m_f2Far[i] = m_f2Near[i] + dir * m_Splts[2];

                m_f3Near[i] = m_nearCorners[i];
                m_f3Far[i] = m_f3Near[i] + dir * m_Splts[3];
            }

            // 计算包围盒
            m_box0 = LightSpaceAABB(m_f0Near, m_f0Far, lightDir);
            m_box1 = LightSpaceAABB(m_f1Near, m_f1Far, lightDir);
            m_box2 = LightSpaceAABB(m_f2Near, m_f2Far, lightDir);
            m_box3 = LightSpaceAABB(m_f3Near, m_f3Far, lightDir);

            // 更新 Ortho width
            m_OrthoWidths[0] = Vector3.Magnitude(m_f0Far[2] - m_f0Near[0]);
            m_OrthoWidths[1] = Vector3.Magnitude(m_f1Far[2] - m_f1Near[0]);
            m_OrthoWidths[2] = Vector3.Magnitude(m_f2Far[2] - m_f2Near[0]);
            m_OrthoWidths[3] = Vector3.Magnitude(m_f3Far[2] - m_f3Near[0]);

            m_cascadeSettings.Set();
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
            Matrix4x4 toShadowViewInv = Matrix4x4.LookAt(Vector3.zero, lightDir, Vector3.up);
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
        public void ConfigCameraToShadowSpace(Camera camera, Vector3 lightDir, int cascade, float distance, float resolution)
        {
            // 选择第 cascade 级视锥划分
            var box = new Vector3[8];
            var f_near = new Vector3[4]; var f_far = new Vector3[4];
            if (cascade == 0) { box = m_box0; f_near = m_f0Near; f_far = m_f0Far; }
            if (cascade == 1) { box = m_box1; f_near = m_f1Near; f_far = m_f1Far; }
            if (cascade == 2) { box = m_box2; f_near = m_f2Near; f_far = m_f2Far; }
            if (cascade == 3) { box = m_box3; f_near = m_f3Near; f_far = m_f3Far; }

            // 计算 Box 中点, 宽高比
            Vector3 center = (box[3] + box[4]) / 2;
            float w = Vector3.Magnitude(box[0] - box[4]);
            float h = Vector3.Magnitude(box[0] - box[2]);
            //float len = Mathf.Max(h, w);
            float len = Vector3.Magnitude(f_far[2] - f_near[0]);
            float disPerPix = len / resolution;

            Matrix4x4 toShadowViewInv = Matrix4x4.LookAt(Vector3.zero, lightDir, Vector3.up);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;

            // 相机坐标旋转到光源坐标系下取整
            center = matTransform(toShadowView, center, 1.0f);
            for (int i = 0; i < 3; i++)
                center[i] = Mathf.Floor(center[i] / disPerPix) * disPerPix;
            center = matTransform(toShadowViewInv, center, 1.0f);

            // 配置相机
            camera.transform.rotation = Quaternion.LookRotation(lightDir);
            camera.transform.position = center;
            camera.nearClipPlane = -distance;
            camera.farClipPlane = distance;
            camera.aspect = 1.0f;
            camera.orthographicSize = len * 0.5f;
        }

        // 保存相机参数, 更改为正交投影
        public void SaveMainCameraSettings(Camera camera)
        {
            m_settings.position = camera.transform.position;
            m_settings.rotation = camera.transform.rotation;
            m_settings.farClipPlane = camera.farClipPlane;
            m_settings.nearClipPlane = camera.nearClipPlane;
            m_settings.aspect = camera.aspect;
            camera.orthographic = true;
        }

        // 还原相机参数, 更改为透视投影
        public void RevertMainCameraSettings(Camera camera)
        {
            camera.transform.position = m_settings.position;
            camera.transform.rotation = m_settings.rotation;
            camera.farClipPlane = m_settings.farClipPlane;
            camera.nearClipPlane = m_settings.nearClipPlane;
            camera.aspect = m_settings.aspect;

            camera.orthographic = false;
        }

        private class ShaderProperties
        {
            public static readonly int _OrthoDistance = Shader.PropertyToID("_OrthoDistance");
            public static readonly int _LightSize = Shader.PropertyToID("_LightSize");
            public static readonly int _Split0 = Shader.PropertyToID("_Split0");
            public static readonly int _Split1 = Shader.PropertyToID("_Split1");
            public static readonly int _Split2 = Shader.PropertyToID("_Split2");
            public static readonly int _Split3 = Shader.PropertyToID("_Split3");
        }
    }
}
