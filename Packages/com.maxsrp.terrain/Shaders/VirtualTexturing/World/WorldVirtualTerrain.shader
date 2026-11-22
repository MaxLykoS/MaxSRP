Shader "MaxTerrain/WorldVirtualTerrain"
{
    // Delta 屏幕入口：MaxTerrainGBufferVertex 重建网格，MaxTerrainVirtualGBuffer取材质并写GBuffer。
    // 同一主地形Pass输出四MRT GBuffer并向u4稀疏写入请求，不单独重画反馈网格。
    // SinglePageProducer.shader 按页范围写缓存。
    // 屏幕网格的 Clipmap 和 morph 用于显示与反馈。生成缓存时，写页 Pass 自己读取固定源数据。
    Properties
    {
        [HideInInspector] _MeshHeightClip ("R16 height Clipmap", 2DArray) = "" {}
        [HideInInspector] _MeshPatch ("World patch origin and size", Vector) = (0,0,32,32)
        [HideInInspector] _MeshParams ("Morph start/end, LOD, quarter mask", Vector) = (80,140,0,15)
        [HideInInspector] _MeshDebug ("Geometry diagnostic", Int) = 0
        [HideInInspector] _MeshHeightScale ("Dataset height range in metres", Float) = 1200
        [HideInInspector] _MeshSourceNormals ("Source heightfield shading normals", Float) = 1
        [HideInInspector] _DDControlClip ("16-material R16 ID Clipmap", 2DArray) = "" {}
        [HideInInspector] _DDControlResident ("Full resident R16 IDs and opacity", 2D) = "black" {}
        [HideInInspector] _DDAlbedoHeight ("Compressed Albedo Height", 2DArray) = "" {}
        [HideInInspector] _DDNormalRoughAo ("Compressed Normal Roughness AO", 2DArray) = "" {}
        _DDHeightBlend ("Fixed height blend Scale Sharpness", Vector) = (4,.5,0,0)
        [Toggle] _DDHexTiling ("Hex Tiling", Float) = 1
        _DDHexParams ("Hex rotation, contrast falloff, exponent", Vector) = (.25,.6,7,0)
        [HideInInspector] _DDCliffProjection ("Direct cliff projection", Float) = 1
        _DDLightDirection ("Terrain light direction", Vector) = (-.45,.8,.3,0)
        [HDR] _DDLightColor ("Terrain sunlight", Color) = (3,2.85,2.55,1)
        [HDR] _DDAmbientColor ("Terrain ambient", Color) = (.18,.22,.28,1)
        [HideInInspector] _DDSurfaceDebug ("Surface diagnostic", Int) = 0
        [HideInInspector] _DDVtAlbedoAo ("World VT Albedo AO", 2DArray) = "" {}
        [HideInInspector] _DDVtNormalRoughnessHeight ("World VT Normal Roughness Height", 2DArray) = "" {}
        [HideInInspector] _DDVtReady ("Published cache ready", Float) = 0
        [HideInInspector] _DDVtMode ("0 Direct, 1 Cache, 2 Provenance, 3 Mip Gap, 4 Raw", Int) = 0
        [HideInInspector] _DDVtMipBias ("Virtual mip bias in levels", Float) = 0
        [HideInInspector] _DDVtMaxAnisotropy ("Cached page maximum anisotropy", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        CGINCLUDE
            #include "UnityCG.cginc"
            #include "../SinglePage/SinglePageRvtSurface.cginc"
            #include "../SinglePage/SinglePageRvtCommon.cginc"
            #pragma enable_d3d11_debug_symbols

            // 让回退路径也用在 DDVtFrag 入口算好的梯度，跨页边界时不在分支内部取 ddx/ddy。
            #define ddx DDRvtMaterialDdx
            #define ddy DDRvtMaterialDdy
            #include "../../Geometry/MeshGeometry.cginc"
            #undef ddx
            #undef ddy
            #include "../../Rendering/MaxTerrainGBuffer.cginc"
            #include "../../Rendering/MaxTerrainGeometryPasses.cginc"
            #include "WorldVtConsumer.cginc"
        ENDCG

        Pass
        {
            // Game 相机提交这一遍。命中时从统一页池取材质，缺页时由本入口直接计算材质。
            Name "MaxTerrainGBuffer"
            Tags { "LightMode"="MaxDeferred" }
            Cull Back ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 5.0
            #pragma vertex MaxTerrainGBufferVertex
            #pragma fragment MaxTerrainVirtualGBuffer
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap maxcount:500
            #pragma multi_compile_local _ DD_SURFACE_ON
            #pragma multi_compile_local _ DD_CONTROL_RESIDENT
            #pragma multi_compile_local_fragment _ DD_LIVE_OVERLAYS
            #pragma multi_compile_local_fragment _ DD_WORLD_VT_BC3
            #pragma multi_compile_local_fragment _ DD_WORLD_VT_ANISOTROPIC
            #pragma multi_compile_local_fragment _ DD_WORLD_VT_MAIN_FEEDBACK
            ENDCG
        }

        Pass
        {
            Name "MaxTerrainShadow"
            Tags { "LightMode"="ShadowCaster" }
            Cull Back ZWrite On ZTest LEqual ColorMask 0
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 5.0
            #pragma vertex MaxTerrainShadowVertex
            #pragma fragment MaxTerrainShadowFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap maxcount:500
            ENDCG
        }
        Pass
        {
            Name "MaxTerrainMotion"
            Tags { "LightMode"="MotionVectors" }
            Cull Back ZWrite Off ZTest Equal
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 5.0
            #pragma vertex MaxTerrainMotionVertex
            #pragma fragment MaxTerrainMotionFragment
            #pragma multi_compile_fragment _ MAX_TAA_VELOCITY_UINT
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap maxcount:500
            ENDCG
        }
    }
    Fallback Off
}
