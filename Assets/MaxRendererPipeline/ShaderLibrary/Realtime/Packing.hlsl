#ifndef MAX_PACKING_INCLUDED
#define MAX_PACKING_INCLUDED

float4 DecodeNormalFromTexture(float4 packedNormal)
{
    float3 normal;
    normal.xy = packedNormal.wy * 2 - 1;
    normal.z = sqrt(max(0.0, 1 - normal.x * normal.x - normal.y * normal.y));
    return float4(normal, 0);
}

float3 SchmidtOrthogonalizationTW(float3 nW, float tW)
{
    return normalize(tW - dot(tW, nW) * nW);
}

#endif