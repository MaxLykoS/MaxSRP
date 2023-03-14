#ifndef GLOBAL_ILLUMINATION_INCLUDED
#define GLOBAL_ILLUMINATION_INCLUDED

float4 c0;
float4 c1;
float4 c2;
float4 c3;
float4 c4;
float4 c5;
float4 c6;
float4 c7;
float4 c8;

samplerCUBE _IBLSpec;
UNITY_DECLARE_TEX2D(_BRDFLUT);

#define MAX_MIP_COUNT 1.0
#define MIN_REFLECTIVITY 0.04

half3 BRDFIBLSpec(float2 scaleBias)
{
	float3 F0 = float3(MIN_REFLECTIVITY, MIN_REFLECTIVITY, MIN_REFLECTIVITY);
    return F0 * scaleBias.x + scaleBias.y;
}
float3 GetSpec(float3 reflectDir, float roughness, float NdotV)
{
	float3 prefilteredColor = texCUBElod(_IBLSpec, float4(reflectDir, roughness * MAX_MIP_COUNT)).rgb;
	float4 scaleBias = UNITY_SAMPLE_TEX2D(_BRDFLUT, float2(NdotV, roughness));
    half3 indirectSpec = BRDFIBLSpec(scaleBias.xy) * prefilteredColor;
	return indirectSpec;
}

float Y0(float3 v)
{
	return 0.2820947917f;
}
float Y1(float3 v)
{
	return 0.4886025119f * v.y;
}

float Y2(float3 v)
{
	return 0.4886025119f * v.z;
}
float Y3(float3 v)
{
	return 0.4886025119f * v.x;
}
float Y4(float3 v)
{
	return 1.0925484306f * v.x * v.y;
}
float Y5(float3 v)
{
	return 1.0925484306f * v.y * v.z;
}
float Y6(float3 v)
{
	return 0.3153915652f * (3.0f * v.z * v.z - 1.0f);
}
float Y7(float3 v)
{
	return 1.0925484306f * v.x * v.z;
}
float Y8(float3 v)
{
	return 0.5462742153f * (v.x * v.x - v.y * v.y);
}

float3 CubemapApprox(float3 N)
{
	float4 approx = c0 * Y0(N) + c1 * Y1(N) + c2 * Y2(N) + c3 * Y3(N) + c4 * Y4(N) + c5 * Y5(N) + c6 * Y6(N) + c7 * Y7(N) + c8 * Y8(N);
	return approx.rgb;
}

float3 PRTApprox(float4 sh_0, float4 sh_1, float sh_2)
{
	float r = (dot(sh_0, float4(c0.x, c1.x, c2.x, c3.x)) + dot(sh_1, float4(c4.x, c5.x, c6.x, c7.x)) - sh_2 * c8.x);
	float g = (dot(sh_0, float4(c0.y, c1.y, c2.y, c3.y)) + dot(sh_1, float4(c4.y, c5.y, c6.y, c7.y)) - sh_2 * c8.z);
	float b = (dot(sh_0, float4(c0.z, c1.z, c2.z, c3.z)) + dot(sh_1, float4(c4.z, c5.z, c6.z, c7.z)) - sh_2 * c8.z);

	return float3(r,g,b) * 0.4;
}

#endif