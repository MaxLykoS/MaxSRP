#ifndef MAX_PACKING_INCLUDED
#define MAX_PACKING_INCLUDED

float4 DecodeNormalFromTexture(float4 packNormal)
{
    float3 normal;
    normal.xy = packNormal.wy * 2 - 1;
    normal.z = sqrt(1 - normal.x * normal.x - normal.y * normal.y);
    return float4(normal, 0);
}

float3 SchmidtOrthogonalizationTW(float3 nW, float tW)
{
    return normalize(tW - dot(tW, nW) * nW);
}

#endif