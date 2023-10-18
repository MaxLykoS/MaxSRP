#ifndef MAX_SCREENSPACESHADOWMAP_PASS_INCLUDED
#define MAX_SCREENSPACESHADOWMAP_PASS_INCLUDED

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
UNITY_DECLARE_TEX2D(_GBuffer1);

v2f vert(a2v v)
{
	v2f o;
	o.pH = UnityObjectToClipPos(v.pO);
	o.uv = v.uv;

	return o;
}

float4 frag(v2f o) : SV_Target
{
	float4 GBuffer1 = UNITY_SAMPLE_TEX2D(_GBuffer1, o.uv);
	float GDepth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_GDepth, o.uv));

	// bg1
	float3 normal = normalize(GBuffer1.rgb * 2 - 1);

	// depth buffer
	float depth = GDepth;

	// reprojection 计算世界坐标
	float4 pNDC = float4(o.uv * 2 - 1, depth, 1);
	float4 pW = mul(unity_MatrixInvVP, pNDC);
	pW = pW / pW.w;

	return GetMainLightShadowVisibility(pW, normal, _DirectionalLightDirection.xyz);
}

#endif