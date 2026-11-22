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

        public float GetExposureMultiplier() => GetExposureMultiplier(CameraEV100, ExposureEV);

        internal static float GetExposureMultiplier(float ev100, float compensation)
        {
            // Saturation-based camera calibration: Lmax = 1.2 * 2^EV100.
            // Scene HDR stores cd/m² / HDRScale; compensate for that scale once.
            float camera = 1.2f * Mathf.Pow(2.0f, Mathf.Clamp(ev100, -10.0f, 24.0f));
            return LightPhotometry.HDRScale / camera * Mathf.Pow(2.0f, Mathf.Clamp(compensation, -16.0f, 16.0f));
        }
    }

    public sealed class MaxColorOutputPass : MaxRenderPass
    {
#if UNITY_EDITOR
        // One-shot Editor diagnostics can copy the real inputs/output in command order.
        // Subscribers must preserve render state; no hook exists in a Player build.
        public static Action<CommandBuffer, Camera, RenderTexture, RenderTexture, Vector4> EditorOutputCapture;
#endif
        private Material m_material;
        private readonly Shader m_defaultShader;
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
            m_defaultShader = shader;
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        internal static float GetLegacyExposureMultiplier(Camera camera, MaxTimeOfDayState environment,
            ColorOutputSettings fallback) =>
            TryGetSceneExposure(camera, environment, out float exposure) ? exposure : fallback.GetExposureMultiplier();

        internal static bool TryGetSceneExposure(Camera camera, MaxTimeOfDayState environment, out float exposure)
        {
            if (!camera || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
            {
                exposure = 1f;
                return false;
            }

            if (environment != null && environment.UseSceneExposure)
            {
                exposure = ColorOutputSettings.GetExposureMultiplier(environment.CameraEV100, environment.ExposureEV);
                return true;
            }

            var legacy = MaxSceneComponents<MaxSceneExposure>.Find(camera);
            exposure = legacy ? ColorOutputSettings.GetExposureMultiplier(legacy.CameraEV100, legacy.ExposureEV) : 1f;
            return legacy;
        }

        public void Execute(ScriptableRenderContext context, Camera camera, RenderTexture color,
            RenderTexture sceneDepth, RenderTargetIdentifier target, RenderTargetIdentifier targetDepth,
            ColorOutputSettings settings, bool showLinearMask = false, MaxPostProcessPass.Output post = default) =>
            Execute(context, camera, color, sceneDepth, target, targetDepth, settings, showLinearMask, post,
                MaxTimeOfDayState.Current, CameraViewport.GetViewport(camera));

        internal void Execute(ScriptableRenderContext context, Camera camera, RenderTexture color,
            RenderTexture sceneDepth, RenderTargetIdentifier target, RenderTargetIdentifier targetDepth,
            ColorOutputSettings settings, bool showLinearMask, MaxPostProcessPass.Output post,
            MaxTimeOfDayState environment, Rect sourceViewport)
        {
            Shader outputShader = post.Active && !showLinearMask && post.OutputShader
                ? post.OutputShader : m_defaultShader;
            if (m_material.shader != outputShader)
            {
                if (!outputShader.isSupported)
                    throw new InvalidOperationException("MaxSRP requires a supported ColorOutput shader.");
                MaxRenderResources.Destroy(ref m_material);
                m_material = new Material(outputShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            using (var commands = new PooledCommandBuffer("HDR Color Output"))
            {
                CommandBuffer cmd = commands.Buffer;
                cmd.SetGlobalTexture(ColorID, color);
                cmd.SetGlobalTexture(DepthID, sceneDepth);
                bool usePost = post.Active && !showLinearMask;
                PostProcessProfile profile = usePost ? post.Profile : null;
                // Both exposure sources scale the resolved HDR only at final output.
                float exposure = showLinearMask ? 1.0f : usePost ? post.ManualExposure
                    : GetLegacyExposureMultiplier(camera, environment, settings);
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
                bool useBloom = usePost && profile.Bloom.Enabled && profile.Bloom.Intensity > 0 && post.Bloom.IsValid;
                cmd.SetGlobalVector("_MaxPostProcessFlags", new Vector4(
                    0f,
                    useBloom ? 1f : 0f,
                    usePost && grading.Enabled ? 1f : 0f, 0f));
                cmd.SetGlobalTexture("_MaxBloomTexture", useBloom ? post.Bloom.Target : new RenderTargetIdentifier(Texture2D.blackTexture));
                cmd.SetGlobalVector("_MaxBloomUVScaleBias", usePost ? post.BloomUVScaleBias : new Vector4(1, 1, 0, 0));
                // 从相机的 HDR 缓冲取样，写入最终目标上的相机视口。
                Rect destinationViewport = CameraViewport.GetViewport(camera);
                cmd.SetGlobalVector("_MaxSourceUVScaleBias", new Vector4(sourceViewport.width / color.width,
                    sourceViewport.height / color.height, sourceViewport.x / color.width, sourceViewport.y / color.height));
                cmd.SetGlobalVector("_MaxColorSaturation", grading.Saturation);
                cmd.SetGlobalVector("_MaxColorContrast", grading.Contrast);
                cmd.SetGlobalVector("_MaxColorGamma", grading.Gamma);
                cmd.SetGlobalVector("_MaxColorGain", grading.Gain);
                cmd.SetGlobalVector("_MaxColorOffset", grading.Offset);
                cmd.SetGlobalVector("_MaxWhiteBalance", new Vector4(Mathf.Clamp(grading.Temperature, 1500f, 15000f), Mathf.Clamp(grading.Tint, -1f, 1f), 0, 0));
                cmd.SetGlobalVector("_MaxFilmParameters", new Vector4(Mathf.Clamp(film.Slope, 0.01f, 1f),
                    Mathf.Clamp01(film.Toe), Mathf.Clamp01(film.Shoulder), Mathf.Clamp(film.BlackClip, 0f, 0.5f)));
                cmd.SetGlobalFloat("_MaxFilmWhiteClip", Mathf.Clamp(film.WhiteClip, 0f, 0.5f));
                // 保留输出中属于其他相机的视口。
                cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    targetDepth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetViewport(destinationViewport);
                GraphicsBuffer exposureBuffer = usePost ? post.Exposure : null;
                MaxAutoExposurePass.BindForDraw(cmd, exposureBuffer);
                cmd.DrawProcedural(Matrix4x4.identity, m_material, 0, MeshTopology.Triangles, 3);
                if (exposureBuffer != null) MaxAutoExposurePass.BindForDraw(cmd, null);
#if UNITY_EDITOR
                EditorOutputCapture?.Invoke(cmd, camera, color, sceneDepth, parameters);
#endif
                if (useBloom) cmd.SetGlobalTexture("_MaxBloomTexture", Texture2D.blackTexture);
                post.Bloom.Release(cmd);
                cmd.SetViewport(destinationViewport);
                context.ExecuteCommandBuffer(cmd);
            }
        }

        internal static bool IsTextureDestination(Camera camera)
        {
            return camera.targetTexture != null || camera.cameraType == CameraType.SceneView ||
                camera.cameraType == CameraType.Preview;
        }

        protected override void ReleaseResources()
        {
            MaxRenderResources.Destroy(ref m_material);
        }
    }
}
