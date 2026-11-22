#ifndef MAX_DYNAMIC_SKY_IBL_INCLUDED
#define MAX_DYNAMIC_SKY_IBL_INCLUDED

#include "../../../ShaderLibrary/Realtime/Lighting/IBLValidation.hlsl"
#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/AtmosphereParameter.hlsl"

// GPU-only dynamic path. The legacy structured-buffer kernels remain available
// for the separate offline probe utilities.
Texture2D<float4> _EnvironmentSky;
SamplerState sampler_EnvironmentSky;
TextureCube<float4> _DynamicRadianceMap;
RWTexture2DArray<min10float3> _DynamicOutput;
TextureCube<float4> _SkyDiffuseSource;
SamplerState sampler_PointClamp;
RWStructuredBuffer<float4> _SkyDiffuseSHOutput;
uint _SkyDiffuseSourceMip;
RWTexture2D<float4> _DynamicBRDFLUT;
uint _DynamicWidth;
uint _DynamicSampleCount;
uint _DynamicBRDFHeight;
float _DynamicRoughness;
float _DynamicSourceResolution;
float _DynamicSourceMaxMip;

float3 DynamicCubeDirection(uint3 id)
{
    return DirectionFromCubemapTexel(id.z, (id.x + 0.5) / _DynamicWidth, (id.y + 0.5) / _DynamicWidth);
}

float2 DynamicSkyUV(float3 direction)
{
    // Legacy uniform map: float2(atan2(z,x)/(2*PI)+0.5, asin(y)/PI+0.5).
    return SkyViewLutLocalDirToUV(direction, _AtmosphereEnvironmentSkyViewHeight,
        _PlanetRadius, _AtmosphereSkyViewSizeAndInvSize);
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
    // Clamp to the finite limit of each channel before writing R11G11B10_FLOAT.
    _DynamicOutput[id] = min(FiniteIBLColor(radiance), float3(65024.0, 65024.0, 64512.0));
}

// UE 5.8.4 CL 58902052, ReflectionEnvironmentShaders.usf: ComputeSkyEnvMapDiffuseIrradianceCS.
// SHCommon.ush defines the signed L0-L2 basis and MonteCarlo.ush defines the uniform sphere samples.
struct SkySHChannel
{
    half4 V0;
    half4 V1;
    half V2;
};

struct SkySHRGB
{
    SkySHChannel R;
    SkySHChannel G;
    SkySHChannel B;
};

groupshared SkySHRGB SkySHSamples[64];

SkySHChannel ScaleSkySH(SkySHChannel coeff, half weight)
{
    coeff.V0 *= weight;
    coeff.V1 *= weight;
    coeff.V2 *= weight;
    return coeff;
}

SkySHChannel AddSkySH(SkySHChannel a, SkySHChannel b)
{
    a.V0 += b.V0;
    a.V1 += b.V1;
    a.V2 += b.V2;
    return a;
}

float4 PackSkySHLinear(SkySHChannel coeff, float c0, float c1, float c3)
{
    return float4(-c1 * coeff.V0.w, -c1 * coeff.V0.y, c1 * coeff.V0.z, c0 * coeff.V0.x - c3 * coeff.V1.z);
}

float4 PackSkySHQuadratic(SkySHChannel coeff, float c2, float c3)
{
    return float4(c2 * coeff.V1.x, -c2 * coeff.V1.y, 3.0 * c3 * coeff.V1.z, -c2 * coeff.V1.w);
}

[numthreads(8, 8, 1)]
void ComputeSkyDiffuseSH(uint3 id : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    float2 uv = (float2(id.xy) + 0.5) / 8.0;
    float phi = 2.0 * PI * uv.x;
    float cosTheta = 1.0 - 2.0 * uv.y;
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    float3 direction = float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
    float3 weightedRadiance = _SkyDiffuseSource.SampleLevel(sampler_PointClamp, direction, _SkyDiffuseSourceMip).rgb * (4.0 * PI / 64.0);

    half3 d = direction;
    half3 d2 = d * d;
    SkySHChannel basis;
    basis.V0 = half4(0.282095, -0.488603 * d.y, 0.488603 * d.z, -0.488603 * d.x);
    basis.V1 = half4(1.092548 * d.x * d.y, -1.092548 * d.y * d.z,
        0.315392 * (3.0 * d2.z - 1.0), -1.092548 * d.x * d.z);
    basis.V2 = 0.546274 * (d2.x - d2.y);
    SkySHSamples[lane].R = ScaleSkySH(basis, weightedRadiance.r);
    SkySHSamples[lane].G = ScaleSkySH(basis, weightedRadiance.g);
    SkySHSamples[lane].B = ScaleSkySH(basis, weightedRadiance.b);
    GroupMemoryBarrierWithGroupSync();

    // Use the UE reduction order with explicit barriers for Unity backends.
    [unroll]
    for (uint offset = 32; offset > 0; offset >>= 1)
    {
        if (lane < offset)
        {
            SkySHRGB other = SkySHSamples[lane + offset];
            SkySHSamples[lane].R = AddSkySH(SkySHSamples[lane].R, other.R);
            SkySHSamples[lane].G = AddSkySH(SkySHSamples[lane].G, other.G);
            SkySHSamples[lane].B = AddSkySH(SkySHSamples[lane].B, other.B);
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (lane == 0)
    {
        SkySHRGB sky = SkySHSamples[0];
        // Pack the cosine convolution and Lambert normalization into the SH coefficients.
        const float sqrtPi = sqrt(PI);
        const float c0 = 1.0 / (2.0 * sqrtPi);
        const float c1 = sqrt(3.0) / (3.0 * sqrtPi);
        const float c2 = sqrt(15.0) / (8.0 * sqrtPi);
        const float c3 = sqrt(5.0) / (16.0 * sqrtPi);
        const float c4 = 0.5 * c2;
        _SkyDiffuseSHOutput[0] = PackSkySHLinear(sky.R, c0, c1, c3);
        _SkyDiffuseSHOutput[1] = PackSkySHLinear(sky.G, c0, c1, c3);
        _SkyDiffuseSHOutput[2] = PackSkySHLinear(sky.B, c0, c1, c3);
        _SkyDiffuseSHOutput[3] = PackSkySHQuadratic(sky.R, c2, c3);
        _SkyDiffuseSHOutput[4] = PackSkySHQuadratic(sky.G, c2, c3);
        _SkyDiffuseSHOutput[5] = PackSkySHQuadratic(sky.B, c2, c3);
        _SkyDiffuseSHOutput[6] = float4(c4 * float3(sky.R.V2, sky.G.V2, sky.B.V2), 1.0);
        const half basisL0 = 0.282095;
        float3 average = float3(sky.R.V0.x, sky.G.V0.x, sky.B.V0.x) / (basisL0 * 4.0 * PI);
        _SkyDiffuseSHOutput[7] = dot(average, 0.3333).xxxx;
    }
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
    _DynamicOutput[id] = sum / max(totalWeight, 1e-6);
}

[numthreads(8, 8, 1)]
void BuildDynamicBRDF(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _DynamicWidth || id.y >= _DynamicBRDFHeight) return;
    float2 uv = (float2(id.xy) + 0.5) / float2(_DynamicWidth, _DynamicBRDFHeight);
    _DynamicBRDFLUT[id.xy] = float4(IntegrateBRDF(uv.x, uv.y), 0.0, 1.0);
}

#endif
