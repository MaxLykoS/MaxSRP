#ifndef MAX_PROCEDURALSKYBOX_PASS_INCLUDED
#define MAX_PROCEDURALSKYBOX_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"
#define MAX_CLOUD_FRAGMENT_SAMPLING
#include "../CloudLayer.hlsl"

float4 _AtmosphereSkyViewRight;
float4 _AtmosphereSkyViewForward;

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
    float cosineTheta = clamp(dot(viewDir, lightDir), -1.0, 1.0);
    float cosineRadius = cos(max(param.SunDiskAngle, 0.0) * (PI / 180.0));
    if (cosineTheta <= cosineRadius) return float3(0, 0, 0);

    // UE SkyAtmosphereCommon.ush: soften only the outer part of the existing
    // disk. This reduces edge flicker and bloom energy without enlarging it.
    float softEdge = saturate(2.0 * (cosineTheta - cosineRadius) /
        max(1.0 - cosineRadius, 1e-10));
    float3 sunLuminance = SolarDiskRadiance(param) * softEdge;

    float disToPlanet = RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir);
    if (disToPlanet >= 0) return float3(0, 0, 0);

    // A ray that misses the atmosphere keeps unit transmission (sun in space).
    sunLuminance *= TransmittanceToAtmosphere(param, eyePos, viewDir);
    // Protect the fixed-HDR RGBA16F target before a possible half overflow.
    // This is not UE's exposure-dependent 64000 clamp: histories remain
    // unexposed, and ordinary solar values are below this storage limit.
    sunLuminance = float3(isnan(sunLuminance.r) ? 0.0 : sunLuminance.r,
        isnan(sunLuminance.g) ? 0.0 : sunLuminance.g,
        isnan(sunLuminance.b) ? 0.0 : sunLuminance.b);
    return clamp(sunLuminance, 0.0, 64000.0);
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
	float3 eyePos = AtmosphereCameraPosition(_WorldSpaceCameraPos.y * ATMOSPHERE_METRES_TO_KM, param.SeaLevel, param.PlanetRadius);

	// Legacy lookup: UNITY_SAMPLE_TEX2D(_SkyviewLUT, ViewDirToUV(viewDir)).rgb;
    float3 localViewDir = float3(dot(viewDir, _AtmosphereSkyViewRight.xyz), viewDir.y,
        dot(viewDir, _AtmosphereSkyViewForward.xyz));
	float2 skyUV = SkyViewLutLocalDirToUV(localViewDir, eyePos.y, param.PlanetRadius, _AtmosphereSkyViewSizeAndInvSize);
	color.rgb += UNITY_SAMPLE_TEX2D(_SkyviewLUT, skyUV).rgb;
	color.rgb += GetSunDisk(param, eyePos, viewDir, lightDir);
    if (_MaxCloudShape.x > 0.5)
    {
        float3 cloudPosition = float3(0, param.PlanetRadius + _MaxCloudGeometry.x * ATMOSPHERE_METRES_TO_KM, 0);
        float3 cloudLightDirection = lightDir;
        float3 solar = param.SolarIrradiance * TransmittanceToSun(param, cloudPosition, cloudLightDirection);
        // Legacy uniform map sampled zenith at float2(0.5, 1).
        float2 zenithUV = SkyViewLutLocalDirToUV(float3(0, 1, 0), eyePos.y,
            param.PlanetRadius, _AtmosphereSkyViewSizeAndInvSize);
        float3 ambient = UNITY_SAMPLE_TEX2D_LOD(_SkyviewLUT, zenithUV, 0).rgb;
        float4 cloud = EvaluateMaxCloudLayer(viewDir, cloudLightDirection, solar, ambient);
        color.rgb = cloud.rgb + cloud.a * color.rgb;
    }

	return color;
}

#endif
