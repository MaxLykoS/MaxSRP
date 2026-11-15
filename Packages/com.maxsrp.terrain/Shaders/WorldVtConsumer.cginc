#ifndef DIRECT_DELTA_WORLD_VT_CONSUMER_INCLUDED
#define DIRECT_DELTA_WORLD_VT_CONSUMER_INCLUDED

#include "WorldVtAddress.cginc"
#include "WorldVtMainFeedback.cginc"

#define DD_VT_SLOT_MASK 2047u

// 页表 word 的低 11 位存 slot+1，11..15 位存实际驻留的 global mip。
// CPU 提前将缺页位置指向最近可用父页；shader 不再循环搜索哈希桶和父 mip。
// RVT、SVT、所有 mip 的同一个物理 slice 都同时索引下面两张数组。
// 两种格式均用 sRGB 底色 RGB。RGBA8 的底色 A 存 AO，BC3 的底色 A 存八面体法线第一分量。
Texture2DArray<float4> _DDVtAlbedoAo;
// RGBA8: 法线 RG、粗糙度 B、高度码 A。BC3: AO R、粗糙度 G、预留 B、法线第二分量 A。
Texture2DArray<float4> _DDVtNormalRoughnessHeight;
float _DDVtReady;
int _DDVtMode;

struct DDVtResidentPage
{
    uint slot;
    uint mip;
    uint source; // 1 = RVT, 2 = SVT。0 用于诊断中的直接着色。
};

float3 DDVtDecodePageNormal(float4 color, float4 data)
{
    #if defined(DD_WORLD_VT_BC3)
        return DDRvtDecodeNormal(float2(color.a, data.a));
    #else
        return DDRvtDecodeNormal(data.rg);
    #endif
}

bool DDVtSamplePage(float2 worldXZ, uint2 pageXZ, DDVtResidentPage page, out DDSurface surface)
{
    surface = (DDSurface)0;
    // 物理页的内区从 texel 8 开始。-0.5 把整数索引对到 texel 中心，
    // 使共享世界边界处的双线性足迹跨进 border，而不是在内区边缘被截断。
    float pageWidth = DDVtPageWidthMetres(page.mip);
    float2 inside = worldXZ / pageWidth - (float2)pageXZ;
    float2 pixel = inside * DD_VT_INTERIOR + DD_VT_BORDER - 0.5;
    int2 low = (int2)floor(pixel);
    float2 weight = frac(pixel);
    int2 p00 = clamp(low, 0, DD_VT_PHYSICAL - 1);
    int2 p10 = clamp(low + int2(1, 0), 0, DD_VT_PHYSICAL - 1);
    int2 p01 = clamp(low + int2(0, 1), 0, DD_VT_PHYSICAL - 1);
    int2 p11 = clamp(low + 1, 0, DD_VT_PHYSICAL - 1);

    float4 n00 = _DDVtNormalRoughnessHeight.Load(int4(p00, page.slot, 0));
    float4 n10 = _DDVtNormalRoughnessHeight.Load(int4(p10, page.slot, 0));
    float4 n01 = _DDVtNormalRoughnessHeight.Load(int4(p01, page.slot, 0));
    float4 n11 = _DDVtNormalRoughnessHeight.Load(int4(p11, page.slot, 0));
    // BC3 世界页先通过整页输入校验再发布。B 仅预留，不表示逐纹素有效性。
    #if !defined(DD_WORLD_VT_BC3)
        // 历史 RGBA8 对照保留高度码约定：0 无效，1/255 对应合法高度 0。
        // 仍先判断原始纹素，避免无效码在双线性插值时混入合法高度。
        if (min(min(n00.a, n10.a), min(n01.a, n11.a)) == 0.0) return false;
    #endif

    float4 a00 = _DDVtAlbedoAo.Load(int4(p00, page.slot, 0));
    float4 a10 = _DDVtAlbedoAo.Load(int4(p10, page.slot, 0));
    float4 a01 = _DDVtAlbedoAo.Load(int4(p01, page.slot, 0));
    float4 a11 = _DDVtAlbedoAo.Load(int4(p11, page.slot, 0));
    float4 albedoAo = lerp(lerp(a00, a10, weight.x), lerp(a01, a11, weight.x), weight.y);

    // 八面体编码在折线两侧可相距很远。先恢复四个世界空间向量，再插值并归一化。
    // 这样陡壁背向细节也能跨编码折线平滑过渡。缓存法线已经包含源地形法线和材质法线。
    float3 normal00 = DDVtDecodePageNormal(a00, n00);
    float3 normal10 = DDVtDecodePageNormal(a10, n10);
    float3 normal01 = DDVtDecodePageNormal(a01, n01);
    float3 normal11 = DDVtDecodePageNormal(a11, n11);
    float3 normal = lerp(lerp(normal00, normal10, weight.x), lerp(normal01, normal11, weight.x), weight.y);
    surface.albedo = albedoAo.rgb;
    surface.normal = dot(normal, normal) > 1e-10 ? normalize(normal) : normal00;
    #if defined(DD_WORLD_VT_BC3)
        float2 aoRoughness = lerp(lerp(n00.rg, n10.rg, weight.x), lerp(n01.rg, n11.rg, weight.x), weight.y);
        surface.ao = aoRoughness.x;
        surface.roughness = aoRoughness.y;
    #else
        float2 roughnessHeight = lerp(lerp(n00.ba, n10.ba, weight.x), lerp(n01.ba, n11.ba, weight.x), weight.y);
        surface.ao = albedoAo.a;
        surface.roughness = roughnessHeight.x;
        // 高度码的还原是线性变换，可在四个有效码完成插值后统一还原。
        surface.height = saturate((roughnessHeight.y * 255.0 - 1.0) / 254.0);
    #endif
    return true;
}

#if defined(DD_WORLD_VT_ANISOTROPIC)
bool DDVtSamplePageUnnormalized(float2 worldXZ, uint2 pageXZ, DDVtResidentPage page, out DDSurface surface)
{
    // AF1 的 DDVtSamplePage 保持原样。此入口只给 AF2/4 使用：同样的四点
    // 双线性取样，但不提前归一化法线，供长轴上多个 tap 一起累积。
    surface = (DDSurface)0;
    float pageWidth = DDVtPageWidthMetres(page.mip);
    float2 inside = worldXZ / pageWidth - (float2)pageXZ;
    float2 pixel = inside * DD_VT_INTERIOR + DD_VT_BORDER - 0.5;
    int2 low = (int2)floor(pixel);
    float2 weight = frac(pixel);
    int2 p00 = low;
    int2 p10 = low + int2(1, 0);
    int2 p01 = low + int2(0, 1);
    int2 p11 = low + 1;

    // 调用者将完整跨度限制为 14 个物理 texel，中心在 128 内区，
    // 因而四点全部落在 8 texel border 内；这里不靠坐标 clamp 隐藏越界。
    float4 n00 = _DDVtNormalRoughnessHeight.Load(int4(p00, page.slot, 0));
    float4 n10 = _DDVtNormalRoughnessHeight.Load(int4(p10, page.slot, 0));
    float4 n01 = _DDVtNormalRoughnessHeight.Load(int4(p01, page.slot, 0));
    float4 n11 = _DDVtNormalRoughnessHeight.Load(int4(p11, page.slot, 0));
    #if !defined(DD_WORLD_VT_BC3)
        // 历史 RGBA8 诊断仍保留其高度有效码约定；正式 BC3 的 B 不参与消费。
        if (min(min(n00.a, n10.a), min(n01.a, n11.a)) == 0.0) return false;
    #endif
    float4 a00 = _DDVtAlbedoAo.Load(int4(p00, page.slot, 0));
    float4 a10 = _DDVtAlbedoAo.Load(int4(p10, page.slot, 0));
    float4 a01 = _DDVtAlbedoAo.Load(int4(p01, page.slot, 0));
    float4 a11 = _DDVtAlbedoAo.Load(int4(p11, page.slot, 0));
    float4 albedoAo = lerp(lerp(a00, a10, weight.x), lerp(a01, a11, weight.x), weight.y);

    float3 normal00 = DDVtDecodePageNormal(a00, n00);
    float3 normal10 = DDVtDecodePageNormal(a10, n10);
    float3 normal01 = DDVtDecodePageNormal(a01, n01);
    float3 normal11 = DDVtDecodePageNormal(a11, n11);
    surface.albedo = albedoAo.rgb;
    surface.normal = lerp(lerp(normal00, normal10, weight.x), lerp(normal01, normal11, weight.x), weight.y);
    #if defined(DD_WORLD_VT_BC3)
        float2 aoRoughness = lerp(lerp(n00.rg, n10.rg, weight.x), lerp(n01.rg, n11.rg, weight.x), weight.y);
        surface.ao = aoRoughness.x;
        surface.roughness = aoRoughness.y;
    #else
        float2 roughnessHeight = lerp(lerp(n00.ba, n10.ba, weight.x), lerp(n01.ba, n11.ba, weight.x), weight.y);
        surface.ao = albedoAo.a;
        surface.roughness = roughnessHeight.x;
        surface.height = saturate((roughnessHeight.y * 255.0 - 1.0) / 254.0);
    #endif
    return true;
}

bool DDVtSampleAnisotropicPage(float2 worldXZ, uint2 pageXZ, DDVtResidentPage page, out DDSurface surface)
{
    DDVtScreenFootprint footprint = DDVtComputeScreenFootprint(DDRvtPositionDx.xz, DDRvtPositionDy.xz);
    // 使用实际驻留 mip 的密度。缺细页时父页的一个 texel 更宽，不能继续
    // 按目标 mip 的纹素密度选择 tap 数和偏移；足迹小于一纹素时只读中心。
    float worldTexel = DDVtPageWidthMetres(page.mip) / (float)DD_VT_INTERIOR;
    float texelsPerMetre = rcp(worldTexel);
    float majorTexels = footprint.majorMetres * texelsPerMetre;
    // 近圆足迹无需仅因整数 mip 的舍入误差就多取一个方向的样本。
    // 用短轴或实际驻留纹素中的较大者作为单个 tap 已覆盖的宽度；
    // 很粗的父页因此自然退回单点。小容差避免精确整数比值的浮点抖动。
    float anisotropicRatio = footprint.majorMetres / max(footprint.minorMetres, worldTexel);
    uint tapCount = (uint)clamp(ceil(anisotropicRatio - 1e-4), 1.0, DDVtAnisotropyLimit());
    float maximumSpan = 2.0 * ((float)DD_VT_BORDER - 1.0);
    float spanScale = min(1.0, maximumSpan / max(majorTexels, 1e-20));
    float2 spanWorld = footprint.majorVector * spanScale;

    // 显式限制完整长轴跨度为 14 texel（半跨度最多 7），给双线性留余量。
    // 常规 bias=0 的 AF4 最长约 4*sqrt(2) texel，不触发此限制。
    // 极端负 bias 或最高 mip 钳制时可能触发，届时会欠过滤；这不是完整
    // 邻页过滤，也不依赖 SamplePage 的 clamp。所有 tap 固定同一物理页。
    surface = (DDSurface)0;
    float3 firstNormal = float3(0, 1, 0);
    [loop] for (uint tap = 0u; tap < tapCount; ++tap)
    {
        float offset = ((float)tap + 0.5) / (float)tapCount - 0.5;
        DDSurface sample;
        // 页边框已按世界位置生成，即使 tap 越出整个世界边缘也有合法延拓。
        // 不在这里重新查页或重新计算 pageXZ，否则会混入另一页/另一实际 mip。
        if (!DDVtSamplePageUnnormalized(worldXZ + spanWorld * offset, pageXZ, page, sample)) return false;
        if (tap == 0u) firstNormal = sample.normal;
        surface.albedo += sample.albedo;
        surface.normal += sample.normal;
        surface.ao += sample.ao;
        surface.roughness += sample.roughness;
        surface.height += sample.height;
    }
    float inverseTapCount = rcp((float)tapCount);
    surface.albedo *= inverseTapCount;
    surface.ao *= inverseTapCount;
    surface.roughness *= inverseTapCount;
    surface.height *= inverseTapCount;
    // 先逐原始纹素解码 oct，再做页内双线性与长轴累积，最后才归一化。
    surface.normal = dot(surface.normal, surface.normal) > 1e-10 ? normalize(surface.normal) :
        (dot(firstNormal, firstNormal) > 1e-10 ? normalize(firstNormal) : float3(0, 1, 0));
    return true;
}
#endif

bool DDVtReadSurface(float2 worldXZ, uint desiredMip, out DDVtResidentPage page, out DDSurface surface)
{
    page = (DDVtResidentPage)0;
    surface = (DDSurface)0;
    if (_DDVtReady < 0.5 || !DDVtInsideWorld(worldXZ)) return false;

    DDVtTableAddress address = DDVtResolveAddress(worldXZ, desiredMip);
    uint entry = _DDVtTable[address.word];
    uint storedSlot = entry & DD_VT_SLOT_MASK;
    uint residentMip = (entry >> 11u) & 31u;
    if (storedSlot == 0u || storedSlot > DD_VT_SLOT_COUNT || residentMip > DD_VT_MAX_MIP) return false;
    page.slot = storedSlot - 1u;
    page.mip = residentMip;
    // 来源由全局 mip 决定，不能用 AVT 的 local mip 判断 RVT/SVT。
    page.source = residentMip >= DD_VT_FIRST_SVT_MIP ? 2u : 1u;
    uint2 pageXZ = DDVtPageCoordinate(worldXZ, residentMip);
    #if defined(DD_WORLD_VT_ANISOTROPIC)
    return DDVtSampleAnisotropicPage(worldXZ, pageXZ, page, surface);
    #else
    return DDVtSamplePage(worldXZ, pageXZ, page, surface);
    #endif
}

// 在同一GBuffer绘制中反馈请求。缓存和直接缺页回退在此汇合成DDSurface，
// 然后由统一编码器写四MRT；此入口不执行旧的地形专用光照。
#if defined(DD_WORLD_VT_MAIN_FEEDBACK)
[earlydepthstencil]
#endif
MaxTerrainGBufferOutputs MaxTerrainVirtualGBuffer(Varyings input)
{
    // 梯度必须先于cache hit/miss分支。直接回退的材质SampleGrad共用它们。
    DDRvtPositionDx = ddx(input.world);
    DDRvtPositionDy = ddy(input.world);
    if (input.diagnostic.z > 0.01) return MaxTerrainPackDiagnostic(float3(1, 0, 1));
    if (_MeshDebug != 0) return MaxTerrainPackDiagnostic(MaxTerrainGeometryDiagnostic(input));
    #if !defined(DD_SURFACE_ON)
        return MaxTerrainPackSurfaceOrDebug(MaxTerrainReadDirectSurface(input, DDRvtPositionDx, DDRvtPositionDy));
    #else
        uint desiredMip = DDVtDesiredMip(DDRvtPositionDx.xz, DDRvtPositionDy.xz);
        #if defined(DD_WORLD_VT_MAIN_FEEDBACK)
            if (_DDVtMode != 0) DDVtWriteMainFeedback(input.world.xz, desiredMip, input.position);
        #endif
        if (_DDSurfaceDebug >= 6 && _DDSurfaceDebug <= 11)
            return MaxTerrainPackDiagnostic(DDControlDebug(DDReadControls(input.world.xz)).rgb);

        bool forceDirect = _DDVtMode == 0;
        #if defined(DD_WORLD_VT_BC3)
            // 正式双BC3没有材质Height；F2继续查看直接源求值，不伪造缓存Height。
            forceDirect = forceDirect || (_DDVtMode == 1 && _DDSurfaceDebug == 2);
        #endif
        DDSurface surface;
        if (forceDirect)
            return MaxTerrainPackSurfaceOrDebug(MaxTerrainReadDirectSurface(input, DDRvtPositionDx, DDRvtPositionDy));

        DDVtResidentPage page;
        bool hit = DDVtReadSurface(input.world.xz, desiredMip, page, surface);
        if (_DDVtMode == 2)
        {
            if (!hit) return MaxTerrainPackDiagnostic(float3(1, 0, 0));
            if (page.mip > desiredMip) return MaxTerrainPackDiagnostic(float3(1, 1, 0));
            return MaxTerrainPackDiagnostic(page.source == 2u ? float3(0, 0, 1) : float3(0, 1, 0));
        }
        if (_DDVtMode == 3)
        {
            if (!hit) return MaxTerrainPackDiagnostic(float3(1, 0, 1));
            float error = saturate(((float)page.mip - (float)desiredMip) / 5.0);
            return MaxTerrainPackDiagnostic(float3(error, 1 - error, 0));
        }
        // 静态道路/贴花已在缓存内。只有缺页回退才求值源材质和静态覆盖。
        // 两种来源最终输出相同材质契约，不能把已光照颜色误写成Albedo。
        if (!hit) surface = MaxTerrainReadDirectSurface(input, DDRvtPositionDx, DDRvtPositionDy);
        return MaxTerrainPackSurfaceOrDebug(surface);
    #endif
}

#endif
