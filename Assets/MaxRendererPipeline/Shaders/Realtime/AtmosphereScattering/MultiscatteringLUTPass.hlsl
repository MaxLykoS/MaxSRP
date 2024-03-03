#ifndef MAX_MULTISCATTERING_PASS_INCLUDED
#define MAX_MULTISCATTERING_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float2 uv : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

v2f vert(appdata v)
{
    v2f o;
    o.vertex = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;
    return o;
}

float4 frag(v2f i) : SV_Target
{
	AtmosphereParameter param = GetAtmosphereParameter();

	float4 color = float4(0, 0, 0, 1);
	float2 uv = i.uv;

	float mu_s = uv.x * 2.0 - 1.0;
	float r = uv.y * param.AtmosphereHeight + param.PlanetRadius;

	float cos_theta = mu_s;
	float sin_theta = sqrt(1.0 - cos_theta * cos_theta);
	float3 lightDir = float3(sin_theta, cos_theta, 0);
	float3 p = float3(0, r, 0);

	color.rgb = IntegralMultiScattering(param, p, lightDir);
	//color.rg = uv;
	return color;
}

#endif