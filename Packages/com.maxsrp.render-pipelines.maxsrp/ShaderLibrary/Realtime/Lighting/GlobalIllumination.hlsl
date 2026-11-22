#ifndef GLOBAL_ILLUMINATION_INCLUDED
#define GLOBAL_ILLUMINATION_INCLUDED

#include "IBLValidation.hlsl"
#include "MaxDiffuseResponse.hlsl"

StructuredBuffer<float4> _SkyDiffuseSH;
samplerCUBE _IBLSpec;
UNITY_DECLARE_TEX2D(_BRDFLUT);

int _MaxMipCount;
#define MIN_REFLECTIVITY 0.04

float3 GetSpecularIBL(float3 reflectDir, float roughness, float NdotV, float3 kS)
{
	float3 prefilteredColor = texCUBElod(_IBLSpec, float4(reflectDir, roughness * max(0, _MaxMipCount - 1))).rgb;
	float2 envBRDF = UNITY_SAMPLE_TEX2D(_BRDFLUT, float2(NdotV, roughness)).rg;
	float3 indirectSpec = prefilteredColor * (kS * envBRDF.x + envBRDF.y);
	return FiniteIBLColor(indirectSpec);
}

float3 GetDiffuseIBL(float3 N, float3 albedo, float3 kD)
{
	// UE 5.8.4 CL 58902052, ReflectionEnvironmentShared.ush: GetSkySHDiffuse.
	N = normalize(N);
	float4 normal = float4(N, 1.0);
	float4 products = normal.xyzz * normal.yzzx;
	float3 linearTerm = float3(dot(_SkyDiffuseSH[0], normal), dot(_SkyDiffuseSH[1], normal), dot(_SkyDiffuseSH[2], normal));
	float3 quadraticTerm = float3(dot(_SkyDiffuseSH[3], products), dot(_SkyDiffuseSH[4], products), dot(_SkyDiffuseSH[5], products));
	float3 irradiance = max(0.0, linearTerm + quadraticTerm + _SkyDiffuseSH[6].xyz * (N.x * N.x - N.y * N.y));
	float3 diffuse = FiniteIBLColor(irradiance) * albedo;
	return FiniteIBLColor(kD * diffuse);
}

#endif
