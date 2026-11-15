#ifndef DIRECT_DELTA_SINGLE_PAGE_SOURCE_INCLUDED
#define DIRECT_DELTA_SINGLE_PAGE_SOURCE_INCLUDED

Texture2D<float> _DDRvtHeight;
SamplerState sampler_DDRvtHeight; // Bilinear Clamp, normalized R16.
float4 _DDRvtHeightInfo; // Source origin X/Z, side, height scale. Fixed 1m lattice.
Texture2D<float> _DDRvtControls;
float4 _DDRvtControlInfo; // Source origin X/Z, side, fixed spacing=1m.

// 世界 VT 用固定 1m 源格点重建同一连续高度面，避免 R16 硬件插值精度
// 在毫米级输出纹素间造成台阶。偏导直接来自这个双线性面，不对量化结果再求 ddx。
float4 _DDRvtTexelGrid; // global interior texel origin X/Z, world metres per output texel X/Z.
float DDRvtContinuousHeight(float2 worldXZ, out float2 slope)
{
    float worldSize = max(_DDControlInfo.x, 1);
    float2 clamped = clamp(worldXZ, 0, worldSize);
    float2 local = clamped - _DDRvtHeightInfo.xy;
    int2 cell = clamp((int2)floor(local), int2(0,0), (int)_DDRvtHeightInfo.z - 2);
    float2 fraction = saturate(local - cell);
    float h00 = _DDRvtHeight.Load(int3(cell, 0)) * _DDRvtHeightInfo.w;
    float h10 = _DDRvtHeight.Load(int3(cell + int2(1,0), 0)) * _DDRvtHeightInfo.w;
    float h01 = _DDRvtHeight.Load(int3(cell + int2(0,1), 0)) * _DDRvtHeightInfo.w;
    float h11 = _DDRvtHeight.Load(int3(cell + int2(1,1), 0)) * _DDRvtHeightInfo.w;
    slope = float2(lerp(h10 - h00, h11 - h01, fraction.y),
                   lerp(h01 - h00, h11 - h10, fraction.x));
    // 地图外延拓高度为常数；越界轴不应该继续带入坡度。
    slope *= float2(worldXZ.x >= 0 && worldXZ.x <= worldSize,
                    worldXZ.y >= 0 && worldXZ.y <= worldSize);
    return lerp(lerp(h00, h10, fraction.x), lerp(h01, h11, fraction.x), fraction.y);
}

float DDRvtSourceHeight(float2 worldXZ)
{
    #if defined(DD_WORLD_VT_RGBA8)
    float2 unusedSlope;
    return DDRvtContinuousHeight(worldXZ, unusedSlope);
    #else
    // 历史固定单页 Half 对照保留原采样方式。
    worldXZ = clamp(worldXZ, 0, max(_DDControlInfo.x, 1));
    float2 uv = (worldXZ - _DDRvtHeightInfo.xy + .5) / _DDRvtHeightInfo.z;
    return _DDRvtHeight.SampleLevel(sampler_DDRvtHeight, uv, 0) * _DDRvtHeightInfo.w;
    #endif
}

float3 DDRvtSourceNormal(float2 worldXZ)
{
    // 从同一份 1m 高度快照读取四邻居，坡度是高差除以世界距离。
    // 地图边缘缩短采样区间，得到单边差分。法线方向与 CDLOD morph 无关。
    float worldSize = max(_DDControlInfo.x, 1);
    worldXZ = clamp(worldXZ, 0, worldSize);
    float2 low = max(worldXZ - 1, 0);
    float2 high = min(worldXZ + 1, worldSize);
    float left = DDRvtSourceHeight(float2(low.x, worldXZ.y));
    float right = DDRvtSourceHeight(float2(high.x, worldXZ.y));
    float bottom = DDRvtSourceHeight(float2(worldXZ.x, low.y));
    float top = DDRvtSourceHeight(float2(worldXZ.x, high.y));
    float2 slope = float2(right - left, top - bottom) / max(high - low, 1e-5);
    return normalize(float3(-slope.x, 1, -slope.y));
}

uint DDRvtLoadControl(int2 localTexel)
{
    return (uint)round(saturate(_DDRvtControls.Load(int3(localTexel, 0))) * 65535.0);
}

DDControls DDRvtReadControls(float2 worldXZ)
{
    DDControls controls = (DDControls)0;
    controls.level = 0;
    controls.count = 1;
    controls.opacity = float3(1,0,0);
    float spacing = max(_DDRvtControlInfo.w, .001);
    float worldSize = max(_DDControlInfo.x, 1);
    float2 grid = clamp(worldXZ, 0, worldSize) / spacing;
    int last = max((int)ceil(worldSize / spacing), 1);
    int2 cell = (int2)min(floor(grid), (float)(last - 1));
    float2 fraction = grid - cell;
    int2 texel0, texel1, texel2;
    if (fraction.x + fraction.y <= 1)
    {
        texel0 = cell; texel1 = cell + int2(1,0); texel2 = cell + int2(0,1);
        controls.bary = float3(1 - fraction.x - fraction.y, fraction.x, fraction.y);
    }
    else
    {
        texel0 = cell + 1; texel1 = cell + int2(0,1); texel2 = cell + int2(1,0);
        controls.bary = float3(fraction.x + fraction.y - 1, 1 - fraction.x, 1 - fraction.y);
    }
    int2 origin = (int2)round(_DDRvtControlInfo.xy / spacing);
    texel0 -= origin; texel1 -= origin; texel2 -= origin;
    int side = (int)round(_DDRvtControlInfo.z);
    if (side < 2 || any(texel0 < 0) || any(texel1 < 0) || any(texel2 < 0) ||
        any(texel0 >= side) || any(texel1 >= side) || any(texel2 >= side))
    {
        controls.invalid = 1;
        return controls;
    }
    // 找到世界点所在的控制三角形后，整数读取三个顶点的 R16 控制字。
    // 三角形重心权重只插值解码后的有序覆盖率。
    DDDecodeControl(DDRvtLoadControl(texel0), controls.bottom.x, controls.top.x, controls.coverage.x);
    DDDecodeControl(DDRvtLoadControl(texel1), controls.bottom.y, controls.top.y, controls.coverage.y);
    DDDecodeControl(DDRvtLoadControl(texel2), controls.bottom.z, controls.top.z, controls.coverage.z);
    controls.ids = DDExtractMaterialIds(controls.bottom, controls.top, controls.count, controls.invalid);
    controls.opacity = DDInterpolateOrderedOpacity(controls.bottom, controls.top,
        controls.coverage, controls.bary, controls.ids, controls.count);
    return controls;
}

float2 DDRvtPageWorldXZ(float2 physicalPixelCenter)
{
    // border 同样按世界坐标生成。相邻页在重叠区采到同一批世界纹素，
    // 因此 consumer 的四点过滤可以跨进 border。
    // SV_POSITION 已位于物理纹素中心，这里直接保留半纹素偏移。
    #if defined(DD_WORLD_VT_RGBA8)
    return (_DDRvtTexelGrid.xy + physicalPixelCenter - _DDRvtInfo.y) * _DDRvtTexelGrid.zw;
    #else
    return _DDRvtRect.xy + (physicalPixelCenter - _DDRvtInfo.y) / _DDRvtInfo.x * _DDRvtRect.zw;
    #endif
}

bool DDRvtHeightFootprintValid(float2 worldXZ)
{
    float worldSize = max(_DDControlInfo.x, 1);
    float2 low = floor(clamp(worldXZ - 1, 0, worldSize));
    float2 high = ceil(clamp(worldXZ + 1, 0, worldSize));
    return _DDRvtHeightInfo.z >= 2 && all(low >= _DDRvtHeightInfo.xy) &&
        all(high <= _DDRvtHeightInfo.xy + _DDRvtHeightInfo.z - 1);
}

#endif
