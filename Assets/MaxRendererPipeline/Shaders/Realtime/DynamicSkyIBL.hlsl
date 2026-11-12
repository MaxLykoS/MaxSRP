#ifndef MAX_DYNAMIC_SKY_IBL_INCLUDED
#define MAX_DYNAMIC_SKY_IBL_INCLUDED

#include "../../ShaderLibrary/Realtime/IBLValidation.hlsl"
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"
#include "CloudLayer.hlsl"
#include "SixWayCloudCommon.hlsl"

// GPU-only dynamic path. The legacy structured-buffer kernels remain available
// for the separate offline probe utilities.
Texture2D<float4> _EnvironmentSky;
SamplerState sampler_EnvironmentSky;
TextureCube<float4> _DynamicRadianceMap;
RWTexture2DArray<float4> _DynamicOutput;
RWTexture2D<float4> _DynamicBRDFLUT;
uint _DynamicWidth;
uint _DynamicSampleCount;
float _DynamicRoughness;
float _DynamicSourceResolution;
float _DynamicSourceMaxMip;

float3 DynamicCubeDirection(uint3 id)
{
    return DirectionFromCubemapTexel(id.z, (id.x + 0.5) / _DynamicWidth, (id.y + 0.5) / _DynamicWidth);
}

float2 DynamicSkyUV(float3 direction)
{
    return float2(atan2(direction.z, direction.x) / (2.0 * PI) + 0.5,
        asin(clamp(direction.y, -1.0, 1.0)) / PI + 0.5);
}

float DynamicSourceMip(float pdf)
{
    float texelSolidAngle = 4.0 * PI / (6.0 * _DynamicSourceResolution * _DynamicSourceResolution);
    float sampleSolidAngle = 1.0 / (max(1u, _DynamicSampleCount) * max(pdf, 1e-6));
    return clamp(0.5 * log2(sampleSolidAngle / texelSolidAngle), 0.0, _DynamicSourceMaxMip);
}

[numthreads(8, 8, 1)]
void SkyToEnvironment(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _DynamicWidth || id.y >= _DynamicWidth || id.z >= 6) return;
    float3 direction = DynamicCubeDirection(id);
    // SkyView contains scattered sky radiance. The analytic solar disk is
    // deliberately absent: direct sunlight is evaluated by the directional BRDF.
    float3 radiance = _EnvironmentSky.SampleLevel(sampler_EnvironmentSky, DynamicSkyUV(direction), 0).rgb;
    if (_MaxCloudShape.x > 0.5)
    {
        AtmosphereParameter atmosphere = GetAtmosphereParameter();
        float3 position = float3(0, atmosphere.PlanetRadius + _MaxCloudGeometry.x, 0);
        float3 cloudLightDirection = _AtmosphereSunDirection;
        float3 solar = atmosphere.SolarIrradiance * TransmittanceToSun(atmosphere, position, cloudLightDirection);
        float3 ambient = _EnvironmentSky.SampleLevel(sampler_EnvironmentSky, float2(0.5, 1), 0).rgb;
        float4 cloud = EvaluateMaxCloudLayer(direction, cloudLightDirection, solar, ambient);
        radiance = cloud.rgb + cloud.a * radiance;
    }
    // Match the raster cards in the low-frequency environment. The cards are
    // sorted far-to-near; only actual bounded ray/quad intersections sample maps.
    for (int card = 0; card < _MaxSixWayCount; ++card)
    {
        float3 right = _MaxSixWayRight[card].xyz, up = _MaxSixWayUp[card].xyz;
        float3 normal = cross(right, up);
        float denominator = dot(direction, normal);
        if (abs(denominator) < 1e-5) continue;
        float3 offset = _MaxSixWayCentres[card].xyz - _MaxSixWayObserver;
        float t = dot(offset, normal) / denominator;
        if (t <= 0) continue;
        float3 hit = direction * t - offset;
        float width = _MaxSixWayRight[card].w;
        float2 uv = float2(dot(hit, right) / width, dot(hit, up) / (width * 0.5)) * 0.5 + 0.5;
        if (any(uv < 0) || any(uv > 1)) continue;
        AtmosphereParameter atmosphere = GetAtmosphereParameter();
        float3 position = float3(0, atmosphere.PlanetRadius + max(1, _MaxSixWayCentres[card].y - _MaxSixWaySeaLevel), 0);
        float3 cloudLightDirection = _AtmosphereSunDirection;
        float3 solar = atmosphere.SolarIrradiance * TransmittanceToSun(atmosphere, position, cloudLightDirection);
        float3 ambient = _EnvironmentSky.SampleLevel(sampler_EnvironmentSky, float2(0.5, 1), 0).rgb;
        float4 cloud = MaxSixWayShade(uv, card, cloudLightDirection, solar, ambient);
        radiance = cloud.rgb + (1 - cloud.a) * radiance;
    }
    // The array/cube uses half storage; finite float values can overflow on store.
    _DynamicOutput[id] = float4(min(FiniteIBLColor(radiance), 65504.0), 1.0);
}

[numthreads(8, 8, 1)]
void FilterSkyDiffuse(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _DynamicWidth || id.y >= _DynamicWidth || id.z >= 6) return;
    float3 N = DynamicCubeDirection(id);
    float3 up = abs(N.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, N));
    float3 bitangent = cross(N, tangent);
    float3 sum = 0.0;
    for (uint i = 0; i < _DynamicSampleCount; ++i)
    {
        float2 xi = Hammersley(i, _DynamicSampleCount);
        float cosTheta = sqrt(1.0 - xi.y);
        float sinTheta = sqrt(xi.y);
        float phi = 2.0 * PI * xi.x;
        float3 L = tangent * (cos(phi) * sinTheta) + bitangent * (sin(phi) * sinTheta) + N * cosTheta;
        sum += FiniteIBLColor(_DynamicRadianceMap.SampleLevel(_Sampler_LinearClamp, L, DynamicSourceMip(cosTheta / PI)).rgb);
    }
    // Cosine-weighted sampling stores E/pi; GetDiffuseIBL multiplies by albedo
    // directly, so the Lambert 1/pi is already included exactly once.
    _DynamicOutput[id] = float4(sum / max(1u, _DynamicSampleCount), 1.0);
}

[numthreads(8, 8, 1)]
void FilterSkySpecular(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _DynamicWidth || id.y >= _DynamicWidth || id.z >= 6) return;
    float3 N = DynamicCubeDirection(id);
    float3 sum = 0.0;
    float totalWeight = 0.0;
    for (uint i = 0; i < _DynamicSampleCount; ++i)
    {
        float3 H = ImportanceSampleGGX(Hammersley(i, _DynamicSampleCount), N, _DynamicRoughness);
        float VdotH = max(dot(N, H), 0.0);
        float3 L = normalize(2.0 * VdotH * H - N);
        float NdotL = max(dot(N, L), 0.0);
        if (NdotL > 0.0)
        {
            float a = _DynamicRoughness * _DynamicRoughness;
            float a2 = a * a;
            float NdotH = max(dot(N, H), 0.0);
            float denominator = NdotH * NdotH * (a2 - 1.0) + 1.0;
            float D = a2 / max(PI * denominator * denominator, 1e-8);
            float pdf = D * NdotH / max(4.0 * VdotH, 1e-6);
            sum += FiniteIBLColor(_DynamicRadianceMap.SampleLevel(_Sampler_LinearClamp, L, DynamicSourceMip(pdf)).rgb) * NdotL;
            totalWeight += NdotL;
        }
    }
    _DynamicOutput[id] = float4(sum / max(totalWeight, 1e-6), 1.0);
}

[numthreads(8, 8, 1)]
void BuildDynamicBRDF(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _DynamicWidth || id.y >= _DynamicWidth) return;
    float2 uv = (float2(id.xy) + 0.5) / _DynamicWidth;
    _DynamicBRDFLUT[id.xy] = float4(IntegrateBRDF(uv.x, uv.y), 0.0, 1.0);
}

#endif
