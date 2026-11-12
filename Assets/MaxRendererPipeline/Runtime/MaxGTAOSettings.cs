using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("MaxSRP/GTAO")]
    public sealed class MaxGTAOSettings : MonoBehaviour
    {
        public ComputeShader Compute;
        [Range(0.0f, 1.0f)] public float Strength = 1.0f;
        [Range(0.05f, 3.0f)] public float Radius = 0.6f;
        [Range(0, 2)] public int Quality = 1;
        [Tooltip("Show linear GTAO visibility as grayscale.")]
        public bool AOOnly;

        private static readonly List<MaxGTAOSettings> Active = new List<MaxGTAOSettings>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        internal static MaxGTAOSettings FindForCamera(Camera camera)
        {
            if (camera.stereoEnabled ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
                return null;
            Scene scene = camera.cameraType == CameraType.SceneView
                ? SceneManager.GetActiveScene() : camera.gameObject.scene;
            MaxGTAOSettings chosen = null;
            for (int i = Active.Count - 1; i >= 0; --i)
            {
                var entry = Active[i];
                if (!entry) { Active.RemoveAt(i); continue; }
                if (!entry.isActiveAndEnabled || entry.gameObject.scene != scene) continue;
                if (!chosen || entry.GetInstanceID() < chosen.GetInstanceID()) chosen = entry;
            }
            return chosen;
        }

        internal int HistoryKey
        {
            get
            {
                unchecked
                {
                    int key = GetInstanceID();
                    key = key * 31 + (Compute ? Compute.GetInstanceID() : 0);
                    key = key * 31 + Strength.GetHashCode();
                    key = key * 31 + Radius.GetHashCode();
                    key = key * 31 + Quality;
                    return key * 31 + (AOOnly ? 1 : 0);
                }
            }
        }
    }
}
