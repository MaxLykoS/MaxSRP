using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Exposure statistics and bloom read clean, resolved scene HDR before editor overlays.
    // Neither stage writes scene color or the shared TAA history.
    public sealed class MaxPostProcessPass : IDisposable
    {
        public struct Output
        {
            public PostProcessProfile Profile;
            public Texture Exposure, Bloom;
            public float ManualExposure;
            public Vector4 BloomUVScaleBias;
            public bool Active;
        }

        private MaxAutoExposurePass m_exposure;
        private MaxBloomPass m_bloom;
        private ComputeShader m_exposureShader;
        private Shader m_bloomShader;

        public Output Prepare(ScriptableRenderContext context, Camera camera, RenderTexture sceneHDR,
            Rect viewport, MaxPostProcessConfig configuration, ColorOutputSettings legacy, bool bypass)
        {
            bool eligible = !bypass && configuration && !camera.stereoEnabled &&
                (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
            if (!eligible)
            {
                m_exposure?.ReleaseCamera(camera);
                m_bloom?.ReleaseCamera(camera);
                return new Output { ManualExposure = legacy.GetExposureMultiplier() };
            }

            EnsureShaders(configuration);
            PostProcessProfile profile = configuration.Profile;
            float manualExposure = profile.Exposure.GetManualMultiplier();
            Texture exposure = null;
            if (profile.Exposure.Mode != ExposureMode.Manual)
                exposure = m_exposure?.Execute(context, camera, sceneHDR, viewport, profile.Exposure);
            else
                m_exposure?.ReleaseCamera(camera);

            Texture bloom = m_bloom?.Execute(context, camera, sceneHDR, viewport, exposure,
                manualExposure, profile.Bloom, configuration.Platform);
            return new Output
            {
                Active = true, Profile = profile, Exposure = exposure, Bloom = bloom,
                ManualExposure = manualExposure,
                BloomUVScaleBias = new Vector4(sceneHDR.width / viewport.width, sceneHDR.height / viewport.height,
                    -viewport.x / viewport.width, -viewport.y / viewport.height)
            };
        }

        private void EnsureShaders(MaxPostProcessConfig configuration)
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
        public void PruneDestroyedCameras()
        {
            m_exposure?.PruneDestroyedCameras();
            m_bloom?.PruneDestroyedCameras();
        }
        public void Dispose()
        {
            m_exposure?.Dispose(); m_exposure = null;
            m_bloom?.Dispose(); m_bloom = null;
            m_exposureShader = null; m_bloomShader = null;
        }
    }
}
