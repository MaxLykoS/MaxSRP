#ifndef MAX_DECALLIT_PASS_INCLUDED
#define MAX_DECALLIT_PASS_INCLUDED

#include "../../ShaderLibrary/Realtime/Lighting.hlsl"
#include "../../ShaderLibrary/Realtime/Shadow.hlsl"
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/Packing.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _AlbedoMap_ST;
float _DecalScale;
CBUFFER_END

UNITY_DECLARE_TEX2D(_AlbedoMap);

struct a2v
{
	float4 pO : POSITION;
	float2 uv : TEXCOORD0;
	float3 nO : NORMAL;
};

struct v2f
{
	float2 uv : TEXCOORD0;
	float4 pH : SV_POSITION;
	float3 nW : TEXCOORD1;
	float3 pW : TEXCOORD2;
};

v2f DecalVertex(a2v v)
{
	v2f o;

	o.pH = float4(1, 1, 1, 1);
	return o;

	/*o.pH = UnityObjectToClipPos(v.pO);
	o.uv = v.uv;
	o.uv = o.uv * _AlbedoMap_ST.xy + _AlbedoMap_ST.zw;
	o.pW = mul(unity_ObjectToWorld, v.pO).xyz;
	o.nW = TransformObjectToWorldNormal(v.nO);

	return o;*/
}

float4 DecalFragment(v2f o) : SV_Target
{
	clip(-1);
	return float4(0, 0, 0, 1);
	/*float4 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, o.uv);

	clip(_DecalScale - albedo.a);

	o.nW = normalize(o.nW);

	float3 c = PBR_Shading(o.pW, o.nW, albedo.rgb, 0, 1);

	return float4(c, 1);*/
}

#endif