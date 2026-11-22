using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public static class ShaderBindings
    {
        // 时间常量
        public static int _Time = Shader.PropertyToID("_Time");
        public static int _SinTime = Shader.PropertyToID("_SinTime");
        public static int _CosTime = Shader.PropertyToID("_CosTime");
        public static int unity_DeltaTime = Shader.PropertyToID("unity_DeltaTime");

        // 环境光和雾效常量
        //public static int ambientSky = Shader.PropertyToID("unity_AmbientSky");
        //public static int ambientEquator = Shader.PropertyToID("unity_AmbientEquator");
        //public static int ambientGround = Shader.PropertyToID("unity_AmbientGround");
        //public static int fogColor = Shader.PropertyToID("unity_FogColor");
        //public static int fogParams = Shader.PropertyToID("unity_FogParams");

        // 相机和屏幕参数
        public static int _WorldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        public static int _ProjectionParams = Shader.PropertyToID("_ProjectionParams");
        public static int _ScreenParams = Shader.PropertyToID("_ScreenParams");
        public static int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        public static int unity_OrthoParams = Shader.PropertyToID("unity_OrthoParams");
        public static int unity_CameraProjection = Shader.PropertyToID("unity_CameraProjection");
        public static int unity_CameraInvProjection = Shader.PropertyToID("unity_CameraInvProjection");
        public static int unity_CameraWorldClipPlanes = Shader.PropertyToID("unity_CameraWorldClipPlanes");

        // 变换矩阵
        public static int unity_MatrixV = Shader.PropertyToID("unity_MatrixV");
        public static int unity_MatrixP = Shader.PropertyToID("unity_MatrixP");
        public static int unity_MatrixVP = Shader.PropertyToID("unity_MatrixVP");
        private static readonly int s_rasterVP = Shader.PropertyToID("_MaxRasterVP");

        public static int unity_MatrixInvV = Shader.PropertyToID("unity_MatrixInvV");
        public static int unity_MatrixInvP = Shader.PropertyToID("unity_MatrixInvP");
        public static int unity_MatrixInvVP = Shader.PropertyToID("unity_MatrixInvVP");

        // 为当前渲染批次上传与 Unity 约定一致的时间参数。
        public static void SetPerFrameShaderVariables(ScriptableRenderContext context)
        {
#if UNITY_EDITOR
            // 在编辑模式下使用实时钟，让 Shader 动画继续推进。
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
            float time = Time.time;
#endif
            float deltaTime = Time.deltaTime;
            float smoothDeltaTime = Time.smoothDeltaTime;

            float timeEights = time / 8f;
            float timeFourth = time / 4f;
            float timeHalf = time / 2f;

            // 生成 Shader 使用的时间、三角函数和帧间隔参数。
            Vector4 timeVector = time * new Vector4(1f / 20f, 1f, 2f, 3f);
            Vector4 sinTimeVector = new Vector4(Mathf.Sin(timeEights), Mathf.Sin(timeFourth), Mathf.Sin(timeHalf), Mathf.Sin(time));
            Vector4 cosTimeVector = new Vector4(Mathf.Cos(timeEights), Mathf.Cos(timeFourth), Mathf.Cos(timeHalf), Mathf.Cos(time));
            Vector4 deltaTimeVector = new Vector4(deltaTime, 1f / deltaTime, smoothDeltaTime, 1f / smoothDeltaTime);

            CommandBuffer cmd = CommandBufferPool.Get(nameof(SetPerFrameShaderVariables));
            try
            {
                cmd.BeginSample(nameof(SetPerFrameShaderVariables));
                cmd.SetGlobalVector(ShaderBindings._Time, timeVector);
                cmd.SetGlobalVector(ShaderBindings._SinTime, sinTimeVector);
                cmd.SetGlobalVector(ShaderBindings._CosTime, cosTimeVector);
                cmd.SetGlobalVector(ShaderBindings.unity_DeltaTime, deltaTimeVector);
                cmd.EndSample(nameof(SetPerFrameShaderVariables));
                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        // 上传相机参数，以及光栅化和深度重建共用的 GPU 投影矩阵。
        public static void SetPerCameraShaderVariables(ScriptableRenderContext context, Camera camera, bool renderIntoTexture = true,
            Matrix4x4? gpuProjectionOverride = null)
        {
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            float cameraWidth = renderSize.x;
            float cameraHeight = renderSize.y;

            // 覆盖传入的投影矩阵已经转换到 GPU 裁剪空间，可以包含 TAA 抖动。
            Matrix4x4 cameraProjection = CameraViewport.GetProjection(camera);
            Matrix4x4 projMatrix = gpuProjectionOverride ?? GL.GetGPUProjectionMatrix(cameraProjection, renderIntoTexture);
            Matrix4x4 viewMatrix = camera.worldToCameraMatrix;
            Matrix4x4 viewProjMatrix = projMatrix * viewMatrix;

            Matrix4x4 invViewMatrix = Matrix4x4.Inverse(viewMatrix);
            Matrix4x4 invProjectionMatrix = Matrix4x4.Inverse(projMatrix);
            Matrix4x4 invViewProjMatrix = Matrix4x4.Inverse(viewProjMatrix);

            float projectionFlip = projMatrix.m11 * cameraProjection.m11 < 0.0f ? -1.0f : 1.0f;

            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            float invNear = Mathf.Approximately(near, 0.0f) ? 0.0f : 1.0f / near;
            float invFar = Mathf.Approximately(far, 0.0f) ? 0.0f : 1.0f / far;
            float isOrthographic = camera.orthographic ? 1.0f : 0.0f;

            // 深度线性化公式参考 http://www.humus.name/temp/Linearize%20depth.txt
            // OpenGL 和 D3D 的深度纹理采样值都在 0 到 1 之间。
            // 因此这里为两种图形后端使用相同的系数。
            // OpenGL 裁剪空间原本使用下列系数。
            // zc0 = (1.0 - far / near) / 2.0;
            // zc1 = (1.0 + far / near) / 2.0;
            // 此处采用下列 D3D 系数。
            float zc0 = 1.0f - far * invNear;
            float zc1 = far * invNear;

            Vector4 zBufferParams = new Vector4(zc0, zc1, zc0 * invFar, zc1 * invFar);

            if (SystemInfo.usesReversedZBuffer)
            {
                zBufferParams.y += zBufferParams.x;
                zBufferParams.x = -zBufferParams.x;
                zBufferParams.w += zBufferParams.z;
                zBufferParams.z = -zBufferParams.z;
            }

            Vector4 projectionParams = new Vector4(projectionFlip, near, far, 1.0f * invFar);

            // 按实际正交投影计算尺寸，包含游戏相机视口造成的尺寸调整。
            float orthoWidth = camera.orthographicSize;
            float orthoHeight = camera.orthographicSize;
            if (camera.orthographic)
            {
                orthoWidth = Mathf.Abs(1.0f / cameraProjection.m00);
                orthoHeight = Mathf.Abs(1.0f / cameraProjection.m11);
            }
            Vector4 orthoParams = new Vector4(orthoWidth, orthoHeight, 0.0f, isOrthographic);

            CommandBuffer cmd = CommandBufferPool.Get(nameof(SetPerCameraShaderVariables));
            try
            {
                cmd.SetGlobalVector(ShaderBindings._WorldSpaceCameraPos, camera.transform.position);
                cmd.SetGlobalVector(ShaderBindings._ProjectionParams, projectionParams);
                cmd.SetGlobalVector(ShaderBindings._ScreenParams, new Vector4(cameraWidth, cameraHeight, 1.0f + 1.0f / cameraWidth, 1.0f + 1.0f / cameraHeight));
                cmd.SetGlobalVector(ShaderBindings._ZBufferParams, zBufferParams);
                cmd.SetGlobalVector(ShaderBindings.unity_OrthoParams, orthoParams);
                // 裁剪平面 unity_CameraWorldClipPlanes[6] 目前由 context.SetupCameraProperties 设置。

                // 自定义 SRP 直接上传已经转换到 GPU 裁剪空间的矩阵。
                // SetViewProjectionMatrices 仅适用于内置渲染管线。
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixV, viewMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixP, projMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixVP, viewProjMatrix);

                // Unity 的 DrawRenderers 会提供引擎内置的无抖动视图投影矩阵。
                // GBuffer 和运动向量光栅化使用 SRP 自己绑定的矩阵。
                cmd.SetGlobalMatrix(s_rasterVP, viewProjMatrix);

                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixInvV, invViewMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixInvP, invProjectionMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixInvVP, invViewProjMatrix);

                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }
    }
}
