Shader "MaxTerrain/SinglePageTerrain"
{
    // Delta-only single-page consumer; original geometry/evaluator files remain unchanged.
    // Cache hits bypass source normals and material evaluation; misses retain the direct path.
    Properties
    {
        [HideInInspector] _MeshHeightClip ("R16 height Clipmap", 2DArray) = "" {}
        [HideInInspector] _MeshPatch ("World patch origin and size", Vector) = (0,0,32,32)
        [HideInInspector] _MeshParams ("Morph start/end, LOD, quarter mask", Vector) = (80,140,0,15)
        [HideInInspector] _MeshDebug ("Geometry diagnostic", Int) = 0
        [HideInInspector] _MeshHeightScale ("Dataset height range in metres", Float) = 1200
        // 默认用源高度场差分法线；0仅用于同机位对照旧三角面法线，不影响几何。
        [HideInInspector] _MeshSourceNormals ("Source heightfield shading normals", Float) = 1
        [HideInInspector] _DDControlClip ("16-material R16 ID Clipmap", 2DArray) = "" {}
        [HideInInspector] _DDControlResident ("Full resident R16 IDs and opacity", 2D) = "black" {}
        [HideInInspector] _DDAlbedoHeight ("Compressed Albedo Height", 2DArray) = "" {}
        [HideInInspector] _DDNormalRoughAo ("Compressed Normal Roughness AO", 2DArray) = "" {}
        _DDHeightBlend ("Fixed height blend Scale Sharpness", Vector) = (4,.5,0,0)
        // 直接Hex Tiling默认开；H/组件可切回原周期平铺。参考与许可见DeltaHexTiling.cginc。
        [Toggle] _DDHexTiling ("Hex Tiling", Float) = 1
        _DDHexParams ("Hex rotation, contrast falloff, exponent", Vector) = (.25,.6,7,0)
        // 直接三平面陡壁投影，默认开。0仅供验证器与旧XZ投影同机位对照。
        // Producer shares DeltaProjection/DeltaMaterial; cached normals are already world-space.
        [HideInInspector] _DDCliffProjection ("Direct cliff projection", Float) = 1
        _DDLightDirection ("Terrain light direction", Vector) = (-.45,.8,.3,0)
        [HDR] _DDLightColor ("Terrain sunlight", Color) = (3,2.85,2.55,1)
        [HDR] _DDAmbientColor ("Terrain ambient", Color) = (.18,.22,.28,1)
        [HideInInspector] _DDSurfaceDebug ("Surface diagnostic", Int) = 0
        [HideInInspector] _DDRvtAlbedoAo ("RVT Albedo AO", 2D) = "black" {}
        [HideInInspector] _DDRvtNormalRoughnessHeight ("RVT Normal Roughness Height", 2D) = "black" {}
        [HideInInspector] _DDRvtMode ("0 Direct, 1 Cache, 2 Coverage", Int) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        CGINCLUDE
            #include "UnityCG.cginc"
            #include "SinglePageRvtSurface.cginc"
            #include "SinglePageRvtCommon.cginc"
            #pragma enable_d3d11_debug_symbols

            #define ddx DDRvtMaterialDdx
            #define ddy DDRvtMaterialDdy
            #include "../../Geometry/MeshGeometry.cginc"
            #undef ddx
            #undef ddy
            #include "SinglePageRvtConsumer.cginc"
        ENDCG
        Pass
        {
            Name "MeshOnly"
            Cull Back ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment DDRvtFrag
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap maxcount:500
            #pragma multi_compile_local _ DD_SURFACE_ON
            #pragma multi_compile_local _ DD_CONTROL_RESIDENT
            ENDCG
        }
    }
    Fallback Off
}
