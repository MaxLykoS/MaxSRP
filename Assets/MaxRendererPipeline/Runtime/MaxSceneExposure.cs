using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    // Exposure belongs to this scene; project and quality pipeline settings stay unchanged.
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class MaxSceneExposure : MonoBehaviour
    {
        [Range(-10.0f, 24.0f)] public float CameraEV100 = 10.5f;
        [Range(-16.0f, 16.0f)] public float ExposureEV = 0.0f;

        private static readonly List<MaxSceneExposure> activeEnvironments = new List<MaxSceneExposure>();

        private void OnEnable()
        {
            if (!activeEnvironments.Contains(this)) activeEnvironments.Add(this);
        }

        private void OnDisable()
        {
            activeEnvironments.Remove(this);
        }

        public static float GetExposureMultiplier(Camera camera, ColorOutputSettings fallback)
        {
            // Numerical previews and reflection captures retain their pipeline exposure.
            if (!camera || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
                return fallback.GetExposureMultiplier();

            Scene scene = camera.cameraType == CameraType.SceneView
                ? SceneManager.GetActiveScene()
                : camera.gameObject.scene;
            MaxSceneExposure selected = null;
            for (int i = activeEnvironments.Count - 1; i >= 0; --i)
            {
                MaxSceneExposure environment = activeEnvironments[i];
                if (!environment)
                {
                    activeEnvironments.RemoveAt(i);
                    continue;
                }
                if (!environment.isActiveAndEnabled || environment.gameObject.scene != scene) continue;
                // Duplicates in one scene have a stable, registration-order-independent choice.
                if (!selected || environment.GetInstanceID() < selected.GetInstanceID()) selected = environment;
            }
            if (!selected) return fallback.GetExposureMultiplier();

            // Match ColorOutputSettings: scene HDR is cd/m² / HDRScale, and exposure
            // is applied once after the shared TAA resolve, without changing history.
            float cameraExposure = 1.2f * Mathf.Pow(2.0f, Mathf.Clamp(selected.CameraEV100, -10.0f, 24.0f));
            return LightPhotometry.HDRScale / cameraExposure *
                Mathf.Pow(2.0f, Mathf.Clamp(selected.ExposureEV, -16.0f, 16.0f));
        }
    }
}
