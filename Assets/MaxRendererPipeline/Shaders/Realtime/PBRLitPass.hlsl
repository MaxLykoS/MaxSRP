#ifndef MAX_PBRLIT_PASS_INCLUDED
#define MAX_PBRLIT_PASS_INCLUDED

#include "../../ShaderLibrary/Realtime/Lighting.hlsl"
#include "../../ShaderLibrary/Realtime/Shadow.hlsl"
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/Packing.hlsl"

struct a2v
{
	float4 pO : POSITION;
	float2 uv : TEXCOORD0;
	float3 nO : NORMAL;
	float3 tO : TANGENT;
};

struct v2f
{
	float2 uv : TEXCOORD0;
	float4 pH : SV_POSITION;
	float3 nW : TEXCOORD1;
	float3 pW : TEXCOORD2;
	float3 tW : TEXCOORD3;
};

CBUFFER_START(UnityPerMaterial)
float4 _AlbedoMap_ST;  
float _Transparency;
CBUFFER_END

UNITY_DECLARE_TEX2D(_AlbedoMap);
UNITY_DECLARE_TEX2D(_MetalnessMap);
UNITY_DECLARE_TEX2D(_RoughnessMap);
UNITY_DECLARE_TEX2D(_NormalMap);

v2f PBRVertex(a2v v)
{
	v2f o;
	o.pH = UnityObjectToClipPos(v.pO);
	o.uv = v.uv;
	o.uv = o.uv * _AlbedoMap_ST.xy + _AlbedoMap_ST.zw;
	o.pW = mul(unity_ObjectToWorld, v.pO).xyz;

	o.nW = TransformObjectToWorldNormal(v.nO);
	o.tW = normalize(TransformObjectToWorld(v.tO.xyz));

	return o;
}

float4 PBRFragment(v2f o) : SV_Target
{
	float4 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, o.uv);
	float metalness = UNITY_SAMPLE_TEX2D(_MetalnessMap, o.uv).r;
	float roughness = UNITY_SAMPLE_TEX2D(_RoughnessMap, o.uv).r;

	o.nW = normalize(o.nW);
	o.tW = normalize(SchmidtOrthogonalizationTW(o.nW, o.tW));
	float3 bW = normalize(cross(o.nW, o.tW));
	float3x3 TBN = float3x3(o.tW, bW, o.nW);
	float4 packedNormal = UNITY_SAMPLE_TEX2D(_NormalMap, o.uv);
	float3 bump = normalize(DecodeNormalFromTexture(packedNormal));
	bump = normalize(mul(bump, TBN));

	float3 c = PBR_Shading(o.pW, bump, albedo.rgb, metalness, roughness);
	return float4(c, albedo.a);
}

float4 PBRFragmentTransparent(v2f o) : SV_Target
{
	float4 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, o.uv);
	float metalness = UNITY_SAMPLE_TEX2D(_MetalnessMap, o.uv).r;
	float roughness = UNITY_SAMPLE_TEX2D(_RoughnessMap, o.uv).r;

	o.nW = normalize(o.nW);
	o.tW = SchmidtOrthogonalizationTW(o.nW, o.tW);
	float3 bW = normalize(cross(o.nW, o.tW));
	float3x3 TBN = float3x3(o.tW, bW, o.nW);
	float4 packedNormal = UNITY_SAMPLE_TEX2D(_NormalMap, o.uv);
	float3 bump = DecodeNormalFromTexture(packedNormal);
	bump = normalize(mul(bump, TBN));

	float3 c = PBR_Shading(o.pW, bump, albedo.rgb, metalness, roughness);
	return float4(c, _Transparency);
}
#endif