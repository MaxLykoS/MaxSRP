using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public enum ToneMappingMode { None, ACES }

    [Serializable]
    public sealed class ColorOutputSettings
    {
        [Tooltip("Manual camera EV at ISO 100. Higher values darken the image; 15 is an outdoor daylight starting point, 6-9 for lit interiors.")]
        [Range(-10.0f, 24.0f)] public float CameraEV100 = 15.0f;
        [Tooltip("Exposure compensation in stops, applied to all lights and the sky. +1 doubles brightness.")]
        [Range(-16.0f, 16.0f)] public float ExposureEV = 0.0f;
        public ToneMappingMode ToneMapping = ToneMappingMode.ACES;
        [Tooltip("Reduce banding in Game and Scene views with subtle color dithering. High-precision camera exports skip dithering.")]
        public bool Dithering = true;
        [SerializeField] private Shader m_colorOutputShader;
        public Shader Shader => m_colorOutputShader;

        public float GetExposureMultiplier()
        {
            // Saturation-based camera calibration: Lmax = 1.2 * 2^EV100.
            // Scene HDR stores cd/m² / HDRScale; compensate for that scale once.
            float camera = 1.2f * Mathf.Pow(2.0f, Mathf.Clamp(CameraEV100, -10.0f, 24.0f));
            return LightPhotometry.HDRScale / camera * Mathf.Pow(2.0f, Mathf.Clamp(ExposureEV, -16.0f, 16.0f));
        }
    }

    public sealed class MaxColorOutputPass : IDisposable
    {
#if UNITY_EDITOR
        // One-shot Editor diagnostics can copy the real inputs/output in command order.
        // Subscribers must preserve render state; no hook exists in a Player build.
        public static Action<CommandBuffer, Camera, RenderTexture, RenderTexture, Vector4> EditorOutputCapture;
#endif
        private Material m_material;
        private static readonly int ColorID = Shader.PropertyToID("_SceneColorHDR");
        private static readonly int DepthID = Shader.PropertyToID("_SceneDepthHDR");
        private static readonly int ParametersID = Shader.PropertyToID("_ColorOutputParameters");

        public MaxColorOutputPass(ColorOutputSettings settings)
        {
            Shader shader = settings.Shader != null ? settings.Shader : Shader.Find("Hidden/MaxSRP/ColorOutput");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires a supported ColorOutput shader.");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Execute(ScriptableRenderContext context, Camera camera, RenderTexture color,
            RenderTexture sceneDepth, RenderTargetIdentifier target, RenderTargetIdentifier targetDepth,
            ColorOutputSettings settings)
        {
            CommandBuffer cmd = CommandBufferPool.Get("HDR Color Output");
            try
            {
                cmd.SetGlobalTexture(ColorID, color);
                cmd.SetGlobalTexture(DepthID, sceneDepth);
                float exposure = MaxSceneExposure.GetExposureMultiplier(camera, settings);
                // Our scene is always rendered into a texture. The backbuffer uses a different
                // projection convention on top-origin APIs; RT destinations keep the source convention.
                float flipToBackbuffer = !IsTextureDestination(camera) && SystemInfo.graphicsUVStartsAtTop ? 1.0f : 0.0f;
                float ditherMode = 0.0f;
                if (settings.Dithering)
                {
                    RenderTexture destination = camera.targetTexture;
                    // Scene view may use a floating-point intermediate, but is ultimately
                    // displayed as LDR. Explicit high-precision camera exports stay unmodified.
                    if (destination == null || camera.cameraType == CameraType.SceneView)
                        ditherMode = QualitySettings.activeColorSpace == ColorSpace.Linear ? 2.0f : 1.0f;
                    else if (destination.sRGB)
                        ditherMode = 2.0f;
                    else if (destination.format == RenderTextureFormat.ARGB32 ||
                        destination.format == RenderTextureFormat.BGRA32)
                        ditherMode = 1.0f;
                }
                var parameters = new Vector4(exposure, (float)settings.ToneMapping, flipToBackbuffer, ditherMode);
                cmd.SetGlobalVector(ParametersID, parameters);
                cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    targetDepth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(new Rect(0, 0, color.width, color.height));
                cmd.DrawProcedural(Matrix4x4.identity, m_material, 0, MeshTopology.Triangles, 3);
#if UNITY_EDITOR
                EditorOutputCapture?.Invoke(cmd, camera, color, sceneDepth, parameters);
#endif
                cmd.SetViewport(CameraViewport.GetViewport(camera));
                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        internal static bool IsTextureDestination(Camera camera)
        {
            return camera.targetTexture != null || camera.cameraType == CameraType.SceneView ||
                camera.cameraType == CameraType.Preview;
        }

        public void Dispose()
        {
            MaxRenderResources.Destroy(ref m_material);
        }
    }
}
