using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public static class ShaderBindings
    {
        // Time constants
        public static int _Time = Shader.PropertyToID("_Time");
        public static int _SinTime = Shader.PropertyToID("_SinTime");
        public static int _CosTime = Shader.PropertyToID("_CosTime");
        public static int unity_DeltaTime = Shader.PropertyToID("unity_DeltaTime");

        // Ambient and Fog constants
        //public static int ambientSky = Shader.PropertyToID("unity_AmbientSky");
        //public static int ambientEquator = Shader.PropertyToID("unity_AmbientEquator");
        //public static int ambientGround = Shader.PropertyToID("unity_AmbientGround");
        //public static int fogColor = Shader.PropertyToID("unity_FogColor");
        //public static int fogParams = Shader.PropertyToID("unity_FogParams");

        // Camera and Screen
        public static int _WorldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        public static int _ProjectionParams = Shader.PropertyToID("_ProjectionParams");
        public static int _ScreenParams = Shader.PropertyToID("_ScreenParams");
        public static int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        public static int unity_OrthoParams = Shader.PropertyToID("unity_OrthoParams");
        public static int unity_CameraProjection = Shader.PropertyToID("unity_CameraProjection");
        public static int unity_CameraInvProjection = Shader.PropertyToID("unity_CameraInvProjection");
        public static int unity_CameraWorldClipPlanes = Shader.PropertyToID("unity_CameraWorldClipPlanes");

        // Transformations
        public static int unity_MatrixV = Shader.PropertyToID("unity_MatrixV");
        public static int unity_MatrixP = Shader.PropertyToID("unity_MatrixP");
        public static int unity_MatrixVP = Shader.PropertyToID("unity_MatrixVP");

        public static int unity_MatrixInvV = Shader.PropertyToID("unity_MatrixInvV");
        public static int unity_MatrixInvP = Shader.PropertyToID("unity_MatrixInvP");
        public static int unity_MatrixInvVP = Shader.PropertyToID("unity_MatrixInvVP");

        public static void SetPerFrameShaderVariables(ScriptableRenderContext context)
        {
#if UNITY_EDITOR
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
            float time = Time.time;
#endif
            float deltaTime = Time.deltaTime;
            float smoothDeltaTime = Time.smoothDeltaTime;

            float timeEights = time / 8f;
            float timeFourth = time / 4f;
            float timeHalf = time / 2f;

            // Time values
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

        public static void SetPerCameraShaderVariables(ScriptableRenderContext context, Camera camera, bool renderIntoTexture = true,
            Matrix4x4? gpuProjectionOverride = null)
        {
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            float cameraWidth = renderSize.x;
            float cameraHeight = renderSize.y;

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

            // From http://www.humus.name/temp/Linearize%20depth.txt
            // But as depth component textures on OpenGL always return in 0..1 range (as in D3D), we have to use
            // the same constants for both D3D and OpenGL here.
            // OpenGL would be this:
            // zc0 = (1.0 - far / near) / 2.0;
            // zc1 = (1.0 + far / near) / 2.0;
            // D3D is this:
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
            // Match the actual orthographic projection, including viewport-dependent Game sizing.
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
                // TODO: missing unity_CameraWorldClipPlanes[6], currently set by context.SetupCameraProperties

                // Upload the already GPU-adjusted matrices directly for this custom SRP.
                // Do not route them through the Built-in-only SetViewProjectionMatrices API.
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixV, viewMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixP, projMatrix);
                cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixVP, viewProjMatrix);

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
