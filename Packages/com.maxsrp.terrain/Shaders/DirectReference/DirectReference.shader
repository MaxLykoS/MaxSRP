Shader "MaxTerrain/DirectReference"
{
    // DirectDelta 对照入口。几何、法线和材质实现都在此入口的 DirectReference/ 目录。
    // 从自己的高度 Clipmap 重建网格，屏幕片元直接计算材质。DD_CONTROL_RESIDENT 选择常驻控制图。
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
        // 不创建VT；材质采样/法线变换见DeltaProjection.cginc和DeltaMaterial.cginc。
        [HideInInspector] _DDCliffProjection ("Direct cliff projection", Float) = 1
        _DDLightDirection ("Terrain light direction", Vector) = (-.45,.8,.3,0)
        [HDR] _DDLightColor ("Terrain sunlight", Color) = (3,2.85,2.55,1)
        [HDR] _DDAmbientColor ("Terrain ambient", Color) = (.18,.22,.28,1)
        [HideInInspector] _DDSurfaceDebug ("Surface diagnostic", Int) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        // GBuffer、阴影和运动向量共用同一高度重建；材质仅在GBuffer中执行。
        CGINCLUDE
            #include "UnityCG.cginc"
            #include "DeltaSurface.cginc"
            #pragma enable_d3d11_debug_symbols

            #include "MeshGeometry.cginc"
            #include "../Rendering/MaxTerrainGBuffer.cginc"
            #include "../Rendering/MaxTerrainGeometryPasses.cginc"
        ENDCG
        Pass
        {
            Name "MaxTerrainGBuffer"
            Tags { "LightMode"="MaxDeferred" }
            Cull Back ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 5.0
            #pragma vertex MaxTerrainGBufferVertex
            #pragma fragment MaxTerrainDirectGBuffer
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap maxcount:500
            #pragma multi_compile_local _ DD_SURFACE_ON
            #pragma multi_compile_local _ DD_CONTROL_RESIDENT
            #pragma multi_compile_local_fragment _ DD_LIVE_OVERLAYS
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
