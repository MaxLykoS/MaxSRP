#ifndef MAX_POST_PROCESS_BLOOM_INCLUDED
#define MAX_POST_PROCESS_BLOOM_INCLUDED

#include "UnityCG.cginc"

// Adapted from UE 5.8 PostProcessBloom.usf, PostProcessDownsample.usf,
// PostProcessMobile.usf and the weighted Gaussian filter kernel.
// Scene and bloom share the fixed MaxSRP HDR scale, without pre-exposure.
Texture2D<float4> _MaxBloomSource;
Texture2D<float4> _MaxBloomLow;
Texture2D<float4> _MaxBloomExposure;
SamplerState sampler_LinearClamp;
float4 _MaxBloomSourceTexel; // reciprocal size, size
float4 _MaxBloomLowTexel;
float4 _MaxBloomSourceRect; // viewport size / texture size, viewport origin / texture size
float4 _MaxBloomParameters; // threshold/radius, high weight, low weight, unused
float4 _MaxBloomExposureParameters; // manual exposure, use GPU exposure
float4 _MaxBloomKernel[32]; // offset in pixels, normalized weight
int _MaxBloomKernelCount;
float4 _MaxBloomFilterAxis;

struct BloomVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_POSITION;
};

BloomVaryings BloomVertex(uint id : SV_VertexID)
{
    BloomVaryings output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.position = float4(output.uv * 2 - 1, 0, 1);
#if UNITY_UV_STARTS_AT_TOP
    output.uv.y = 1 - output.uv.y;
#endif
    return output;
}

float3 BloomFinite(float3 value)
{
    value = float3(isnan(value.r) ? 0 : value.r, isnan(value.g) ? 0 : value.g, isnan(value.b) ? 0 : value.b);
    return clamp(value, 0, 65504);
}

float3 BloomSample(float2 uv)
{
    float2 lower = _MaxBloomSourceRect.zw + 0.5 * _MaxBloomSourceTexel.xy;
    float2 upper = _MaxBloomSourceRect.zw + _MaxBloomSourceRect.xy - 0.5 * _MaxBloomSourceTexel.xy;
    return BloomFinite(_MaxBloomSource.SampleLevel(sampler_LinearClamp, clamp(uv, lower, upper), 0).rgb);
}

float3 BloomSampleLow(float2 uv)
{
    return BloomFinite(_MaxBloomLow.SampleLevel(sampler_LinearClamp,
        clamp(uv, 0.5 * _MaxBloomLowTexel.xy, 1 - 0.5 * _MaxBloomLowTexel.xy), 0).rgb);
}

float BloomExposure()
{
    float value = _MaxBloomExposureParameters.y > 0.5
        ? _MaxBloomExposure.Load(int3(0, 0, 0)).x : _MaxBloomExposureParameters.x;
    // Normal camera limits are far inside this range. Guard corrupt/non-finite
    // values before luminance multiplication and the mobile HDR weights.
    return isnan(value) ? 1 : clamp(value, 1e-20, 1e20);
}

float3 BloomThreshold(float3 color, float exposure)
{
    // UE uses a two-unit linear transition above the exposed luminance threshold.
    // -1 bypasses thresholding. The result remains in unexposed scene units.
    float amount = _MaxBloomParameters.x < 0 ? 1 :
        saturate((dot(color, float3(0.3, 0.59, 0.11)) * exposure - _MaxBloomParameters.x) * 0.5);
    return color * amount;
}

float3 BloomFourSamples(float2 uv)
{
    float2 offset = _MaxBloomSourceTexel.xy;
    return (BloomSample(uv + offset * float2(-1, -1)) + BloomSample(uv + offset * float2(1, -1)) +
        BloomSample(uv + offset * float2(-1, 1)) + BloomSample(uv + offset * float2(1, 1))) * 0.25;
}

float4 BloomSetupPC(BloomVaryings input) : SV_Target
{
    float2 uv = input.uv * _MaxBloomSourceRect.xy + _MaxBloomSourceRect.zw;
    return float4(BloomThreshold(BloomFourSamples(uv), BloomExposure()), 0);
}

float4 BloomDownsamplePC(BloomVaryings input) : SV_Target
{
    return float4(BloomFourSamples(input.uv), 0);
}

float3 BloomGaussian(float2 uv)
{
    float3 result = 0;
    [loop] for (int i = 0; i < _MaxBloomKernelCount; ++i)
        result += BloomSample(uv + _MaxBloomFilterAxis.xy * _MaxBloomSourceTexel.xy * _MaxBloomKernel[i].x) * _MaxBloomKernel[i].y;
    return result;
}

float4 BloomGaussianX(BloomVaryings input) : SV_Target
{
    return float4(BloomGaussian(input.uv), 0);
}

float4 BloomGaussianYAdd(BloomVaryings input) : SV_Target
{
    return float4(BloomFinite(BloomGaussian(input.uv) * _MaxBloomParameters.y + BloomSampleLow(input.uv)), 0);
}

float BloomHDRWeight(float3 color, float exposure)
{
    return rcp(dot(color, float3(1, 2, 1)) * exposure + 4);
}

float4 BloomSetupMobile(BloomVaryings input) : SV_Target
{
    float exposure = BloomExposure();
    float2 uv = input.uv * _MaxBloomSourceRect.xy + _MaxBloomSourceRect.zw;
    float2 offset = _MaxBloomSourceTexel.xy;
    float3 c0 = BloomSample(uv + offset * float2(-1, -1));
    float3 c1 = BloomSample(uv + offset * float2(1, -1));
    float3 c2 = BloomSample(uv + offset * float2(-1, 1));
    float3 c3 = BloomSample(uv + offset * float2(1, 1));
    float4 weights = float4(BloomHDRWeight(c0, exposure), BloomHDRWeight(c1, exposure),
        BloomHDRWeight(c2, exposure), BloomHDRWeight(c3, exposure));
    // Algebraically identical to UE HdrWeight4 -> average -> HdrWeightInv4.
    // This form avoids cancellation in 1-Luma4 when exposed HDR is very bright.
    float3 color = (c0 * weights.x + c1 * weights.y + c2 * weights.z + c3 * weights.w) /
        max(dot(weights, float4(1, 1, 1, 1)), 1e-30);
    return float4(BloomFinite(BloomThreshold(color, exposure)), 0);
}

float2 BloomCircle(float start, float count, float index)
{
    float angle = 6.28318530718 * (start + index) / count;
    return float2(sin(angle), cos(angle));
}

float4 BloomDownsampleMobile(BloomVaryings input) : SV_Target
{
    float3 result = BloomSample(input.uv);
    [unroll] for (int i = 0; i < 14; ++i)
        result += BloomSample(input.uv + BloomCircle(2.0 / 14.0, 14, i) * _MaxBloomParameters.x * _MaxBloomSourceTexel.xy);
    return float4(BloomFinite(result / 15), 0);
}

float4 BloomUpsampleMobile(BloomVaryings input) : SV_Target
{
    float3 high = BloomSample(input.uv);
    float3 low = BloomSampleLow(input.uv);
    [unroll] for (int i = 0; i < 7; ++i)
    {
        float2 offset = BloomCircle(2.0 / 7.0, 7, i) * _MaxBloomParameters.x;
        high += BloomSample(input.uv + offset * _MaxBloomSourceTexel.xy);
        low += BloomSampleLow(input.uv + offset * _MaxBloomLowTexel.xy);
    }
    return float4(BloomFinite((high * _MaxBloomParameters.y + low * _MaxBloomParameters.z) / 8), 0);
}

float4 BloomMergeMobile(BloomVaryings input) : SV_Target
{
    float3 high = BloomSample(input.uv);
    [unroll] for (int i = 0; i < 6; ++i)
        high += BloomSample(input.uv + BloomCircle(2.0 / 6.0, 6, i) * _MaxBloomParameters.x * _MaxBloomSourceTexel.xy);
    return float4(BloomFinite((high * (_MaxBloomParameters.y / 7) + BloomSampleLow(input.uv)) * _MaxBloomParameters.z), 0);
}

#endif
