#ifndef MAX_PACKING_INCLUDED
#define MAX_PACKING_INCLUDED

// Match Unity's imported RGB, AG/DXT5nm, BC5 and ASTC normal encodings.
float3 DecodeNormalFromTexture(float4 packedNormal)
{
#if defined(UNITY_NO_DXT5nm)
    return packedNormal.xyz * 2.0 - 1.0;
#else
#if defined(UNITY_ASTC_NORMALMAP_ENCODING)
    float2 xy = packedNormal.wy * 2.0 - 1.0;
#else
    packedNormal.x *= packedNormal.w;
    float2 xy = packedNormal.xy * 2.0 - 1.0;
#endif
    return float3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
#endif
}

#endif