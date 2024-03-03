#ifndef MAX_PROCEDURALSKYBOX_PASS_INCLUDED
#define MAX_PROCEDURALSKYBOX_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"

struct appdata
{
    float4 posO : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float2 uv : TEXCOORD0;
    float3 posW : TEXCOORD1;
    float4 posH : SV_POSITION;
};

float3 GetSunDisk(in AtmosphereParameter param, float3 eyePos, float3 viewDir, float3 lightDir)
{
    // 计算入射光照
    float cosine_theta = dot(viewDir, -lightDir);
    float theta = acos(cosine_theta) * (180.0 / PI);
    float3 sunLuminance = param.SunLightColor * param.SunLightIntensity;

    // 判断光线是否被星球阻挡
    float disToPlanet = RayIntersectSphere(float3(0, 0, 0), param.PlanetRadius, eyePos, viewDir);
    if (disToPlanet >= 0) return float3(0, 0, 0);

    // 和大气层求交
    float disToAtmosphere = RayIntersectSphere(float3(0, 0, 0), param.PlanetRadius + param.AtmosphereHeight, eyePos, viewDir);
    if (disToAtmosphere < 0) return float3(0, 0, 0);

    // 计算衰减
    //float3 hitPoint = eyePos + viewDir * disToAtmosphere;
    //sunLuminance *= Transmittance(param, hitPoint, eyePos);
    sunLuminance *= TransmittanceToAtmosphere(param, eyePos, viewDir);

    if (theta < param.SunDiskAngle) return sunLuminance;
    return float3(0, 0, 0);
}

v2f vert(appdata v)
{
    v2f o;
    o.posH = UnityObjectToClipPos(v.posO);
    o.posW = TransformObjectToWorld(v.posO);
    o.uv = v.uv;
    return o;
}

fixed4 frag(v2f i) : SV_Target
{
	AtmosphereParameter param = GetAtmosphereParameter();

	float4 color = float4(0, 0, 0, 1);
	float3 viewDir = normalize(i.posW);

	float3 lightDir = -_DirectionalLightDirection;
	float h = _WorldSpaceCameraPos.y - param.SeaLevel + param.PlanetRadius;
	float3 eyePos = float3(0, h, 0);

	color.rgb += UNITY_SAMPLE_TEX2D(_SkyviewLUT, ViewDirToUV(viewDir)).rgb;
	color.rgb += GetSunDisk(param, eyePos, viewDir, lightDir);

	return color;
}

#endif