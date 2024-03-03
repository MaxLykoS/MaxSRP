#ifndef MAX_TRANSMITTANCE_PASS_INCLUDED
#define MAX_TRANSMITTANCE_PASS_INCLUDED

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

fixed4 frag(v2f i) : SV_Target
{
    AtmosphereParameter param = GetAtmosphereParameter();

    float4 color = float4(0, 0, 0, 1);
	float2 uv = i.uv;

	float bottomRadius = param.PlanetRadius;
	float topRadius = param.PlanetRadius + param.AtmosphereHeight;

	// 计算当前 uv 对应的 cos_theta, height
	float cos_theta = 0.0;
	float r = 0.0;
	UvToTransmittanceLutParams(bottomRadius, topRadius, uv, cos_theta, r);

	float sin_theta = sqrt(1.0 - cos_theta * cos_theta);
	float3 viewDir = float3(sin_theta, cos_theta, 0);
	float3 eyePos = float3(0, r, 0);

	// 光线和大气层求交
	float dis = RayIntersectSphere(float3(0, 0, 0), param.PlanetRadius + param.AtmosphereHeight, eyePos, viewDir);
	float3 hitPoint = eyePos + viewDir * dis;

	// raymarch 计算 transmittance
	color.rgb = Transmittance(param, eyePos, hitPoint);

	return color;
}

#endif