#ifndef MAX_BISTRO_META_PASS_INCLUDED
#define MAX_BISTRO_META_PASS_INCLUDED

#include "BistroGBuffer.hlsl"
#include "../../../MaxRendererPipeline/ShaderLibrary/Realtime/MaxMetaPass.hlsl"

struct BistroMetaVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

BistroMetaVaryings BistroMetaVertex(BistroAttributes input)
{
    BistroMetaVaryings output;
    output.position = MaxMetaRasterPosition(input.position, input.lightmapUV, input.dynamicUV);
    output.uv = BistroUV(input.uv);
    return output;
}

float4 BistroMetaFragment(BistroMetaVaryings input) : SV_Target
{
    float4 albedo = UNITY_SAMPLE_TEX2D(_BaseColorMap, input.uv);
    BistroClipAlpha(albedo.a);
    float3 mask = UNITY_SAMPLE_TEX2D(_MaskMap, input.uv).rgb;
    return MaxMetaFragment(MaxMetaLightmappingAlbedo(albedo.rgb, mask.b, mask.g));
}

#endif
