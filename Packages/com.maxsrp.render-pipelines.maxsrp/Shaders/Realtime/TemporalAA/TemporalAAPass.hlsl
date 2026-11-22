#ifndef MAX_TEMPORAL_AA_INCLUDED
#define MAX_TEMPORAL_AA_INCLUDED
#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/Core/Packing.hlsl"
float _TaaSkyHistoryInvalid;
float4 _MaxSixWayTemporal;

Texture2D<float4> _TaaSource, _TaaHistory, _TaaResolved;
#if defined(MAX_TAA_VELOCITY_UINT)
Texture2D<uint2> _TaaMotion;
#else
Texture2D<float2> _TaaMotion;
#endif
Texture2D<float4> _GBuffer1;
Texture2D<float> _TaaDepth;
Texture2D<float2> _TaaResolvedMetadata;
SamplerState sampler_PointClamp, sampler_LinearClamp;
#if defined(MAX_AUTO_EXPOSURE_BUFFER)
StructuredBuffer<float4> _MaxEyeAdaptationBuffer;
#endif
float4 _TaaSize;
float _TaaManualExposure; // Exposure multiplier for fixed HDR.
float4 _TaaSettings; // History ready, orthographic sky changed, preserve alpha, dynamic marker in alpha.
float3 _TaaHistoryQuantizationError;
uint _TaaFrameIndex;
int _TaaDebugView;
float _TaaSharpness;
float _TaaGen4CurrentFrameWeight;
float4 _MaxTaaJitter;
float4x4 _MaxPreviousVP, _TaaClipToPreviousClip;
#if UNITY_UV_STARTS_AT_TOP
static const float TaaDown = 1;
#else
static const float TaaDown = -1;
#endif

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
    return _TaaSource.SampleLevel(sampler_PointClamp, TaaClampUV(uv), 0);
}
float3 TaaFinite(float3 c)
{
    return float3(isfinite(c.x) ? max(c.x, 0) : 0,
        isfinite(c.y) ? max(c.y, 0) : 0, isfinite(c.z) ? max(c.z, 0) : 0);
}

// UE 5.8.4 Gen4 Main (TemporalAA.usf): Y is four times luma.
float3 TaaYCoCg(float3 rgb)
{
    return float3(dot(rgb, float3(1, 2, 1)), 2 * (rgb.r - rgb.b), dot(rgb, float3(-1, 2, -1)));
}
float3 TaaRGB(float3 c)
{
    c *= 0.25;
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}
float TaaHDRWeight(float luma4, float exposure) { return rcp(luma4 * exposure + 4); }

float4 TaaHistoryTap(float2 uv)
{
    return _TaaHistory.SampleLevel(sampler_LinearClamp, TaaClampUV(uv), 0);
}

// UE TextureSampling.ush, GetBicubic2DCatmullRomSamples. Filter the
// motion-presence bit with exactly the same taps as the RGB history.
float4 TaaHistoryBicubic(float2 uv)
{
    float2 pixel = TaaClampUV(uv) * _TaaSize.xy;
    float2 basePixel = floor(pixel - 0.5) + 1;
    float2 f = pixel - basePixel;
    float2 f2 = f * f;
    float2 inner = (1.25 - f2) * f + 0.5;
    float2 outer = (0.25 * f2 - 0.0625) * (1.125 - 0.5 * f2.yx);
    float2 left = outer - 2 * f * outer;
    float2 right = outer + 2 * f * outer;
    float center = 1 - left.x - right.x - left.y - right.y;
    float2 origin = basePixel - 0.5;
    float4 sum = TaaHistoryTap((origin + float2(-1, inner.y)) * _TaaSize.zw) * left.x;
    sum += TaaHistoryTap((origin + float2(2, inner.y)) * _TaaSize.zw) * right.x;
    sum += TaaHistoryTap((origin + inner) * _TaaSize.zw) * center;
    sum += TaaHistoryTap((origin + float2(inner.x, -1)) * _TaaSize.zw) * left.y;
    sum += TaaHistoryTap((origin + float2(inner.x, 2)) * _TaaSize.zw) * right.y;
    return sum;
}

float2 TaaClipToUV(float2 ndc)
{
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    return ndc * 0.5 + 0.5;
}
float4 TaaClipPosition(float2 uv, float deviceDepth)
{
    float2 ndc = uv * 2 - 1;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    return float4(ndc, DeviceDepthToClipDepth(deviceDepth), 1);
}
bool TaaBackground(float depth)
{
#if UNITY_REVERSED_Z
    return depth <= 0;
#else
    return depth >= 1;
#endif
}
float TaaDepth(float2 uv) { return _TaaDepth.SampleLevel(sampler_PointClamp, TaaClampUV(uv), 0); }
float2 TaaVelocity(float2 uv)
{
#if defined(MAX_TAA_VELOCITY_UINT)
    // UE Gen4 GLES uses an integer texel load and normalizes before decoding.
    int2 pixel = int2(TaaClampUV(uv) * _TaaSize.xy);
    return float2(_TaaMotion.Load(int3(pixel, 0))) / 65535.0;
#else
    return _TaaMotion.SampleLevel(sampler_PointClamp, TaaClampUV(uv), 0);
#endif
}
bool TaaHasVelocity(float2 value) { return value.x > 0; }
bool TaaNearer(float first, float second)
{
#if UNITY_REVERSED_Z
    return first > second;
#else
    return first < second;
#endif
}

// TemporalAASample changes the Z used by camera reprojection and the UV used
// for object velocity. Camera reprojection still uses the center pixel's XY.
float2 TaaSelectVelocityUV(float2 uv, inout float depth)
{
    const float2 stepUV = 2 * _TaaSize.zw * float2(1, TaaDown);
    float2 topLeft = uv - stepUV;
    float2 topRight = uv + float2(stepUV.x, -stepUV.y);
    float2 bottomLeft = uv + float2(-stepUV.x, stepUV.y);
    float2 bottomRight = uv + stepUV;
    float tl = TaaDepth(topLeft);
    float tr = TaaDepth(topRight);
    float bl = TaaDepth(bottomLeft);
    float br = TaaDepth(bottomRight);
    bool leftTop = TaaNearer(tl, tr);
    bool leftBottom = TaaNearer(bl, br);
    float top = leftTop ? tl : tr;
    float bottom = leftBottom ? bl : br;
    bool useTop = TaaNearer(top, bottom);
    float nearest = useTop ? top : bottom;
    if (!TaaNearer(nearest, depth)) return uv;
    depth = nearest;
    return useTop ? (leftTop ? topLeft : topRight) : (leftBottom ? bottomLeft : bottomRight);
}

float3 TaaSkyDirection(float2 uv, float depth)
{
    float3 view = mul(unity_MatrixInvP, TaaClipPosition(uv, depth)).xyz;
    return mul((float3x3)unity_MatrixInvV, view);
}

bool TaaCameraVelocity(float2 uv, float depth, out float2 motion)
{
    motion = 0;
    float4 previous;
    if (TaaBackground(depth))
    {
        if (unity_OrthoParams.w > 0.5) return _TaaSettings.y < 0.5;
        // Use an infinite direction for Unity's finite-far sky depth.
        float3 direction = TaaSkyDirection(uv + _MaxTaaJitter.xy, depth);
        previous = mul(_MaxPreviousVP, float4(direction, 0));
    }
    else
        previous = mul(_TaaClipToPreviousClip, TaaClipPosition(uv, depth));
    if (previous.w <= 0) return false;
    motion = uv - TaaClipToUV(previous.xy / previous.w);
    return all(isfinite(motion));
}

static const float2 TaaPlus[5] = {
    float2(0, -TaaDown), float2(-1, 0), float2(0, 0), float2(1, 0), float2(0, TaaDown)
};

void TaaCurrentNeighborhood(float2 uv, float exposure, float3 center, out float3 filtered, out float3 lo, out float3 hi)
{
    lo = hi = center;
#ifdef MAX_TAA_GEN4_LOW
    filtered = center;
#else
    float2 jitter = _MaxTaaJitter.xy * _TaaSize.xy;
    float total = 0;
    filtered = 0;
#endif
    [unroll] for (int i = 0; i < 5; ++i)
    {
        float2 offset = TaaPlus[i];
        float3 sampleColor = i == 2 ? center : TaaYCoCg(TaaCurrent(uv + offset * _TaaSize.zw).rgb);
        lo = min(lo, sampleColor);
        hi = max(hi, sampleColor);
#ifndef MAX_TAA_GEN4_LOW
        float2 distance = offset - jitter;
        float weight = exp(-2.29 * dot(distance, distance)) * TaaHDRWeight(sampleColor.x, exposure);
        filtered += sampleColor * weight;
        total += weight;
#endif
    }
#ifndef MAX_TAA_GEN4_LOW
    filtered *= rcp(total);
#endif
}

float3 TaaGen4Blend(float3 current, float3 history, float3 lo, float3 hi,
    float2 motion, float exposure, bool responsive, out float historyWeight)
{
    float speed = length(motion * _TaaSize.xy) * 2;
    float currentWeight = lerp(_TaaGen4CurrentFrameWeight, 0.2, saturate(speed / 40));
    float difference = abs(current.x - history.x);
    float minimumWeight = difference > 0 ? saturate(0.01 * history.x / difference) : (history.x > 0 ? 1 : 0);
    currentWeight = responsive ? 0.25 : max(currentWeight, minimumWeight);
    history = clamp(history, lo, hi);
    float currentShare = currentWeight * TaaHDRWeight(current.x, exposure);
    float historyShare = (1 - currentWeight) * TaaHDRWeight(history.x, exposure);
    float invShare = rcp(currentShare + historyShare);
    historyWeight = historyShare * invShare;
    return history * historyWeight + current * (currentShare * invShare);
}

// UE Quantization.ush and RandomPCG.ush. Use the mantissa precision of
// MaxSRP's actual history target, and apply only on the desktop path.
float3 TaaQuantize(float3 color, uint2 pixel)
{
    if (_TaaHistoryQuantizationError.x == 0) return color;
    uint3 v = uint3(pixel, _TaaFrameIndex & 7u) * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v.x += v.y * v.z;
    float noise = float(v.x >> 16u) * (1.0 / 65536.0);
    float3 error = asfloat(asuint(color * _TaaHistoryQuantizationError) & 0xff800000u);
    return color + error * noise;
}

struct TaaOutput { float4 color : SV_Target0; float2 metadata : SV_Target1; };
TaaOutput TaaResolve(TaaVaryings input)
{
    float2 uv = input.uv;
    float4 current = TaaCurrent(uv);
    float depth = TaaDepth(uv);
    bool sky = TaaBackground(depth);
    float2 centerVelocity = TaaVelocity(uv);
    bool valid = _TaaSettings.x > 0.5;
    if (!sky && !TaaHasVelocity(centerVelocity))
    {
        if (centerVelocity.y > 0.75 ||
            (centerVelocity.y < 0.25 && _GBuffer1.SampleLevel(sampler_PointClamp, uv, 0).a <= 0.5))
            valid = false;
    }
    if (sky && (_TaaSkyHistoryInvalid > 0.5 || (unity_OrthoParams.w > 0.5 && _TaaSettings.y > 0.5)))
        valid = false;
    bool responsive = sky && _MaxSixWayTemporal.x > 0.5;

#if defined(MAX_AUTO_EXPOSURE_BUFFER)
    float exposure = _MaxEyeAdaptationBuffer[0].x;
#else
    float exposure = _TaaManualExposure;
#endif
    float3 filtered, lo, hi;
    TaaCurrentNeighborhood(uv, exposure, TaaYCoCg(current.rgb), filtered, lo, hi);
    bool dynamicCenter = TaaHasVelocity(centerVelocity);
    float historyWeight = 0;
    float3 result = filtered;
    [branch] if (valid)
    {
        float2 velocityUV = TaaSelectVelocityUV(uv, depth);
        float2 selected = TaaVelocity(velocityUV);
        float2 motion;
        if (TaaHasVelocity(selected))
        {
            motion = MaxDecodeVelocityUV(selected);
            valid = all(isfinite(motion));
        }
        else
            valid = TaaCameraVelocity(uv, depth, motion);

        float2 historyUV = uv - motion;
        valid = valid && all(historyUV > 0) && all(historyUV < 1);
        [branch] if (valid)
        {
            float4 history = TaaHistoryBicubic(historyUV);
#ifndef MAX_TAA_GEN4_LOW
            [branch] if (_TaaSettings.w > 0.5 && history.a > 0 && !dynamicCenter)
            {
                valid = false;
                [unroll] for (int i = 0; i < 5; ++i)
                    if (i != 2) valid = valid || TaaHasVelocity(TaaVelocity(uv + TaaPlus[i] * _TaaSize.zw));
            }
#endif
            if (valid)
                result = TaaGen4Blend(filtered, TaaYCoCg(history.rgb), lo, hi, motion, exposure, responsive, historyWeight);
        }
    }
    result = TaaFinite(TaaRGB(result));
    result = min(result, 65504);
    result = TaaQuantize(result, uint2(input.position.xy));
    result = min(result, 65504);
    TaaOutput output;
#ifdef MAX_TAA_GEN4_LOW
    output.metadata = float2(0, historyWeight);
#else
    output.metadata = float2(dynamicCenter ? 1 : 0, historyWeight);
#endif
    float alpha = _TaaSettings.z > 0.5 ? current.a : 1;
#ifndef MAX_TAA_GEN4_LOW
    if (_TaaSettings.w > 0.5) alpha = dynamicCenter ? 1 : 0;
#endif
    output.color = float4(result, alpha);
    return output;
}

float4 TaaResolveColorOnly(TaaVaryings input) : SV_Target
{
    return TaaResolve(input).color;
}

// Log-domain detail equals the mean log contrast between a pixel and its
// neighbours. Ratios and small-argument series preserve dim HDR values.
float3 TaaSharpen(float2 uv, float3 color)
{
    float3 center = TaaFinite(color);
    float3 logContrast = 0;
    [unroll] for (int i = 0; i < 4; ++i)
    {
        float2 sampleUV = TaaClampUV(uv + TaaPlus[i < 2 ? i : i + 1] * _TaaSize.zw);
        float3 neighbour = TaaFinite(_TaaResolved.SampleLevel(sampler_PointClamp, sampleUV, 0).rgb);
        float3 delta = (center - neighbour) / (1 + neighbour);
        float3 ratio = (1 + center) / (1 + neighbour);
        logContrast += abs(delta) < 0.001 ? delta * (1 - 0.5 * delta) : log(ratio);
    }
    float3 boost = (0.25 * _TaaSharpness) * logContrast;
    float3 gain = abs(boost) < 0.001 ? boost * (1 + 0.5 * boost) : exp(boost) - 1;
    return TaaFinite(center + (1 + center) * gain);
}
float4 TaaCopy(TaaVaryings input) : SV_Target
{
    [branch] if (_TaaDebugView != 0)
    {
        float weight = saturate(_TaaResolvedMetadata.SampleLevel(sampler_PointClamp, input.uv, 0).y);
        float mask = _TaaDebugView == 1 ? weight : (weight <= 0 ? 1 : 0);
        return float4(mask.xxx, 1);
    }
    float4 resolved = _TaaResolved.SampleLevel(sampler_PointClamp, input.uv, 0);
    if (_TaaSettings.z < 0.5) resolved.a = 1;
    [branch] if (_TaaSharpness > 0) resolved.rgb = TaaSharpen(input.uv, resolved.rgb);
    resolved.rgb = min(resolved.rgb, 65504);
    return resolved;
}
#endif
