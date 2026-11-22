using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    // A sky visibility slice shares the UV layout of its Unity lightmap.
    public sealed class MaxBakedLighting
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

        private static readonly int ArrayId = Shader.PropertyToID("_MaxSkyVisibilityArray");
        private static readonly int ReadyId = Shader.PropertyToID("_MaxSkyVisibilityReady");

        public static Texture2DArray GetSkyVisibility(Camera camera) =>
            GetSkyVisibility(camera, MaxTimeOfDayState.Current);

        internal static Texture2DArray GetSkyVisibility(Camera camera, MaxTimeOfDayState environment)
        {
            if (!camera) return null;
            var visibility = environment?.SkyVisibility;
            return visibility ? visibility : MaxBakedLightingData.FindForCamera(camera)?.SkyVisibility;
        }

        internal static void Bind(CommandBuffer commands, Texture2DArray visibility)
        {
            // A camera without a bake must clear the ready flag left by the previous camera.
            commands.SetGlobalFloat(ReadyId, visibility ? 1 : 0);
            if (visibility) commands.SetGlobalTexture(ArrayId, visibility);
        }

        internal static void RepairMissingMaterials(RendererMaterials[] originals)
        {
            if (originals == null) return;
            // Local bake variants may be absent on a fresh checkout.
            foreach (RendererMaterials entry in originals)
            {
                if (entry == null || !entry.Renderer || entry.SourceMaterials == null) continue;
                Material[] materials = entry.Renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length && i < entry.SourceMaterials.Length; ++i)
                {
                    if (materials[i] || !entry.SourceMaterials[i]) continue;
                    materials[i] = entry.SourceMaterials[i];
                    changed = true;
                }
                if (changed) entry.Renderer.sharedMaterials = materials;
            }
        }

#if UNITY_EDITOR
        public static IEnumerable<(MonoBehaviour Owner, MaxBakedLighting Data)> ReadSceneBakes(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (var environment in root.GetComponentsInChildren<MaxBakedSolarGI>(true))
                {
                    if (!environment.SkyVisibility && (environment.OriginalMaterials?.Length ?? 0) == 0) continue;
                    yield return (environment, new MaxBakedLighting
                    {
                        SkyVisibility = environment.SkyVisibility,
                        OriginalMaterials = environment.OriginalMaterials,
                        GeneratedMaterials = environment.GeneratedMaterials,
                        BakeDescription = environment.BakeDescription
                    });
                }
                foreach (var legacy in root.GetComponentsInChildren<MaxBakedLightingData>(true))
                {
                    if (!legacy.SkyVisibility && (legacy.OriginalMaterials?.Length ?? 0) == 0) continue;
                    yield return (legacy, new MaxBakedLighting
                    {
                        SkyVisibility = legacy.SkyVisibility,
                        OriginalMaterials = legacy.OriginalMaterials,
                        GeneratedMaterials = legacy.GeneratedMaterials,
                        BakeDescription = legacy.BakeDescription
                    });
                }
            }
        }

        // Restore authoring materials before lightmap indices change or variants are cloned again.
        public static void RestoreSourceMaterials(Scene scene)
        {
            foreach (var (owner, data) in ReadSceneBakes(scene))
            {
                foreach (RendererMaterials entry in data.OriginalMaterials ?? Array.Empty<RendererMaterials>())
                {
                    if (entry == null || !entry.Renderer || entry.SourceMaterials == null) continue;
                    entry.Renderer.sharedMaterials = entry.SourceMaterials;
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(entry.Renderer);
                    UnityEditor.EditorUtility.SetDirty(entry.Renderer);
                }
                if (owner is MaxBakedLightingData legacy)
                {
                    legacy.SkyVisibility = null;
                    legacy.OriginalMaterials = Array.Empty<RendererMaterials>();
                }
                else if (owner is MaxBakedSolarGI environment)
                {
                    environment.SkyVisibility = null;
                    environment.OriginalMaterials = Array.Empty<RendererMaterials>();
                    environment.ApplyToPipeline();
                }
                UnityEditor.EditorUtility.SetDirty(owner);
            }
        }
#endif
    }
}
