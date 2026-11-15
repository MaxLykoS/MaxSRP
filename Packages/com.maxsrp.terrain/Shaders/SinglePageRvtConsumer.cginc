#ifndef DIRECT_DELTA_SINGLE_PAGE_CONSUMER_INCLUDED
#define DIRECT_DELTA_SINGLE_PAGE_CONSUMER_INCLUDED

Texture2D<float4> _DDRvtAlbedoAo;
Texture2D<float4> _DDRvtNormalRoughnessHeight;

bool DDRvtReadCachedSurface(float2 worldXZ, out DDSurface surface)
{
    surface = (DDSurface)0;
    if (_DDRvtInfo.w < .5 || _DDRvtInfo.x < 1 || _DDRvtInfo.z < 1 ||
        any(_DDRvtRect.zw <= 0) || any(worldXZ < _DDRvtRect.xy) ||
        any(worldXZ > _DDRvtRect.xy + _DDRvtRect.zw)) return false;

    float2 pixel = (worldXZ - _DDRvtRect.xy) / _DDRvtRect.zw * _DDRvtInfo.x + _DDRvtInfo.y - .5;
    int2 low = (int2)floor(pixel);
    float2 weight = frac(pixel);
    int last = (int)round(_DDRvtInfo.z) - 1;
    int2 p00 = clamp(low, 0, last);
    int2 p10 = clamp(low + int2(1,0), 0, last);
    int2 p01 = clamp(low + int2(0,1), 0, last);
    int2 p11 = clamp(low + 1, 0, last);
    float4 a00 = _DDRvtAlbedoAo.Load(int3(p00,0));
    float4 a10 = _DDRvtAlbedoAo.Load(int3(p10,0));
    float4 a01 = _DDRvtAlbedoAo.Load(int3(p01,0));
    float4 a11 = _DDRvtAlbedoAo.Load(int3(p11,0));
    float4 n00 = _DDRvtNormalRoughnessHeight.Load(int3(p00,0));
    float4 n10 = _DDRvtNormalRoughnessHeight.Load(int3(p10,0));
    float4 n01 = _DDRvtNormalRoughnessHeight.Load(int3(p01,0));
    float4 n11 = _DDRvtNormalRoughnessHeight.Load(int3(p11,0));
    if (min(min(n00.a,n10.a),min(n01.a,n11.a)) < 0) return false;
    float4 albedoAo = lerp(lerp(a00,a10,weight.x),lerp(a01,a11,weight.x),weight.y);
    float2 roughnessHeight = lerp(lerp(n00.ba,n10.ba,weight.x),lerp(n01.ba,n11.ba,weight.x),weight.y);
    // Decode before interpolation: hardware bilinear filtering across the
    // octahedral wrap would corrupt normals on steep / downward-facing detail.
    float3 normal = lerp(lerp(DDRvtDecodeNormal(n00.rg),DDRvtDecodeNormal(n10.rg),weight.x),
        lerp(DDRvtDecodeNormal(n01.rg),DDRvtDecodeNormal(n11.rg),weight.x),weight.y);
    surface.albedo = albedoAo.rgb;
    surface.ao = albedoAo.a;
    surface.normal = dot(normal,normal) > 1e-10 ? normalize(normal) : DDRvtDecodeNormal(n00.rg);
    surface.roughness = roughnessHeight.x;
    surface.height = roughnessHeight.y;
    return true;
}

float4 DDRvtFrag(Varyings input) : SV_Target
{
    DDRvtPositionDx = ddx(input.world);
    DDRvtPositionDy = ddy(input.world);
    // These uniform diagnostic routes retain the original geometry/control
    // interpretation, which is deliberately not part of the material cache.
    if (_DDRvtMode == 0 || _MeshDebug != 0) return Frag(input);
    #if !defined(DD_SURFACE_ON)
        return Frag(input);
    #else
        if (input.diagnostic.z > .01) return float4(1,0,1,1);
        if (_DDSurfaceDebug >= 6 && _DDSurfaceDebug <= 11) return Frag(input);
        DDSurface surface;
        bool hit = DDRvtReadCachedSurface(input.world.xz, surface);
        if (_DDRvtMode == 2) return hit ? float4(0,1,0,1) : float4(1,0,0,1);
        // Cache lookup precedes all source-normal and material evaluation.
        [branch] if (!hit) return Frag(input);
        if (_DDSurfaceDebug == 1) return float4(surface.albedo,1);
        if (_DDSurfaceDebug == 2) return float4(surface.height.xxx,1);
        if (_DDSurfaceDebug == 3) return float4(surface.normal * .5 + .5,1);
        if (_DDSurfaceDebug == 4) return float4(surface.roughness.xxx,1);
        if (_DDSurfaceDebug == 5) return float4(surface.ao.xxx,1);
        return float4(DDLightSurface(surface,input.world),1);
    #endif
}

#endif
