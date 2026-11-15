// Persistent v4 page recipe hashes this producer name; keep it for archive compatibility.
Shader "DirectDeltaMesh/SinglePageProducer"
{
    // Delta 写页入口。世界 VT 在运行时生成 RVT，离线烘焙 SVT 也执行同一个 Pass。
    // 每次绘制只接收一个页的世界范围和源高度、控制点窗口。
    Properties
    {
        [HideInInspector] _DDRvtHeight ("Fixed 1m source heights", 2D) = "black" {}
        [HideInInspector] _DDRvtControls ("Fixed 1m source controls", 2D) = "black" {}
        [HideInInspector] _DDAlbedoHeight ("Compressed Albedo Height", 2DArray) = "" {}
        [HideInInspector] _DDNormalRoughAo ("Compressed Normal Roughness AO", 2DArray) = "" {}
        _DDHeightBlend ("Fixed height blend Scale Sharpness", Vector) = (4,.5,0,0)
        [Toggle] _DDHexTiling ("Hex Tiling", Float) = 1
        _DDHexParams ("Hex rotation, contrast falloff, exponent", Vector) = (.25,.6,7,0)
        [HideInInspector] _DDCliffProjection ("Cliff projection", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "SinglePageRvtSurface.cginc"
        #include "SinglePageRvtCommon.cginc"
        #include "SinglePageRvtSource.cginc"

        struct PageVaryings { float4 position : SV_POSITION; };
        PageVaryings PageVert(uint vertexId : SV_VertexID)
        {
            PageVaryings output;
            float2 corner = float2((vertexId << 1) & 2u, vertexId & 2u);
            output.position = float4(corner * 2 - 1, 0, 1);
            return output;
        }

        struct PageOutputs
        {
            float4 albedoAo : SV_Target0;
            float4 normalRoughnessHeight : SV_Target1;
        };

        PageOutputs PageFrag(PageVaryings input)
        {
            // 固定单页对照与世界 VT 共用此写页入口。_DDRvtRect 给出 XZ 原点和
            // 世界宽度，_DDRvtInfo 给出有效纹素数和边框。SV_POSITION 是页 RT
            // 的物理像素中心，所以输出的第 0 行对应这一页较小的世界 Z。
            float2 worldXZ = DDRvtPageWorldXZ(input.position.xy);
            // 用全屏三角形覆盖页面 RT，再在每个纹素中心从源高度场重建世界位置。
            // 高度来自固定源窗口，相机位置和屏幕网格的 CDLOD morph 都不参与。
            #if defined(DD_WORLD_VT_RGBA8)
            // 所有世界页都使用连续源高度和解析坡度，包括 mip0 与离线 mip8。
            // 输出纹素覆盖多少米由页面规定，不依赖相机或物理池的分组。
            float2 heightSlope;
            float height = DDRvtContinuousHeight(worldXZ, heightSlope);
            float3 world = float3(worldXZ.x, height, worldXZ.y);
            DDRvtPositionDx = float3(_DDRvtTexelGrid.z, heightSlope.x * _DDRvtTexelGrid.z, 0);
            DDRvtPositionDy = float3(0, heightSlope.y * _DDRvtTexelGrid.w, _DDRvtTexelGrid.w);
            #else
            float3 world = float3(worldXZ.x, DDRvtSourceHeight(worldXZ), worldXZ.y);
            DDRvtPositionDx = ddx(world);
            DDRvtPositionDy = ddy(world);
            #endif
            float3 sourceNormal = DDRvtSourceNormal(worldXZ);
            DDControls controls = DDRvtReadControls(worldXZ);
            DDSurface surface = DDEvaluateSurface(world, sourceNormal, controls);
            PageOutputs output;
            // 两张纹理必须作为一页一起发布。Height 是混合后的材质高度，
            // 源地形海拔只参与计算，不占用缓存通道。
            output.albedoAo = float4(surface.albedo, surface.ao);
            output.normalRoughnessHeight = float4(DDRvtEncodeNormal(surface.normal), surface.roughness, surface.height);
            #if defined(DD_WORLD_VT_RGBA8)
            // 世界页的数据图是线性 UNorm。字节 0 留给无效纹素，合法高度占 1..255。
            // 底色图的 sRGB 写入只转换 RGB，AO 仍是线性值。
            output.normalRoughnessHeight.a = (1.0 + round(saturate(surface.height) * 254.0)) / 255.0;
            #endif
            if (surface.invalid > .5 || !DDRvtHeightFootprintValid(worldXZ))
            {
                output.albedoAo = float4(1,0,1,1);
                #if defined(DD_WORLD_VT_RGBA8)
                output.normalRoughnessHeight = float4(.5,.5,1,0);
                #else
                // 固定单页对照的 Half 格式用负值标记无效纹素。
                output.normalRoughnessHeight = float4(.5,.5,1,-1);
                #endif
            }
            return output;
        }

        #include "WorldVtOverlayCommon.cginc"

        DDOverlayMrtOutputs PageOverlayBaseFrag(PageVaryings input)
        {
            // 仅覆盖页走此入口。旧 PageFrag / Pass0 保留原算式与双 RT 输出，
            // 没有覆盖（含 border）的页面仍可以逐字节回归原写页路径。
            float2 worldXZ = DDRvtPageWorldXZ(input.position.xy);
            float2 heightSlope;
            float height = DDRvtContinuousHeight(worldXZ, heightSlope);
            float3 world = float3(worldXZ.x, height, worldXZ.y);
            DDRvtPositionDx = float3(_DDRvtTexelGrid.z, heightSlope.x * _DDRvtTexelGrid.z, 0);
            DDRvtPositionDy = float3(0, heightSlope.y * _DDRvtTexelGrid.w, _DDRvtTexelGrid.w);
            float3 sourceNormal = DDRvtSourceNormal(worldXZ);
            DDControls controls = DDRvtReadControls(worldXZ);
            DDSurface surface = DDEvaluateSurface(world, sourceNormal, controls);
            if (surface.invalid > .5 || !DDRvtHeightFootprintValid(worldXZ))
            {
                // CPU 整页校验是正式发布门禁；保留意外输入的紫色/零高度诊断。
                // 不把无效原料伪装为合法覆盖页。
                return DDOverlayPackTargets(float3(1,0,1), sourceNormal, 1, 1, 0, 1);
            }
            return DDOverlayPackTargets(surface.albedo, surface.normal, surface.ao,
                surface.roughness, DDOverlayHeightCode(surface.height), 1);
        }
        ENDCG
        Pass
        {
            Name "ProduceSurface"
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma multi_compile_local_fragment _ DD_WORLD_VT_RGBA8
            #pragma vertex PageVert
            #pragma fragment PageFrag
            ENDCG
        }
        Pass
        {
            // CPU 先完整写满三附件，再按稳定顺序提交道路与贴花。
            Name "ProduceOverlayBase"
            Blend Off
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma multi_compile_local_fragment _ DD_WORLD_VT_RGBA8
            #pragma vertex PageVert
            #pragma fragment PageOverlayBaseFrag
            ENDCG
        }
    }
    Fallback Off
}
