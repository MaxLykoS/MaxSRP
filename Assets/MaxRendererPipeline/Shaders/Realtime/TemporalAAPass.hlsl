#ifndef MAX_TEMPORAL_AA_INCLUDED
#define MAX_TEMPORAL_AA_INCLUDED
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"

Texture2D<float4> _TaaSource, _TaaHistory, _TaaMotion, _TaaResolved;
Texture2D<float4> _GBuffer1;
Texture2D<float> _TaaDepth;
Texture2D<float2> _TaaPreviousDepth;
SamplerState sampler_PointClamp, sampler_LinearClamp;
float4 _TaaSize, _TaaSettings, _TaaSourceRect;
float4 _MaxTaaJitter;
float4x4 _MaxUnjitteredVP, _MaxPreviousVP, _MaxPreviousView;

struct TaaVaryings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
TaaVaryings TaaVertex(uint id : SV_VertexID)
{
    TaaVaryings o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * 2 - 1, 0, 1);
#if UNITY_UV_STARTS_AT_TOP
    o.uv.y = 1 - o.uv.y;
#endif
    return o;
}
float2 TaaClampUV(float2 uv) { return clamp(uv, 0.5 * _TaaSize.zw, 1 - 0.5 * _TaaSize.zw); }
float4 TaaCurrent(float2 uv)
{
    return _TaaSource.SampleLevel(sampler_PointClamp, TaaClampUV(uv) * _TaaSourceRect.xy + _TaaSourceRect.zw, 0);
}
float3 TaaFinite(float3 c)
{
    return float3(isfinite(c.x) ? clamp(c.x, 0, 65504) : 0,
        isfinite(c.y) ? clamp(c.y, 0, 65504) : 0, isfinite(c.z) ? clamp(c.z, 0, 65504) : 0);
}
// Bounded HDR working space, independent of the final camera exposure.
float3 TaaWorking(float3 rgb)
{
    rgb = TaaFinite(rgb);
    float y = dot(rgb, float3(0.25, 0.5, 0.25));
    return float3(y, 0.5 * (rgb.r - rgb.b), 0.25 * (-rgb.r + 2 * rgb.g - rgb.b)) / (1 + y);
}
float3 TaaScene(float3 c)
{
    c /= max(1 - c.x, 1e-6);
    return TaaFinite(float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z));
}
float3 TaaClip(float3 history, float3 lo, float3 hi)
{
    float3 center = 0.5 * (hi + lo), extent = max(0.5 * (hi - lo), 1e-6);
    float3 offset = history - center;
    float distance = max(abs(offset.x / extent.x), max(abs(offset.y / extent.y), abs(offset.z / extent.z)));
    return center + offset / max(distance, 1);
}
// Five-tap Catmull-Rom approximation; color-volume clipping below bounds overshoot.
float3 TaaHistoryBicubic(float2 uv)
{
    float2 position = uv * _TaaSize.xy;
    float2 center = floor(position - 0.5) + 0.5;
    float2 f = position - center, f2 = f * f, f3 = f2 * f;
    float2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    float2 w1 = 1.5 * f3 - 2.5 * f2 + 1;
    float2 w2 = -1.5 * f3 + 2 * f2 + 0.5 * f;
    float2 w3 = 0.5 * (f3 - f2), w12 = w1 + w2;
    float2 middle = (center + w2 / w12) * _TaaSize.zw;
    float2 low = (center - 1) * _TaaSize.zw, high = (center + 2) * _TaaSize.zw;
    float a = w12.x * w0.y, b = w0.x * w12.y, c = w12.x * w12.y;
    float d = w3.x * w12.y, e = w12.x * w3.y;
    float3 result = a * _TaaHistory.SampleLevel(sampler_LinearClamp, float2(middle.x, low.y), 0).rgb;
    result += b * _TaaHistory.SampleLevel(sampler_LinearClamp, float2(low.x, middle.y), 0).rgb;
    result += c * _TaaHistory.SampleLevel(sampler_LinearClamp, middle, 0).rgb;
    result += d * _TaaHistory.SampleLevel(sampler_LinearClamp, float2(high.x, middle.y), 0).rgb;
    result += e * _TaaHistory.SampleLevel(sampler_LinearClamp, float2(middle.x, high.y), 0).rgb;
    return result / (a + b + c + d + e);
}
float2 TaaClipUV(float4 p)
{
    float2 uv = p.xy / p.w * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1 - uv.y;
#endif
    return uv;
}
bool TaaBackground(float d)
{
#if UNITY_REVERSED_Z
    return d <= 0;
#else
    return d >= 1;
#endif
}
struct TaaOutput { float4 color : SV_Target0; float2 depthCount : SV_Target1; };
TaaOutput TaaResolve(TaaVaryings input)
{
    float2 uv = input.uv;
    float4 current = TaaCurrent(uv);
    float deviceDepth = _TaaDepth.SampleLevel(sampler_PointClamp, uv, 0);
    bool sky = TaaBackground(deviceDepth);
    float2 ndc = uv * 2 - 1;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    float4 world = mul(unity_MatrixInvVP, float4(ndc, DeviceDepthToClipDepth(deviceDepth), 1));
    float4 previous, unjittered;
    float eyeDepth = 0, expectedDepth = 0;
    if (sky)
    {
        float3 direction = world.xyz - _WorldSpaceCameraPos * world.w;
        if (unity_OrthoParams.w > 0.5)
            direction = mul((float3x3)unity_MatrixInvV, float3(0, 0, -1));
        previous = mul(_MaxPreviousVP, float4(direction, 0));
        unjittered = mul(_MaxUnjitteredVP, float4(direction, 0));
        // An orthographic sky has a single view direction, not a projectable
        // perspective point. Retain it only while the orientation is unchanged.
        if (unity_OrthoParams.w > 0.5) previous = unjittered = float4(0, 0, 0, 1);
    }
    else
    {
        world /= world.w;
        previous = mul(_MaxPreviousVP, world);
        unjittered = mul(_MaxUnjitteredVP, world);
        eyeDepth = max(0, -mul(unity_MatrixV, world).z);
        expectedDepth = -mul(_MaxPreviousView, world).z;
    }
    float2 motion = 0;
    bool valid = _TaaSettings.x > 0.5 && previous.w > 0 && unjittered.w > 0;
    if (sky && unity_OrthoParams.w > 0.5 && _TaaSettings.w > 0.5) valid = false;
    if (valid) motion = TaaClipUV(unjittered) - TaaClipUV(previous);
    float4 objectMotion = _TaaMotion.SampleLevel(sampler_PointClamp, uv, 0);
    if (!sky && abs(objectMotion.w) < 0.5)
        valid = valid && _GBuffer1.SampleLevel(sampler_PointClamp, uv, 0).a > 0.5;
    if (!sky && abs(objectMotion.w) > 0.5)
    {
        valid = valid && objectMotion.w > 0;
        motion = objectMotion.xy;
        expectedDepth = objectMotion.z;
    }
    valid = valid && all(isfinite(motion)) && isfinite(expectedDepth) && isfinite(eyeDepth);
    if (!isfinite(eyeDepth)) eyeDepth = 0;
    // Color history is a resolved canonical pixel grid: velocity excludes jitter.
    // Previous depth is raw raster depth: its lookup ALSO accounts for both jitters.
    float2 historyUV = uv - motion;
    float2 previousDepthUV = historyUV + _MaxTaaJitter.zw - _MaxTaaJitter.xy;
    valid = valid && all(historyUV >= 0.5 * _TaaSize.zw) && all(historyUV <= 1 - 0.5 * _TaaSize.zw) &&
        all(previousDepthUV >= 0) && all(previousDepthUV <= 1);
    // A fresh history must not sample uninitialized/lost textures, even speculatively.
    float2 oldDepthCount = 0;
    if (valid)
    {
        oldDepthCount = _TaaPreviousDepth.SampleLevel(sampler_PointClamp, previousDepthUV, 0);
        float tolerance = max(0.01, 0.005 * expectedDepth);
        valid = sky ? oldDepthCount.x == 0 : oldDepthCount.x > 0 &&
            expectedDepth > 0 && abs(oldDepthCount.x - expectedDepth) <= tolerance;
    }
    float3 centerColor = TaaWorking(current.rgb), result = centerColor;
    float count = 1;
    if (valid)
    {
        float3 minimum = centerColor, maximum = centerColor, sum = 0, sum2 = 0;
        [unroll] for (int y = -1; y <= 1; ++y)
            [unroll] for (int x = -1; x <= 1; ++x)
            {
                float3 c = TaaWorking(TaaCurrent(uv + float2(x, y) * _TaaSize.zw).rgb);
                minimum = min(minimum, c); maximum = max(maximum, c); sum += c; sum2 += c * c;
            }
        float3 mean = sum / 9, sigma = sqrt(max(0, sum2 / 9 - mean * mean));
        float3 lo = max(minimum, mean - _TaaSettings.z * sigma);
        float3 hi = min(maximum, mean + _TaaSettings.z * sigma);
        // Always retain the current sample in the admissible volume.
        lo = min(lo, centerColor); hi = max(hi, centerColor);
        float3 history = TaaWorking(TaaHistoryBicubic(historyUV));
        float3 clipped = TaaClip(history, lo, hi);
        // Large changes beyond local variation reduce confidence; stochastic samples
        // within the local distribution remain eligible to converge.
        float change = abs(history.x - clipped.x) / max(0.02, sigma.x);
        float confidence = 1 - saturate(change);
        float pixels = length(motion * _TaaSize.xy);
        float cap = lerp(_TaaSettings.y, min(_TaaSettings.y, 0.8), saturate(pixels / 8));
        float weight = min(cap, oldDepthCount.y / (oldDepthCount.y + 1)) * confidence;
        result = lerp(centerColor, clipped, weight);
        count = min(32, 1 / max(1 - weight, 1e-4));
    }
    TaaOutput o;
    o.color = float4(TaaScene(result), current.a);
    o.depthCount = float2(eyeDepth, count);
    return o;
}
float4 TaaCopy(TaaVaryings input) : SV_Target
{
    return _TaaResolved.SampleLevel(sampler_PointClamp, input.uv, 0);
}
#endif
