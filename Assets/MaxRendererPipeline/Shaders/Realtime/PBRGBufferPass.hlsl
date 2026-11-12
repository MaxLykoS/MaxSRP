#ifndef MAX_PBRGBUFFER_PASS_INCLUDED
#define MAX_PBRGBUFFER_PASS_INCLUDED

#include "../../ShaderLibrary/Realtime/Lighting.hlsl"
#include "../../ShaderLibrary/Realtime/Shadow.hlsl"
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/Packing.hlsl"
#include "../../ShaderLibrary/Realtime/MaxBakedLighting.hlsl"

struct a2v
{
    float4 pO : POSITION;
    float2 uv : TEXCOORD0;
    float2 lightmapUV : TEXCOORD1;
    float2 dynamicUV : TEXCOORD2;
    float3 nO : NORMAL;
    float4 tO : TANGENT;
};

struct v2f
{
    float4 pH : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 nW : TEXCOORD1;
    float4 tW : TEXCOORD2;
    float2 lightmapUV : TEXCOORD3;
    float3 positionWS : TEXCOORD4;
};

CBUFFER_START(UnityPerMaterial)
float4 _AlbedoMap_ST;
float _MaxSkyVisibilitySlice;
CBUFFER_END

UNITY_DECLARE_TEX2D(_AlbedoMap);
UNITY_DECLARE_TEX2D(_MetalnessMap);
UNITY_DECLARE_TEX2D(_RoughnessMap);
UNITY_DECLARE_TEX2D(_NormalMap);

v2f PBRGBufferVertex(a2v v)
{
    v2f o;
    o.pH = UnityObjectToClipPos(v.pO);
    o.uv = v.uv;
    o.uv = o.uv * _AlbedoMap_ST.xy + _AlbedoMap_ST.zw;

    o.nW = TransformObjectToWorldNormal(v.nO);
    o.tW = float4(normalize(TransformObjectToWorldDir(v.tO.xyz)),
        v.tO.w * unity_WorldTransformParams.w);
    o.lightmapUV = MaxLightmapUV(v.lightmapUV);
    o.positionWS = TransformObjectToWorld(v.pO.xyz);
    return o;
}

void PBRGBufferFragment(
    v2f o,
    out float4 GBuffer0 : SV_Target0,
    out float4 GBuffer1 : SV_Target1,
    out float4 GBuffer2 : SV_Target2,
    out float4 GBuffer3 : SV_Target3
)
{
    float4 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, o.uv);
    float metalness = UNITY_SAMPLE_TEX2D(_MetalnessMap, o.uv).r;
    float roughness = UNITY_SAMPLE_TEX2D(_RoughnessMap, o.uv).r;

    o.nW = normalize(o.nW);
    // Re-orthogonalize the interpolated tangent and preserve UV/transform handedness.
    float3 tW = normalize(o.tW.xyz - o.nW * dot(o.nW, o.tW.xyz));
    float3 bW = normalize(cross(o.nW, tW)) * o.tW.w;
    float3x3 TBN = float3x3(tW, bW, o.nW);
    float4 packedNormal = UNITY_SAMPLE_TEX2D(_NormalMap, o.uv);
    float3 bump = DecodeNormalFromTexture(packedNormal);
    bump = normalize(mul(bump, TBN));

    GBuffer0 = albedo;
    // Alpha declares this material's temporal contract. Unity can omit object
    // MotionVectors draws for Camera-only renderers; camera reprojection is valid.
    GBuffer1 = float4(bump * 0.5 + 0.5, 1);
    GBuffer2 = float4(MaxSampleSkyVisibility(o.lightmapUV, _MaxSkyVisibilitySlice),
        MaxHasBakedLightmap(), roughness, metalness);
    float3 bakedDiffuse = MaxSampleBakedDiffuse(o.lightmapUV, o.positionWS,
        bump, albedo.rgb, metalness, roughness, o.nW);
    GBuffer3 = float4(MaxDiagnosticGBufferLighting(bakedDiffuse, float3(0.0, 0.0, 0.0)), 0.0);
}
#endif
