using UnityEngine;

namespace MaxSRP
{
    // Deserializes exposure overrides in archived scenes authored before Baked Solar TOD owned them.
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class MaxSceneExposure : MonoBehaviour
    {
        [Range(-10.0f, 24.0f)] public float CameraEV100 = 10.5f;
        [Range(-16.0f, 16.0f)] public float ExposureEV = 0.0f;

        private void OnEnable() => MaxSceneComponents<MaxSceneExposure>.Register(this);
        private void OnDisable() => MaxSceneComponents<MaxSceneExposure>.Unregister(this);

        public static float GetExposureMultiplier(Camera camera, ColorOutputSettings fallback) =>
            MaxColorOutputPass.GetLegacyExposureMultiplier(camera, MaxTimeOfDayState.Current, fallback);

        public static bool TryGetExposureMultiplier(Camera camera, out float exposure) =>
            MaxColorOutputPass.TryGetSceneExposure(camera, MaxTimeOfDayState.Current, out exposure);
    }
}
