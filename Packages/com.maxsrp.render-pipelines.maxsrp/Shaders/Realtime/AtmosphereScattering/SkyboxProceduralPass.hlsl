#ifndef MAX_PROCEDURALSKYBOX_PASS_INCLUDED
#define MAX_PROCEDURALSKYBOX_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/Lighting/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/RayMarching.hlsl"

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

float3 GetMoonDisk(in AtmosphereParameter param, float3 eyePos, float3 viewDir)
{
    // The effective lunar source already contains the TOD fade. Keep the disk's
    // angular size fixed and vary its radiance, rather than its coverage or the sky.
    if (param.MoonDiskAngle <= 0.0 || !any(param.LunarIrradiance > 0.0)) return 0.0;
    float3 moonDirection = param.MoonDirection * rsqrt(max(dot(param.MoonDirection, param.MoonDirection), 1e-12));
    float cosineTheta = clamp(dot(viewDir, moonDirection), -1.0, 1.0);
    float cosineRadius = cos(param.MoonDiskAngle * (PI / 180.0));
    float edgeWidth = max(fwidth(cosineTheta), 1e-8);
    float coverage = smoothstep(cosineRadius - 0.5 * edgeWidth, cosineRadius + 0.5 * edgeWidth, cosineTheta);
    if (coverage <= 0.0 || RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir) >= 0.0) return 0.0;

    // First version is a neutral full-moon disk. Its light traverses the same
    // atmosphere once; foreground atmospheric scattering remains in SkyView.
    float3 moonLuminance = LunarDiskRadiance(param) * coverage *
        TransmittanceToAtmosphere(param, eyePos, viewDir);
    moonLuminance = float3(isnan(moonLuminance.r) ? 0.0 : moonLuminance.r,
        isnan(moonLuminance.g) ? 0.0 : moonLuminance.g,
        isnan(moonLuminance.b) ? 0.0 : moonLuminance.b);
    return clamp(moonLuminance, 0.0, 64000.0);
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
    color.rgb += GetMoonDisk(param, eyePos, viewDir);

    // Protect the combined fixed-HDR target, including overlapping light disks.
    color.rgb = float3(isnan(color.r) ? 0.0 : color.r,
        isnan(color.g) ? 0.0 : color.g, isnan(color.b) ? 0.0 : color.b);
    color.rgb = clamp(color.rgb, 0.0, 64000.0);
	return color;
}

#endif
