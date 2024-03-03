#ifndef MAX_SKYVIEW_PASS_INCLUDED
#define MAX_SKYVIEW_PASS_INCLUDED


#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
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
	float3 viewDir = UVToViewDir(uv);

	float3 lightDir = _DirectionalLightDirection;

	float h = _WorldSpaceCameraPos.y - param.SeaLevel + param.PlanetRadius;
	float3 eyePos = float3(0, h, 0);

	color.rgb = GetSkyView(
		param, eyePos, viewDir, lightDir, -1.0f);
	return color;
}

#endif