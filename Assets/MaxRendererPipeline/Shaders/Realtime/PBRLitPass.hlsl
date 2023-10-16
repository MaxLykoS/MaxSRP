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
};

struct v2f
{
	float2 uv : TEXCOORD0;
	float4 pH : SV_POSITION;
};

UNITY_DECLARE_TEX2D(_GDepth);
UNITY_DECLARE_TEX2D(_GBuffer0);
UNITY_DECLARE_TEX2D(_GBuffer1);
UNITY_DECLARE_TEX2D(_GBuffer2);
UNITY_DECLARE_TEX2D(_GBuffer3);

v2f PBRVertex(a2v v)
{
	v2f o;
	o.pH = UnityObjectToClipPos(v.pO);
	o.uv = v.uv;

	return o;
}

float4 PBRFragment(v2f o, out float depthOut : SV_Depth) : SV_Target
{
	float4 GBuffer0 = UNITY_SAMPLE_TEX2D(_GBuffer0, o.uv);
	float4 GBuffer1 = UNITY_SAMPLE_TEX2D(_GBuffer1, o.uv);
	float4 GBuffer2 = UNITY_SAMPLE_TEX2D(_GBuffer2, o.uv);
	float4 GBuffer3 = UNITY_SAMPLE_TEX2D(_GBuffer3, o.uv);
	float GDepth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_GDepth, o.uv));

	// gb0
	float4 albedo = GBuffer0;
	
	// bg1
	float3 normal = normalize(GBuffer1.rgb * 2 - 1);

	// gb2
	float2 motionVec = GBuffer2.rg;
	float roughness = GBuffer2.b; roughness = max(roughness, 0.05);
	float metalness = GBuffer2.a;

	// gb3
	float3 emission = GBuffer3.rgb;
	float occlusion = GBuffer3.a;

	// depth buffer
	float depth = GDepth;
	depthOut = depth;

	// reprojection 计算世界坐标
	float4 pNDC = float4(o.uv * 2 - 1, depth, 1);
	float4 pW = mul(unity_MatrixInvVP, pNDC);
	pW = pW / pW.w;

	float3 c = PBR_Shading(pW, normal, albedo.rgb, metalness, roughness, o.uv);

	// ldr to hdr
	//c = c / (c + float3(1.0, 1.0, 1.0));
	//c = pow(c, float3(1.0 / 2.2, 1.0 / 2.2, 1.0 / 2.2));

	return float4(c, albedo.a);
}

/*float4 PBRFragmentTransparent(v2f o) : SV_Target
{
	float4 albedo = UNITY_SAMPLE_TEX2D(_AlbedoMap, o.uv);
	float metalness = UNITY_SAMPLE_TEX2D(_MetalnessMap, o.uv).r;
	float roughness = UNITY_SAMPLE_TEX2D(_RoughnessMap, o.uv).r;

	o.nW = normalize(o.nW);
	o.tW = normalize(o.tW);
	float3 bW = normalize(cross(o.nW, o.tW));
	float3x3 TBN = float3x3(o.tW, bW, o.nW);
	float4 packedNormal = UNITY_SAMPLE_TEX2D(_NormalMap, o.uv);
	float3 bump = DecodeNormalFromTexture(packedNormal);
	bump = normalize(mul(bump, TBN));

	float3 c = PBR_Shading(o.pW, bump, albedo.rgb, metalness, roughness);
	return float4(1.0,1.0,1.0, _Transparency);
}*/
#endif