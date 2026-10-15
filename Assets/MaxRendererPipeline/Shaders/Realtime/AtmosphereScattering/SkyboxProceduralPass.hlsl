#ifndef MAX_PROCEDURALSKYBOX_PASS_INCLUDED
#define MAX_PROCEDURALSKYBOX_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"

struct v2f
{
    float2 ndc : TEXCOORD0;
    float4 posH : SV_POSITION;
};

float SkyFarClipDepth()
{
#if defined(UNITY_REVERSED_Z)
    return 0.0;
#else
    return 1.0;
#endif
}

float3 GetSunDisk(in AtmosphereParameter param, float3 eyePos, float3 viewDir, float3 lightDir)
{
    // 计算入射光照
    float cosine_theta = clamp(dot(viewDir, lightDir), -1.0, 1.0);
    float theta = acos(cosine_theta) * (180.0 / PI);
    if (theta >= param.SunDiskAngle) return float3(0, 0, 0);
    float3 sunLuminance = SolarDiskRadiance(param);

    // 判断光线是否被星球阻挡
    float disToPlanet = RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir);
    if (disToPlanet >= 0) return float3(0, 0, 0);

    // 和大气层求交
    // A ray that misses the atmosphere keeps unit transmission (visible sun in space).
    sunLuminance *= TransmittanceToAtmosphere(param, eyePos, viewDir);

    return sunLuminance;
}

v2f vert(uint vertexID : SV_VertexID)
{
    v2f o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.ndc = uv * 2.0 - 1.0;
    o.posH = float4(o.ndc, SkyFarClipDepth(), 1.0);
    return o;
}

float4 frag(v2f i) : SV_Target
{
	AtmosphereParameter param = GetAtmosphereParameter();

	float4 color = float4(0, 0, 0, 1);
	// Both the raster position and inverse VP use the offscreen scene projection.
    // Subtract cameraPos*w before normalizing; this also handles an infinite far plane.
    float4 farWorld = mul(unity_MatrixInvVP, float4(i.ndc, SkyFarClipDepth(), 1.0));
    float3 viewDir = normalize(farWorld.xyz - _WorldSpaceCameraPos * farWorld.w);
    if (unity_OrthoParams.w > 0.5)
        viewDir = normalize(mul((float3x3)unity_MatrixInvV, float3(0.0, 0.0, -1.0)));

	float3 lightDir = normalize(_AtmosphereSunDirection);
	float3 eyePos = AtmosphereCameraPosition(_WorldSpaceCameraPos.y, param.SeaLevel, param.PlanetRadius);

	color.rgb += UNITY_SAMPLE_TEX2D(_SkyviewLUT, ViewDirToUV(viewDir)).rgb;
	color.rgb += GetSunDisk(param, eyePos, viewDir, lightDir);

	return color;
}

#endif