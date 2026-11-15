#ifndef MAX_TERRAIN_GBUFFER_INCLUDED
#define MAX_TERRAIN_GBUFFER_INCLUDED

// MaxTerrain只负责求出表面属性。太阳、局部灯、IBL、AO、雾和曝光全部由
// MaxSRP的统一Lighting与后效处理；不把旧DDLightSurface的结果写入GBuffer。
struct MaxTerrainGBufferOutputs
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 attributes : SV_Target2;
    float4 bakedAndAo : SV_Target3;
};

MaxTerrainGBufferOutputs MaxTerrainPackSurface(DDSurface surface)
{
    MaxTerrainGBufferOutputs output;
    output.albedo = float4(saturate(surface.albedo), 1);
    // A=0禁止把相机相关CDLOD morph当作普通静止几何做相机运动回退。
    // 显式MaxTerrainMotion Pass写出的有效运动向量才允许使用颜色历史。
    output.normal = float4(normalize(surface.normal) * 0.5 + 0.5, 0);
    // 当前没有地形天空可见度/Lightmap：visibility=1、baked-valid=0。
    // Surface16为非金属契约；粗糙度直接传给统一BRDF，不在此重复光照。
    output.attributes = float4(1, 0, saturate(surface.roughness), 0);
    output.bakedAndAo = float4(0, 0, 0, saturate(surface.ao));
    return output;
}

MaxTerrainGBufferOutputs MaxTerrainPackDiagnostic(float3 color)
{
    // AO=-1为显式unlit诊断哨兵：宿主Lighting直接输出RGB，连F0镜面项也旁路。
    // 它仍受统一TAA/曝光/色调映射；精确原值应检查GBuffer或页面纹理。
    MaxTerrainGBufferOutputs output = (MaxTerrainGBufferOutputs)0;
    output.normal = float4(0.5, 1, 0.5, 0);
    output.attributes = float4(1, 0, 1, 0);
    output.bakedAndAo = float4(max(color, 0), -1);
    return output;
}

float3 MaxTerrainGeometryDiagnostic(Varyings input)
{
    float3 normal = normalize(cross(ddy(input.world), ddx(input.world)));
    if (normal.y < 0) normal = -normal;
    float illumination = 0.3 + 0.7 * saturate(dot(normal, normalize(float3(-0.45, 0.8, 0.3))));
    float3 color = float3(0.62, 0.65, 0.68) * illumination;
    if (_MeshDebug == 1) color = LevelColor(input.diagnostic.x) * illumination;
    if (_MeshDebug == 3) color = LevelColor(min(input.diagnostic.x, MESH_HEIGHT_MAX_LOD)) * illumination;
    if (_MeshDebug == 2) color = lerp(float3(0.15, 0.35, 0.8), float3(0.95, 0.4, 0.12), input.diagnostic.y);
    if (_MeshDebug == 4)
    {
        float3 coordinates = float3(input.grid, input.grid.x + input.grid.y);
        float3 edge = abs(frac(coordinates + 0.5) - 0.5) / max(fwidth(coordinates), 1e-4);
        float gridLine = 1 - saturate(min(edge.x, min(edge.y, edge.z)));
        color = lerp(color, float3(0.035, 0.045, 0.055), gridLine);
    }
    return color;
}

DDSurface MaxTerrainReadDirectSurface(Varyings input, float3 positionDx, float3 positionDy)
{
    DDSurface surface = (DDSurface)0;
    surface.normal = float3(0, 1, 0);
    surface.roughness = surface.ao = 1;
    surface.invalid = input.diagnostic.z;
    float3 geometryNormal;
    if (_MeshSourceNormals > 0.5)
    {
        float normalInvalid, sourceLevel;
        geometryNormal = MeshSourceHeightNormal(input.world.xz, normalInvalid, sourceLevel);
        surface.invalid = max(surface.invalid, normalInvalid);
    }
    else
    {
        // 梯度由片元入口在任何缺页/诊断分支前求出，不在缺页边缘重新求导。
        geometryNormal = normalize(cross(positionDy, positionDx));
        if (geometryNormal.y < 0) geometryNormal = -geometryNormal;
    }
    #if defined(DD_SURFACE_ON)
        DDControls controls = DDReadControls(input.world.xz);
        float geometryInvalid = surface.invalid;
        surface = DDEvaluateSurface(input.world, geometryNormal, controls);
        surface.invalid = max(surface.invalid, geometryInvalid);
        #if defined(DD_LIVE_OVERLAYS)
            // 这里只用于Direct高保真参考和缺页回退。VT命中已经包含静态覆盖。
            DDApplyLiveOverlays(DDLiveSourceWorld(input.world), geometryNormal,
                positionDx, positionDy, surface);
        #endif
    #else
        surface.albedo = float3(0.62, 0.65, 0.68);
        surface.normal = geometryNormal;
    #endif
    return surface;
}

MaxTerrainGBufferOutputs MaxTerrainPackSurfaceOrDebug(DDSurface surface)
{
    if (surface.invalid > 0.01) return MaxTerrainPackDiagnostic(float3(1, 0, 1));
    if (_DDSurfaceDebug == 1) return MaxTerrainPackDiagnostic(surface.albedo);
    if (_DDSurfaceDebug == 2) return MaxTerrainPackDiagnostic(surface.height.xxx);
    if (_DDSurfaceDebug == 3) return MaxTerrainPackDiagnostic(surface.normal * 0.5 + 0.5);
    if (_DDSurfaceDebug == 4) return MaxTerrainPackDiagnostic(surface.roughness.xxx);
    if (_DDSurfaceDebug == 5) return MaxTerrainPackDiagnostic(surface.ao.xxx);
    return MaxTerrainPackSurface(surface);
}

MaxTerrainGBufferOutputs MaxTerrainDirectGBuffer(Varyings input)
{
    // 与VT回退相同：连续梯度先计算，随后才允许进入逐像素分支。
    float3 positionDx = ddx(input.world), positionDy = ddy(input.world);
    if (input.diagnostic.z > 0.01) return MaxTerrainPackDiagnostic(float3(1, 0, 1));
    if (_MeshDebug != 0) return MaxTerrainPackDiagnostic(MaxTerrainGeometryDiagnostic(input));
    #if defined(DD_SURFACE_ON)
        if (_DDSurfaceDebug >= 6 && _DDSurfaceDebug <= 11)
            return MaxTerrainPackDiagnostic(DDControlDebug(DDReadControls(input.world.xz)).rgb);
    #endif
    return MaxTerrainPackSurfaceOrDebug(MaxTerrainReadDirectSurface(input, positionDx, positionDy));
}

#endif
