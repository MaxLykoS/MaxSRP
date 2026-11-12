using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    // Scene-owned baked data. Each array slice has the same UV layout as one Unity lightmap.
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class MaxBakedLightingData : MonoBehaviour
    {
        [Serializable]
        public sealed class RendererMaterials
        {
            public Renderer Renderer;
            public Material[] SourceMaterials;
        }

        public Texture2DArray SkyVisibility;
        public RendererMaterials[] OriginalMaterials = Array.Empty<RendererMaterials>();
        public Material[] GeneratedMaterials = Array.Empty<Material>();
        [TextArea] public string BakeDescription;

        private static readonly List<MaxBakedLightingData> Active = new List<MaxBakedLightingData>();
        private static readonly int ArrayId = Shader.PropertyToID("_MaxSkyVisibilityArray");
        private static readonly int ReadyId = Shader.PropertyToID("_MaxSkyVisibilityReady");

        private void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
            // A fresh checkout has source materials but no local bake clones.
            // Repair only missing slots, preserving any locally generated bindings.
            foreach (RendererMaterials entry in OriginalMaterials)
            {
                if (entry == null || !entry.Renderer || entry.SourceMaterials == null) continue;
                Material[] materials = entry.Renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length && i < entry.SourceMaterials.Length; ++i)
                    if (!materials[i] && entry.SourceMaterials[i])
                    {
                        materials[i] = entry.SourceMaterials[i];
                        changed = true;
                    }
                if (changed) entry.Renderer.sharedMaterials = materials;
            }
        }
        private void OnDisable() { Active.Remove(this); }

        public static void BindForCamera(CommandBuffer cmd, Camera camera)
        {
            MaxBakedLightingData chosen = null;
            if (camera && (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView))
            {
                Scene scene = camera.cameraType == CameraType.SceneView
                    ? SceneManager.GetActiveScene() : camera.gameObject.scene;
                for (int i = Active.Count - 1; i >= 0; --i)
                {
                    var entry = Active[i];
                    if (!entry) { Active.RemoveAt(i); continue; }
                    if (!entry.isActiveAndEnabled || !entry.SkyVisibility || entry.gameObject.scene != scene) continue;
                    if (!chosen || entry.GetInstanceID() < chosen.GetInstanceID()) chosen = entry;
                }
            }
            cmd.SetGlobalFloat(ReadyId, chosen ? 1 : 0);
            if (chosen) cmd.SetGlobalTexture(ArrayId, chosen.SkyVisibility);
        }

        // Call before re-baking/reassigning lightmap indices so a generated material is never cloned again.
        public static void RestoreSourceMaterials(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MaxBakedLightingData data in root.GetComponentsInChildren<MaxBakedLightingData>(true))
            {
                foreach (RendererMaterials entry in data.OriginalMaterials)
                {
                    if (!entry.Renderer || entry.SourceMaterials == null) continue;
                    entry.Renderer.sharedMaterials = entry.SourceMaterials;
#if UNITY_EDITOR
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(entry.Renderer);
                    UnityEditor.EditorUtility.SetDirty(entry.Renderer);
#endif
                }
                data.SkyVisibility = null;
                data.OriginalMaterials = Array.Empty<RendererMaterials>();
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(data);
#endif
            }
        }
    }
}
