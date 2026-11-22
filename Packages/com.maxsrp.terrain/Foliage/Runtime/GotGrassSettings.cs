using System;
using UnityEngine;

namespace MaxTerrain
{
    [Serializable]
    public sealed class GotGrassTypeMaterial
    {
        public int TypeId;
        public Texture2D ColorRamp, ColorDetail, ScalarRamp;
        public bool ColorDetailTopDown;
    }

    [CreateAssetMenu(menuName = "MaxTerrain/GotGrass Settings", fileName = "GotGrass")]
    public sealed class GotGrassSettings : ScriptableObject
    {
        public ComputeShader GenerationShader;
        public Shader DrawShader;
        public TextAsset TypeCatalog;
        public GotGrassTypeMaterial[] TypeMaterials;

        [Header("Terrain Placement")]
        public int GrassMaterialA = 19, GrassMaterialB = 39;
        public int GrassTypeA = 2, GrassTypeB = 14;
        [Range(0, 1)] public float DensityA = 1, DensityB = 1;
        [Range(0, 1)] public float GrassHeightA = .9f, GrassHeightB = .05f;
        public float MinimumHeight = 256;
        [Range(0, 90)] public float MaxSlope = 40;
        public int Seed = 1;

        [Header("View Budget")]
        [Min(1)] public float MaxDistance = 64;
        [Min(0)] public float ThinningStart = 32, LowTopologyDistance = 28;
        [Min(1)] public int VisibleCapacity = 262144;
        public bool HighTopology = true, DistanceThinning = true;

        [Header("Wind and Contact")]
        public Vector2 WindDirection = new Vector2(1, .35f);
        [Min(0)] public float WindStrength = 2, WindSpeed = 1.4f;
        public Shader DisplacementShader;
        public GotGrassInteractionSettings Interaction;
    }
}
