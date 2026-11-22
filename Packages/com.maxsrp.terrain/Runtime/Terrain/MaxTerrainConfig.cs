using MaxSRP;
using System;
using UnityEngine;

namespace MaxTerrain
{
    public enum MaxTerrainGeometryPreset { Direct = 0, Budget = 1 }

    [Serializable]
    public sealed class MaxTerrainProfile
    {
        [SerializeField, Range(MaxTerrainConfig.MinimumPagesPerFrame, MaxTerrainConfig.MaximumPagesPerFrame)]
        [Tooltip("Maximum VT pages published per frame, shared by runtime composition and baked-page uploads.")]
        private int m_pagesPerFrame = MaxTerrainConfig.MaximumPagesPerFrame;

        [SerializeField, Min(MaxTerrainConfig.MinimumPhysicalCacheMB)]
        [Tooltip("VT physical texture budget in decimal MB (1 MB = 1,000,000 bytes). Shared by RVT and SVT; excludes page tables and staging textures.")]
        private int m_physicalCacheMB = MaxTerrainConfig.DefaultPcPhysicalCacheMB;

        [SerializeField, Tooltip("Direct keeps the reference terrain's detailed LODs farther away. Budget uses the existing compact MaxTerrain LOD ranges.")]
        private MaxTerrainGeometryPreset m_geometryPreset = MaxTerrainGeometryPreset.Direct;
        [SerializeField, Range(MaxTerrainConfig.MinimumLodDistanceScale, MaxTerrainConfig.MaximumLodDistanceScale)]
        [Tooltip("Geometry LOD distance multiplier. Higher values keep dense geometry farther away. The morph safety minimum still applies; source height spacing stays 1 metre.")]
        private float m_lodDistanceScale = 1f;
        [SerializeField, Range(0, MaxTerrainConfig.MaximumGeometryLod)]
        [Tooltip("Finest geometry LOD allowed: 0/1/2/3/4 gives nearest vertex spacing of 1/2/4/8/16 metres. Height source samples are unchanged.")]
        private int m_minimumGeometryLod;

        public int PagesPerFrame => Mathf.Clamp(m_pagesPerFrame,
            MaxTerrainConfig.MinimumPagesPerFrame, MaxTerrainConfig.MaximumPagesPerFrame);
        public int PhysicalCacheMB => Mathf.Clamp(m_physicalCacheMB,
            MaxTerrainConfig.MinimumPhysicalCacheMB, MaxTerrainConfig.MaximumPhysicalCacheMB);
        public MaxTerrainGeometryPreset GeometryPreset => m_geometryPreset == MaxTerrainGeometryPreset.Budget
            ? MaxTerrainGeometryPreset.Budget : MaxTerrainGeometryPreset.Direct;
        public float LodDistanceScale => MaxTerrainConfig.ClampLodDistanceScale(m_lodDistanceScale);
        public int MinimumGeometryLod => Mathf.Clamp(m_minimumGeometryLod, 0, MaxTerrainConfig.MaximumGeometryLod);

        public MaxTerrainProfile() { }
        public MaxTerrainProfile(int pagesPerFrame, int physicalCacheMB,
            MaxTerrainGeometryPreset geometryPreset = MaxTerrainGeometryPreset.Direct)
        { m_pagesPerFrame = pagesPerFrame; m_physicalCacheMB = physicalCacheMB; m_geometryPreset = geometryPreset; }

        internal void Validate()
        {
            m_pagesPerFrame = PagesPerFrame; m_physicalCacheMB = PhysicalCacheMB;
            m_geometryPreset = GeometryPreset; m_lodDistanceScale = LodDistanceScale;
            m_minimumGeometryLod = MinimumGeometryLod;
        }
    }

    [CreateAssetMenu(menuName = "MaxTerrain/Terrain Configuration")]
    public sealed class MaxTerrainConfig : ScriptableObject
    {
        public const int MinimumPagesPerFrame = 4;
        public const int MaximumPagesPerFrame = 20;
        public const int MinimumPhysicalCacheMB = 16;
        public const int MaximumPhysicalCacheMB = 1024;
        public const int DefaultPcPhysicalCacheMB = 360;
        public const int DefaultMobilePhysicalCacheMB = 64;
        public const float MinimumLodDistanceScale = .25f;
        public const float MaximumLodDistanceScale = 2f;
        public const int MaximumGeometryLod = MeshCdlod.HeightMaxLod;

        [SerializeField, InspectorName("PC")] private MaxTerrainProfile m_pc = new MaxTerrainProfile(MaximumPagesPerFrame, DefaultPcPhysicalCacheMB);
        [SerializeField, InspectorName("Mobile")] private MaxTerrainProfile m_mobile = new MaxTerrainProfile(MinimumPagesPerFrame, DefaultMobilePhysicalCacheMB, MaxTerrainGeometryPreset.Budget);

        public MaxTerrainProfile GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;

        public int PagesPerFrame => GetProfile(MaxRenderQuality.Platform)?.PagesPerFrame
            ?? DefaultPagesPerFrame(MaxRenderQuality.Platform);
        public int PhysicalCacheMB => GetProfile(MaxRenderQuality.Platform)?.PhysicalCacheMB
            ?? DefaultPhysicalCacheMB(MaxRenderQuality.Platform);
        public MaxTerrainGeometryPreset GeometryPreset => GetProfile(MaxRenderQuality.Platform)?.GeometryPreset
            ?? DefaultGeometryPreset(MaxRenderQuality.Platform);
        public float LodDistanceScale => GetProfile(MaxRenderQuality.Platform)?.LodDistanceScale ?? 1f;
        public int MinimumGeometryLod => GetProfile(MaxRenderQuality.Platform)?.MinimumGeometryLod ?? 0;

        public static int DefaultPagesPerFrame(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? MinimumPagesPerFrame : MaximumPagesPerFrame;
        public static int DefaultPhysicalCacheMB(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? DefaultMobilePhysicalCacheMB : DefaultPcPhysicalCacheMB;
        public static MaxTerrainGeometryPreset DefaultGeometryPreset(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? MaxTerrainGeometryPreset.Budget : MaxTerrainGeometryPreset.Direct;
        public static float ClampLodDistanceScale(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 1f : Mathf.Clamp(value, MinimumLodDistanceScale, MaximumLodDistanceScale);

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new MaxTerrainProfile(MaximumPagesPerFrame, DefaultPcPhysicalCacheMB);
            if (m_mobile == null) m_mobile = new MaxTerrainProfile(MinimumPagesPerFrame, DefaultMobilePhysicalCacheMB, MaxTerrainGeometryPreset.Budget);
            m_pc.Validate();
            m_mobile.Validate();
#if UNITY_EDITOR
            // Request an editor update; resource/history changes happen at
            // the next render boundary, never inside this Inspector callback.
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }
    }
}
