using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public enum ToneMappingMode { None = 0, ACES = 1, UEFilm = 2 }

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
        private static readonly ColorGradingSettings NeutralGrading = new ColorGradingSettings();
        private static readonly FilmSettings DefaultFilm = new FilmSettings();

        public MaxColorOutputPass(ColorOutputSettings settings, Shader outputShader = null)
        {
            Shader shader = outputShader != null ? outputShader : settings.Shader != null ? settings.Shader : Shader.Find("Hidden/MaxSRP/ColorOutput");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires a supported ColorOutput shader.");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Execute(ScriptableRenderContext context, Camera camera, RenderTexture color,
            RenderTexture sceneDepth, RenderTargetIdentifier target, RenderTargetIdentifier targetDepth,
            ColorOutputSettings settings, bool showLinearMask = false, MaxPostProcessPass.Output post = default)
        {
            CommandBuffer cmd = CommandBufferPool.Get("HDR Color Output");
            try
            {
                cmd.SetGlobalTexture(ColorID, color);
                cmd.SetGlobalTexture(DepthID, sceneDepth);
                bool usePost = post.Active && !showLinearMask;
                PostProcessProfile profile = usePost ? post.Profile : null;
                float exposure = showLinearMask ? 1.0f : usePost ? post.ManualExposure : MaxSceneExposure.GetExposureMultiplier(camera, settings);
                // Our scene is always rendered into a texture. The backbuffer uses a different
                // projection convention on top-origin APIs; RT destinations keep the source convention.
                float flipToBackbuffer = !IsTextureDestination(camera) && SystemInfo.graphicsUVStartsAtTop ? 1.0f : 0.0f;
                float ditherMode = 0.0f;
                if ((usePost ? profile.Dithering : settings.Dithering) && !showLinearMask)
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
                var parameters = new Vector4(exposure,
                    showLinearMask ? (float)ToneMappingMode.None : (float)(usePost ? profile.Film.Mode : settings.ToneMapping),
                    flipToBackbuffer, ditherMode);
                cmd.SetGlobalVector(ParametersID, parameters);
                ColorGradingSettings grading = usePost ? profile.ColorGrading : NeutralGrading;
                FilmSettings film = usePost ? profile.Film : DefaultFilm;
                cmd.SetGlobalVector("_MaxPostProcessFlags", new Vector4(
                    usePost && post.Exposure != null ? 1f : 0f,
                    usePost && profile.Bloom.Enabled && profile.Bloom.Intensity > 0 && post.Bloom != null ? 1f : 0f,
                    usePost && grading.Enabled ? 1f : 0f, 0f));
                cmd.SetGlobalTexture("_MaxExposureTexture", post.Exposure != null ? post.Exposure : Texture2D.whiteTexture);
                cmd.SetGlobalTexture("_MaxBloomTexture", post.Bloom != null ? post.Bloom : Texture2D.blackTexture);
                cmd.SetGlobalVector("_MaxBloomUVScaleBias", usePost ? post.BloomUVScaleBias : new Vector4(1, 1, 0, 0));
                Rect viewport = usePost ? CameraViewport.GetViewport(camera) : new Rect(0, 0, color.width, color.height);
                cmd.SetGlobalVector("_MaxSourceUVScaleBias", new Vector4(viewport.width / color.width,
                    viewport.height / color.height, viewport.x / color.width, viewport.y / color.height));
                cmd.SetGlobalVector("_MaxColorSaturation", grading.Saturation);
                cmd.SetGlobalVector("_MaxColorContrast", grading.Contrast);
                cmd.SetGlobalVector("_MaxColorGamma", grading.Gamma);
                cmd.SetGlobalVector("_MaxColorGain", grading.Gain);
                cmd.SetGlobalVector("_MaxColorOffset", grading.Offset);
                cmd.SetGlobalVector("_MaxWhiteBalance", new Vector4(Mathf.Clamp(grading.Temperature, 1500f, 15000f), Mathf.Clamp(grading.Tint, -1f, 1f), 0, 0));
                cmd.SetGlobalVector("_MaxFilmParameters", new Vector4(Mathf.Clamp(film.Slope, 0.01f, 1f),
                    Mathf.Clamp01(film.Toe), Mathf.Clamp01(film.Shoulder), Mathf.Clamp(film.BlackClip, 0f, 0.5f)));
                cmd.SetGlobalFloat("_MaxFilmWhiteClip", Mathf.Clamp(film.WhiteClip, 0f, 0.5f));
                cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    targetDepth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(viewport);
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
