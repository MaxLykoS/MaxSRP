using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Exposure statistics and bloom read clean, resolved scene HDR before editor overlays.
    // Neither stage writes scene color or the shared TAA history.
    public sealed class MaxPostProcessPass : MaxRenderPass
    {
        public struct Output
        {
            public PostProcessProfile Profile;
            public Shader OutputShader;
            public GraphicsBuffer Exposure;
            public MaxBloomPass.Output Bloom;
            public float ManualExposure;
            public Vector4 BloomUVScaleBias;
            public bool Active;
        }

        private MaxAutoExposurePass m_exposure;
        private MaxBloomPass m_bloom;
        private ComputeShader m_exposureShader;
        private Shader m_bloomShader;

        internal GraphicsBuffer GetTemporalExposure(Camera camera, RenderTexture sceneHDR, Rect viewport,
            MaxPostProcessSettings configuration, out float multiplier)
        {
            ExposureSettings settings = configuration.Profile.Exposure;
            // Scene RGB is already divided by HDRScale. Both existing exposure
            // paths return the multiplier in those same fixed HDR units.
            multiplier = settings.GetManualMultiplier();
            return settings.Mode != ExposureMode.Manual && m_exposureShader == configuration.ExposureCompute
                ? m_exposure?.GetPreviousExposure(camera, sceneHDR, viewport, settings) : null;
        }

        public Output Prepare(ScriptableRenderContext context, Camera camera, RenderTexture sceneHDR,
            Rect viewport, MaxPostProcessConfig configuration, ColorOutputSettings legacy, bool bypass, bool exposureOnly = false) =>
            Prepare(context, camera, sceneHDR, viewport, new MaxPostProcessSettings(configuration, MaxRenderQuality.Platform),
                legacy, bypass, exposureOnly);

        internal Output Prepare(ScriptableRenderContext context, Camera camera, RenderTexture sceneHDR,
            Rect viewport, MaxPostProcessSettings configuration, ColorOutputSettings legacy, bool bypass, bool exposureOnly = false)
        {
            bool eligible = !bypass && configuration.Active && !camera.stereoEnabled &&
                (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
            if (!eligible)
            {
                m_exposure?.ReleaseCamera(camera);
                return new Output { ManualExposure = legacy.GetExposureMultiplier() };
            }

            EnsureShaders(configuration);
            PostProcessProfile profile = configuration.Profile;
            float manualExposure = profile.Exposure.GetManualMultiplier();
            GraphicsBuffer exposure = null;
            if (profile.Exposure.Mode != ExposureMode.Manual)
                exposure = m_exposure?.Execute(context, camera, sceneHDR, viewport, profile.Exposure);
            else
                m_exposure?.ReleaseCamera(camera);

            if (exposureOnly) return default;

            MaxBloomPass.Output bloom = m_bloom?.Execute(context, sceneHDR, viewport, exposure,
                manualExposure, profile.Bloom, configuration.Platform) ?? default;
            return new Output
            {
                Active = true, Profile = profile, OutputShader = configuration.OutputShader,
                Exposure = exposure, Bloom = bloom,
                ManualExposure = manualExposure,
                BloomUVScaleBias = new Vector4(sceneHDR.width / viewport.width, sceneHDR.height / viewport.height,
                    -viewport.x / viewport.width, -viewport.y / viewport.height)
            };
        }

        private void EnsureShaders(MaxPostProcessSettings configuration)
        {
            if (configuration.ExposureCompute != m_exposureShader)
            {
                m_exposure?.Dispose();
                m_exposureShader = configuration.ExposureCompute;
                m_exposure = m_exposureShader ? new MaxAutoExposurePass(m_exposureShader) : null;
            }
            if (configuration.BloomShader != m_bloomShader)
            {
                m_bloom?.Dispose();
                m_bloomShader = configuration.BloomShader;
                m_bloom = m_bloomShader ? new MaxBloomPass(m_bloomShader) : null;
            }
        }

        public void ResetHistory(Camera camera) => m_exposure?.ResetHistory(camera);
        public override void PruneDestroyedCameras() => m_exposure?.PruneDestroyedCameras();
        protected override void ReleaseResources()
        {
            m_exposure?.Dispose(); m_exposure = null;
            m_bloom?.Dispose(); m_bloom = null;
            m_exposureShader = null; m_bloomShader = null;
        }
    }
}
