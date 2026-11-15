#ifndef DIRECT_DELTA_SINGLE_PAGE_COMMON_INCLUDED
#define DIRECT_DELTA_SINGLE_PAGE_COMMON_INCLUDED

// Single-page prototype ABI. Rect describes the interior; Info describes
// interior pixels, border pixels, physical side and ready flag respectively.
float4 _DDRvtRect;
float4 _DDRvtInfo;
int _DDRvtMode;

float2 DDRvtSignNotZero(float2 value)
{
    return float2(value.x < 0 ? -1 : 1, value.y < 0 ? -1 : 1);
}

// Full-sphere, Y-up octahedral storage. Cliff normal maps can point below
// the horizon, so a positive-Y hemisphere reconstruction would lose data.
float2 DDRvtEncodeNormal(float3 normal)
{
    normal /= max(dot(abs(normal), float3(1,1,1)), 1e-8);
    float2 encoded = normal.xz;
    if (normal.y < 0)
        encoded = (1 - abs(encoded.yx)) * DDRvtSignNotZero(encoded);
    return encoded * .5 + .5;
}

float3 DDRvtDecodeNormal(float2 encoded)
{
    float2 value = encoded * 2 - 1;
    float3 normal = float3(value.x, 1 - abs(value.x) - abs(value.y), value.y);
    if (normal.y < 0)
        normal.xz = (1 - abs(normal.zx)) * DDRvtSignNotZero(normal.xz);
    return normalize(normal);
}

#endif
