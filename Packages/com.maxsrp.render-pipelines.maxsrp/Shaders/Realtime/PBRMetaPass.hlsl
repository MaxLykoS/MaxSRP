#ifndef MAX_PBR_META_PASS_INCLUDED
#define MAX_PBR_META_PASS_INCLUDED

#include "PBRGBufferPass.hlsl"
#include "../../ShaderLibrary/Realtime/MaxMetaPass.hlsl"

struct PBRMetaVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

PBRMetaVaryings PBRMetaVertex(a2v input)
{
    PBRMetaVaryings output;
    output.position = MaxMetaRasterPosition(input.pO, input.lightmapUV, input.dynamicUV);
    output.uv = TRANSFORM_TEX(input.uv, _AlbedoMap);
    return output;
}

float4 PBRMetaFragment(PBRMetaVaryings input) : SV_Target
{
    float3 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, input.uv).rgb;
    float metalness = UNITY_SAMPLE_TEX2D(_MetalnessMap, input.uv).r;
    float roughness = UNITY_SAMPLE_TEX2D(_RoughnessMap, input.uv).r;
    return MaxMetaFragment(MaxMetaLightmappingAlbedo(albedo, metalness, roughness));
}

#endif
