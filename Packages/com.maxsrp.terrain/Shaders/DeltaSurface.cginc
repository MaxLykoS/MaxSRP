#ifndef DIRECT_DELTA_MESH_SURFACE_INCLUDED
#define DIRECT_DELTA_MESH_SURFACE_INCLUDED

// 阅读入口：DDShade → DDReadControls → DDEvaluateSurface → DDLightSurface。
// Delta直接回退与VT写页共用此求值器；DirectDelta使用私有三平面参照副本。
// 注释标记：[PPT原文]/[PPT公式]为用户给出的原文；[等价改写]对应当前实现；
// [工程实现]/[工程约定]/[亮带修复]为本工程设计，不能当成三角洲未公开的源码。
// 16个稳定ID就是数组slice；源材质全部常驻，无DTA。RVT/SVT缓存本求值器的结果。
Texture2DArray<float> _DDControlClip;
Texture2D<float> _DDControlResident;
float4 _DDControlOrigins[6]; // world origin X/Z, source stride, valid
float4 _DDControlInfo; // world metres, texture/Clipmap side, levels, base spacing
Texture2DArray<float4> _DDAlbedoHeight;
Texture2DArray<float4> _DDNormalRoughAo;
SamplerState sampler_DDAlbedoHeight; // Repeat / Trilinear, shared by both arrays
float4 _DDMaterialUv[16]; // UV per world metre X/Z, offset U/V
float4 _DDMaterialInfo[16]; // normal strength, source resolution, last mip, valid
float4 _DDHeightBlend; // Fixed Scale, Sharpness; Z/W unused
float _DDCliffProjection; // 1=按法线权重随机单投影；0=旧XZ验证对照。
float _DDHexTiling; // 1=Hex去重复，0=周期平铺；直接回退和写页共用此源采样。
float4 _DDHexParams; // rotation strength, falloff contrast, weight exponent, reserved
float4 _DDLightDirection;
float4 _DDLightColor;
float4 _DDAmbientColor;
int _DDSurfaceDebug;

struct DDControls
{
    int3 ids;                 // 本三角的稳定ID，从小到大；只使用前count槽。
    float3 opacity;           // PPT有序覆盖，仅F6/F7诊断使用；不是最终weights。
    int3 bottom, top;         // 三个原始控制点各自的两层ID。
    float3 coverage, bary;    // coverage=三点Top覆盖率；bary=三点空间插值系数。
    int count;
    int level;                // 控制源级别，与材质mip或mesh LOD独立。
    float invalid;
};

struct DDMaterial
{
    float3 albedo, normal;
    float height, roughness, ao;
};

struct DDSurface
{
    float3 albedo, normal;
    float height, roughness, ao, invalid;
};

#include "DeltaControls.cginc"
#include "DeltaProjection.cginc"
#include "DeltaHexTiling.cginc"
#include "DeltaMaterial.cginc"
#include "DeltaLighting.cginc"
#include "MeshSurfaceOverlays.cginc"

// 工程调试配色，不参与材质混合。
float3 DDIdColor(int id)
{
    float hue = frac((id + 1) * .61803398875);
    return saturate(abs(frac(hue + float3(0, 2.0/3.0, 1.0/3.0)) * 6 - 3) - 1) * .75 + .15;
}

float3 DDControlLevelColor(int level)
{
    if (level == 0) return float3(.25,.7,.38);
    if (level == 1) return float3(.35,.62,.92);
    if (level == 2) return float3(.95,.72,.28);
    if (level == 3) return float3(.85,.4,.27);
    if (level == 4) return float3(.67,.42,.8);
    return float3(.15,.8,.82);
}

// F6–F11只看控制数据，提前返回，避免为调试ID加载材质源。
float4 DDControlDebug(DDControls controls)
{
    if (controls.invalid > .5) return float4(1,0,1,1);
    if (_DDSurfaceDebug == 6)
    {
        // Resolve the dominant ID using ordered control coverage, before height blending.
        float upper1 = controls.opacity.y, upper2 = controls.opacity.z;
        float3 coverage = float3((1 - upper1) * (1 - upper2), upper1 * (1 - upper2), upper2);
        int dominant = coverage.y > coverage.x ? 1 : 0;
        if (coverage.z > coverage[dominant]) dominant = 2;
        return float4(DDIdColor(controls.ids[dominant]),1);
    }
    if (_DDSurfaceDebug == 7) return float4(controls.opacity.yz,0,1);
    if (_DDSurfaceDebug == 11) return float4(DDControlLevelColor(controls.level),1);
    int layer = _DDSurfaceDebug - 8;
    if (layer >= controls.count) return float4(0,0,0,1);
    return float4(DDIdColor(controls.ids[layer]),1);
}

float4 DDShade(float3 worldPosition, float3 geometryNormal)
{
    #if defined(DD_LIVE_OVERLAYS)
    // 必须在控制诊断/材质非法等逐像素分支之前取屏幕梯度。
    float3 overlayDx = ddx(worldPosition), overlayDy = ddy(worldPosition);
    #endif
    // 1. 三次整数控制读取；错误数据用紫色显式显示。
    DDControls controls = DDReadControls(worldPosition.xz);
    [branch] if (_DDSurfaceDebug >= 6 && _DDSurfaceDebug <= 11)
        return DDControlDebug(controls);

    // 2. 材质采样和亮带修复；F1–F5直接显示光照前的混合通道。
    DDSurface surface = DDEvaluateSurface(worldPosition, geometryNormal, controls);
    if (surface.invalid > .5) return float4(1,0,1,1);
    #if defined(DD_LIVE_OVERLAYS)
    DDApplyLiveOverlays(DDLiveSourceWorld(worldPosition), geometryNormal, overlayDx, overlayDy, surface);
    #endif
    if (_DDSurfaceDebug == 1) return float4(surface.albedo,1);
    if (_DDSurfaceDebug == 2) return float4(surface.height.xxx,1);
    if (_DDSurfaceDebug == 3) return float4(surface.normal * .5 + .5,1);
    if (_DDSurfaceDebug == 4) return float4(surface.roughness.xxx,1);
    if (_DDSurfaceDebug == 5) return float4(surface.ao.xxx,1);
    // 3. F12/正常显示才进入光照。
    return float4(DDLightSurface(surface,worldPosition),1);
}

#endif
