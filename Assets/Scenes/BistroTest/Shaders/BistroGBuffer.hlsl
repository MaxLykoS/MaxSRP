#ifndef MAX_BISTRO_GBUFFER_INCLUDED
#define MAX_BISTRO_GBUFFER_INCLUDED

#include "../../../MaxRendererPipeline/ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../MaxRendererPipeline/ShaderLibrary/Realtime/Packing.hlsl"
#include "../../../MaxRendererPipeline/Shaders/Realtime/MotionVectorsPass.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _BaseColorMap_ST;
float _EmissionNits;
float _Cutoff;
float _Cull;
CBUFFER_END

UNITY_DECLARE_TEX2D(_BaseColorMap);
UNITY_DECLARE_TEX2D(_MaskMap);
UNITY_DECLARE_TEX2D(_NormalMap);
UNITY_DECLARE_TEX2D(_EmissionMap);
float4x4 _ShadowCasterVP;

float2 BistroUV(float2 uv)
{
    return uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
}

void BistroClipAlpha(float alpha)
{
    // An explicit zero cutoff keeps opaque assets opaque, including alpha = 0.
    if (_Cutoff > 0.0)
        clip(alpha - _Cutoff);
}

struct BistroAttributes
{
    float4 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
};

struct BistroVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
};

BistroVaryings BistroGBufferVertex(BistroAttributes input)
{
    BistroVaryings output;
    output.position = UnityObjectToClipPos(input.position.xyz);
    output.uv = BistroUV(input.uv);
    output.normal = TransformObjectToWorldNormal(input.normal);
    output.tangent = float4(normalize(TransformObjectToWorldDir(input.tangent.xyz)),
        input.tangent.w * unity_WorldTransformParams.w);
    return output;
}

void BistroGBufferFragment(BistroVaryings input, bool frontFace : SV_IsFrontFace,
    out float4 GBuffer0 : SV_Target0,
    out float4 GBuffer1 : SV_Target1,
    out float4 GBuffer2 : SV_Target2,
    out float4 GBuffer3 : SV_Target3)
{
    float4 albedo = UNITY_SAMPLE_TEX2D(_BaseColorMap, input.uv);
    BistroClipAlpha(albedo.a);
    float3 mask = UNITY_SAMPLE_TEX2D(_MaskMap, input.uv).rgb;
    float3 normal = normalize(input.normal);
    float3 tangent = normalize(input.tangent.xyz - normal * dot(normal, input.tangent.xyz));
    float3 bitangent = normalize(cross(normal, tangent)) * input.tangent.w;
    float3 tangentNormal = DecodeNormalFromTexture(UNITY_SAMPLE_TEX2D(_NormalMap, input.uv));
    float3 mappedNormal = normalize(mul(tangentNormal, float3x3(tangent, bitangent, normal)));
    // Thin foliage uses the opposite outward normal on the back of its sheet.
    if (!frontFace)
        mappedNormal = -mappedNormal;

    GBuffer0 = albedo;
    // Same temporal contract as the production PBR material, including camera-only motion.
    GBuffer1 = float4(mappedNormal * 0.5 + 0.5, 1.0);
    GBuffer2 = float4(0.0, 0.0, saturate(mask.g), saturate(mask.b));
    // Scene HDR is luminance / LightPhotometry.HDRScale, before the shared exposure.
    float3 emission = UNITY_SAMPLE_TEX2D(_EmissionMap, input.uv).rgb * (max(_EmissionNits, 0.0) / 100000.0);
    GBuffer3 = float4(emission, saturate(mask.r));
}

struct BistroShadowAttributes
{
    float4 position : POSITION;
    float2 uv : TEXCOORD0;
};

struct BistroShadowVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

BistroShadowVaryings BistroShadowVertex(BistroShadowAttributes input)
{
    BistroShadowVaryings output;
    output.position = mul(_ShadowCasterVP, mul(unity_ObjectToWorld, input.position));
    output.uv = BistroUV(input.uv);
    return output;
}

float4 BistroShadowFragment(BistroShadowVaryings input) : SV_Target
{
    BistroClipAlpha(UNITY_SAMPLE_TEX2D(_BaseColorMap, input.uv).a);
    return 0.0;
}

struct BistroMotionAttributes
{
    float4 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 previousPosition : TEXCOORD4;
};

struct BistroMotionVaryings
{
    float4 position : SV_POSITION;
    float4 current : TEXCOORD0;
    float4 previous : TEXCOORD1;
    float previousDepth : TEXCOORD2;
    float2 uv : TEXCOORD3;
};

BistroMotionVaryings BistroMotionVertex(BistroMotionAttributes input)
{
    MotionAttributes sharedInput;
    sharedInput.position = input.position;
    sharedInput.previousPosition = input.previousPosition;
    MotionVaryings sharedOutput = MaxMotionVertex(sharedInput);
    BistroMotionVaryings output;
    output.position = sharedOutput.position;
    output.current = sharedOutput.current;
    output.previous = sharedOutput.previous;
    output.previousDepth = sharedOutput.previousDepth;
    output.uv = BistroUV(input.uv);
    return output;
}

float4 BistroMotionFragment(BistroMotionVaryings input) : SV_Target
{
    BistroClipAlpha(UNITY_SAMPLE_TEX2D(_BaseColorMap, input.uv).a);
    MotionVaryings sharedInput;
    sharedInput.position = input.position;
    sharedInput.current = input.current;
    sharedInput.previous = input.previous;
    sharedInput.previousDepth = input.previousDepth;
    return MaxMotionFragment(sharedInput);
}

#endif
