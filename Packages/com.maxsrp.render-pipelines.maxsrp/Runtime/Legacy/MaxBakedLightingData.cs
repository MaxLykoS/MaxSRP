using System;
using UnityEngine;

namespace MaxSRP
{
    // Retain the original script GUID and field names for archived bake scenes.
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class MaxBakedLightingData : MonoBehaviour
    {
        public Texture2DArray SkyVisibility;
        public MaxBakedLighting.RendererMaterials[] OriginalMaterials = Array.Empty<MaxBakedLighting.RendererMaterials>();
        public Material[] GeneratedMaterials = Array.Empty<Material>();
        [TextArea] public string BakeDescription;

        private static readonly Predicate<MaxBakedLightingData> HasSkyVisibility = data => data.SkyVisibility;

        private void OnEnable()
        {
            MaxSceneComponents<MaxBakedLightingData>.Register(this);
            MaxBakedLighting.RepairMissingMaterials(OriginalMaterials);
        }

        private void OnDisable() => MaxSceneComponents<MaxBakedLightingData>.Unregister(this);

        internal static MaxBakedLightingData FindForCamera(Camera camera) =>
            MaxSceneComponents<MaxBakedLightingData>.Find(camera, HasSkyVisibility);
    }
}
