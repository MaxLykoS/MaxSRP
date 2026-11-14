#ifndef MAX_UE_MOBILE_SSAO_INCLUDED
#define MAX_UE_MOBILE_SSAO_INCLUDED

#include "UnityCG.cginc"

// Adapted from Epic Games' UE 5.8.4 / CL 58902052 implementation:
// PostProcessAmbientOcclusion.usf: MainPSandCS, SSAOSmoothPSandCS, UpsampleSSAO.
// PostProcessAmbientOcclusionCommon.ush: WedgeWithNormal, OPTIMIZATION_O1.
// SystemTextures.cpp: the original quantized 4x4 SSAO randomization pattern.
// Engine entry point: PostProcessAmbientOcclusionMobile.cpp.
// Original UE source copyright Epic Games, Inc. All Rights Reserved.
// Engine-specific transforms, centimetres, HZB addressing, and depth packing
// are adapted here; no GTAO integral or independent temporal resolve is used.
// MAX_SSAO_INLINE_QUAD enables fine derivatives only on the higher shader
// target. Without it, runtime records the explicit quad filter at quality 1/2.
// Offline compilation may define the same keyword before including this file.

Texture2D<float> _GDepth;
Texture2D<float4> _GBuffer1;
Texture2D<float> _SSAOSetupTexture;
Texture2D<float4> _SSAOInput;
SamplerState ssao_point_clamp_sampler;
SamplerState ssao_linear_clamp_sampler;

float4 _SSAOFullSize;       // full W, H, 1/W, 1/H
float4 _SSAOSize;           // AO W, H, 1/W, 1/H
float4 _SSAOHZBSize;        // ceil(full W/2), ceil(full H/2), reciprocals
float4x4 _SSAOInvProjection;
float4x4 _SSAOView;
float4 _SSAOProjectionScale; // signed GPU P00, P11, P02, P12 (including jitter)
float4 _SSAOParameters;    // world radius m, bias parameter m, power, fade distance m
float _SSAOFadeRadius;
float _SSAOScreenRadius;   // UE view-space radius parameter, default 200
float _SSAORadiusInWorldSpace;
int _SSAOQuality;
int _SSAONoiseIndex;
int _SSAOUpsampleQuality;
int _SSAOFilterMode;       // 0: explicit quad-normal blur, 1: UE no-TAA smooth

static const float SSAOMaxDepth = 1000000.0;
static const float SSAOPi = 3.14159265358979323846;

struct SSAOVaryings
{
    float4 position : SV_POSITION;
};

SSAOVaryings SSAOVertex(uint vertexID : SV_VertexID)
{
    SSAOVaryings output;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

bool SSAOBackground(float deviceDepth)
{
#if defined(UNITY_REVERSED_Z)
    return deviceDepth <= 0.0;
#else
    return deviceDepth >= 1.0;
#endif
}

float2 SSAOUVToClip(float2 uv)
{
    float2 position = uv * 2.0 - 1.0;
#if UNITY_UV_STARTS_AT_TOP
    position.y = -position.y;
#endif
    return position;
}

float2 SSAOClipToUV(float2 position)
{
#if UNITY_UV_STARTS_AT_TOP
    position.y = -position.y;
#endif
    return position * 0.5 + 0.5;
}

int2 SSAOFullPixel(float2 uv)
{
    return clamp(int2(uv * _SSAOFullSize.xy), 0, int2(_SSAOFullSize.xy) - 1);
}

float SSAOLinearDepthAtPixel(int2 pixel)
{
    float depth = _GDepth.Load(int3(pixel, 0));
    if (SSAOBackground(depth)) return SSAOMaxDepth;
    float clipDepth = depth;
#if !defined(UNITY_REVERSED_Z)
    clipDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
    float2 uv = (pixel + 0.5) * _SSAOFullSize.zw;
    float4 position = mul(_SSAOInvProjection, float4(SSAOUVToClip(uv), clipDepth, 1.0));
    float linearDepth = -position.z / position.w;
    // Geometry beyond the AO fade is unoccluded. Keep a finite background
    // sentinel for the HZB, packed working buffer, and bilateral depth weights.
    return isnan(linearDepth) ? SSAOMaxDepth : clamp(linearDepth, 0.0001, SSAOMaxDepth);
}

float3 SSAOViewPosition(float2 uv, float linearDepth)
{
    float4 ray = mul(_SSAOInvProjection, float4(SSAOUVToClip(uv), 0.5, 1.0));
    float3 positiveZRay = float3(ray.xy, -ray.z);
    return float3(positiveZRay.xy / max(abs(positiveZRay.z), 1e-8) * linearDepth, linearDepth);
}

float2 SSAOProjectView(float3 position)
{
    // Unity view space faces -Z; the SSAO math below uses positive view depth.
    float2 clip = position.xy / max(position.z, 1e-6) * _SSAOProjectionScale.xy - _SSAOProjectionScale.zw;
    return SSAOClipToUV(clip);
}

float3 SSAOViewNormal(float2 uv)
{
    float3 normal = _GBuffer1.Load(int3(SSAOFullPixel(uv), 0)).xyz * 2.0 - 1.0;
    normal = mul((float3x3)_SSAOView, normal);
    normal.z = -normal.z;
    return normal * rsqrt(max(dot(normal, normal), 1e-8));
}

float3 SSAOQuadNormal(float2 xy)
{
    // UE ReconstructNormal uses the positive Z hemisphere for quad weights.
    return float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
}

float3 SSAOPackDepth(float depth)
{
    // UE OUTPUT_DEPTH: PackR24F(asuint(depthCm)>>8), then UnpackRGBA8.rgb.
    // Preserve centimetres inside this byte representation; all other code
    // uses metres. Half-res RGBA8 stores AO in R and these depth bytes in GBA.
    uint bits = asuint(max(depth, 0.0001) * 100.0) >> 8;
    return float3(bits & 255u, (bits >> 8) & 255u, (bits >> 16) & 255u) * (1.0 / 255.0);
}

float SSAOUnpackDepth(float3 packed)
{
    uint3 bytes = (uint3)(saturate(packed) * 255.0);
    uint bits = bytes.x | (bytes.y << 8) | (bytes.z << 16);
    return asfloat(bits << 8) * 0.01;
}

float SSAONormalWeight(float2 a, float2 b)
{
    float cosine = saturate(dot(SSAOQuadNormal(a), SSAOQuadNormal(b)));
    float square = cosine * cosine;
    return square * square;
}

// SystemTextures.cpp's Reorder[16], lenm and Quantize8SignedByte results.
// Point/wrap sampling the original 64x64 RG8 texture gives the same repeated
// 4x4 table. Keep its 8-bit quantization instead of inventing another noise.
static const uint2 SSAORandomBytes[16] =
{
    uint2(240,128), uint2(67,219), uint2(147,224), uint2(225,193),
    uint2(89,221),  uint2(197,197),uint2(3,152),   uint2(38,217),
    uint2(174,240), uint2(128,242),uint2(225,147), uint2(30,168),
    uint2(41,186),  uint2(242,175),uint2(103,250), uint2(194,227)
};

float2 SSAORandomVector(float2 uv)
{
    // GetSSAOShaderParameters: phase*(2.48,7.52)/randomTextureWidth.
    float temporalScale = _SSAOSize.x / _SSAOFullSize.x;
    float2 position = uv * _SSAOSize.xy + (_SSAONoiseIndex & 7) * float2(2.48, 7.52) * temporalScale;
    uint2 pixel = (uint2)floor(position) & 3u;
    return float2(SSAORandomBytes[pixel.x + pixel.y * 4u]) * (2.0 / 255.0) - 1.0;
}

static const float2 SSAOSamples3[3] =
{
    float2(0.0,-1.0)*0.43, float2(0.58,0.814)*0.7, float2(-0.58,0.814)
};
static const float2 SSAOSamples6[6] =
{
    float2(0.000,0.200), float2(0.325,0.101), float2(0.272,-0.396),
    float2(-0.385,-0.488), float2(-0.711,0.274), float2(0.060,0.900)
};

float2 SSAOWedgeWithNormal(float2 sampleUVLeft, float2 sampleUVRight,
    float3 biasedViewPosition, float3 scaledViewNormal)
{
    // UE's default HzbStepMipLevelFactor=.5 makes ComputeMipLevel negative
    // for every sample. Its clamped level 0 is the furthest 2x2 scene-depth HZB.
    // No mip-blended multi-resolution AO or non-default HZB factor is exposed.
    float depthLeft = _SSAOSetupTexture.SampleLevel(ssao_point_clamp_sampler, sampleUVLeft, 0);
    float depthRight = _SSAOSetupTexture.SampleLevel(ssao_point_clamp_sampler, sampleUVRight, 0);
    float3 deltaLeft = SSAOViewPosition(sampleUVLeft, depthLeft) - biasedViewPosition;
    float3 deltaRight = SSAOViewPosition(sampleUVRight, depthRight) - biasedViewPosition;
    // WedgeWithNormal OPTIMIZATION_O1, after cancelling UE's FovFix/InvFovFix.
    return saturate(float2(dot(deltaLeft, scaledViewNormal), dot(deltaRight, scaledViewNormal)) /
        max(float2(dot(deltaLeft, deltaLeft), dot(deltaRight, deltaRight)), 1e-10));
}

float SSAOVisibility(float2 uv, float depth, float3 normal)
{
    if (depth >= _SSAOParameters.w || depth >= SSAOMaxDepth) return 1.0;
    float useWorldRadius = saturate(_SSAORadiusInWorldSpace);
    // UE radius: non-WS /400, then /4 for its mip convention, times scene Z.
    float actualRadius = lerp(max(_SSAOScreenRadius, 0.0) * depth / 1600.0,
        max(_SSAOParameters.x, 0.0) * 0.25, useWorldRadius);
    if (actualRadius <= 1e-8) return 1.0;
    float3 position = SSAOViewPosition(uv, depth);
    // UE stores Bias in cm, divides it by 1000, then multiplies by scene Z.
    // Our bias parameter is metres: .03m corresponds to UE's default 3cm.
    position += normal * (_SSAOParameters.y * 0.1 * depth);
    float2 centerUV = SSAOProjectView(position);
    float2 random = SSAORandomVector(uv) * actualRadius;
    float3 scaledNormal = normal * (0.08 * lerp(depth, 10.0, useWorldRadius));
    float2 clipToUV = float2(0.5, 0.5);
#if UNITY_UV_STARTS_AT_TOP
    clipToUV.y = -clipToUV.y;
#endif
    float2 projectedRadius = _SSAOProjectionScale.xy * clipToUV / max(position.z, 1e-6);
    float2 weightAccumulator = 0.0001;
    int sampleCount = _SSAOQuality >= 4 ? 6 : 3;
    int stepCount = _SSAOQuality >= 3 ? 3 : (_SSAOQuality == 2 ? 2 : 1);
    [loop] for (int i = 0; i < sampleCount; ++i)
    {
        float2 offset = _SSAOQuality >= 4 ? SSAOSamples6[i] : SSAOSamples3[i];
        float2 localRandom = offset.x * random + offset.y * float2(-random.y, random.x);
        float2 maximumAngle = 0.0;
        [loop] for (int step = 0; step < stepCount; ++step)
        {
            float scale = (step + 1.0) / stepCount;
            float2 deltaUV = scale * localRandom * projectedRadius;
            maximumAngle = max(maximumAngle, SSAOWedgeWithNormal(centerUV + deltaUV,
                centerUV - deltaUV, position, scaledNormal));
        }
        float2 visibility = 1.0 - maximumAngle;
        weightAccumulator += float2(dot(visibility, visibility), 2.0);
    }
    float visibility = weightAccumulator.x / weightAccumulator.y;
    // MainPSandCS applies distance fade, then Power, then its quad blur.
    float fade = saturate((depth - (_SSAOParameters.w - _SSAOFadeRadius)) / max(_SSAOFadeRadius, 0.01));
    visibility = lerp(visibility, 1.0, fade);
    // Intensity/Strength is applied once by the existing indirect-light path.
    return saturate(pow(abs(visibility), max(_SSAOParameters.z, 0.01)));
}

float4 SSAOSetup(SSAOVaryings input) : SV_Target
{
    int2 origin = int2(input.position.xy) * 2;
    int2 maximum = int2(_SSAOFullSize.xy) - 1;
    float depth = SSAOLinearDepthAtPixel(min(origin, maximum));
    depth = max(depth, SSAOLinearDepthAtPixel(min(origin + int2(1,0), maximum)));
    depth = max(depth, SSAOLinearDepthAtPixel(min(origin + int2(0,1), maximum)));
    depth = max(depth, SSAOLinearDepthAtPixel(min(origin + 1, maximum)));
    return float4(depth, 0, 0, 1);
}

float4 SSAOEvaluate(SSAOVaryings input) : SV_Target
{
    float2 uv = input.position.xy * _SSAOSize.zw;
    float depth = SSAOLinearDepthAtPixel(SSAOFullPixel(uv));
    float3 normal = SSAOViewNormal(uv);
    float visibility = SSAOVisibility(uv, depth, normal);
#if defined(MAX_SSAO_INLINE_QUAD) && !defined(SHADER_API_GLES3) && !defined(SHADER_API_GLES)
    // All fragments reach these derivatives, including sky / faded pixels.
    // Do not move early returns from SSAOVisibility above this point.
    if (_SSAOQuality == 1 || _SSAOQuality == 2)
    {
        float4 center = float4(visibility, depth, normal.xy);
        float4 derivativeX = ddx_fine(center);
        float4 derivativeY = ddy_fine(center);
        int2 parity = int2(input.position.xy) & 1;
        float4 neighborX = center - derivativeX * (parity.x * 2 - 1);
        float4 neighborY = center - derivativeY * (parity.y * 2 - 1);
        float weightX = SSAONormalWeight(center.zw, neighborX.zw);
        float weightY = SSAONormalWeight(center.zw, neighborY.zw);
        visibility = (visibility + weightX * neighborX.x + weightY * neighborY.x) / (1.0 + weightX + weightY);
    }
#endif
    return float4(saturate(visibility), SSAOPackDepth(depth));
}

float4 SSAOFilter(SSAOVaryings input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float2 uv = input.position.xy * _SSAOSize.zw;
    float depth = SSAOLinearDepthAtPixel(SSAOFullPixel(uv));
    float visibility;
    if (_SSAOFilterMode == 0)
    {
        // Compatibility equivalent of UE's inline fine-derivative quad blur.
        // GLES3 uses explicit same-quad point loads instead of fine derivatives.
        int2 maximum = int2(_SSAOSize.xy) - 1;
        int2 neighborX = clamp(int2(pixel.x ^ 1, pixel.y), 0, maximum);
        int2 neighborY = clamp(int2(pixel.x, pixel.y ^ 1), 0, maximum);
        float2 uvX = (neighborX + 0.5) * _SSAOSize.zw;
        float2 uvY = (neighborY + 0.5) * _SSAOSize.zw;
        float2 normal = SSAOViewNormal(uv).xy;
        float weightX = SSAONormalWeight(normal, SSAOViewNormal(uvX).xy);
        float weightY = SSAONormalWeight(normal, SSAOViewNormal(uvY).xy);
        visibility = (_SSAOInput.Load(int3(pixel, 0)).r +
            weightX * _SSAOInput.Load(int3(neighborX, 0)).r +
            weightY * _SSAOInput.Load(int3(neighborY, 0)).r) / (1.0 + weightX + weightY);
    }
    else
    {
        // SSAOSmoothPSandCS: UE's four bilinear fetches at offsets 0/2.
        // Runtime enables this only when TAA is absent and AO is full-res.
        visibility = _SSAOInput.SampleLevel(ssao_linear_clamp_sampler, uv, 0).r;
        visibility += _SSAOInput.SampleLevel(ssao_linear_clamp_sampler, uv + float2(2,0) * _SSAOSize.zw, 0).r;
        visibility += _SSAOInput.SampleLevel(ssao_linear_clamp_sampler, uv + float2(0,2) * _SSAOSize.zw, 0).r;
        visibility += _SSAOInput.SampleLevel(ssao_linear_clamp_sampler, uv + 2.0 * _SSAOSize.zw, 0).r;
        visibility *= 0.25;
    }
    return float4(saturate(visibility), SSAOPackDepth(depth));
}

float4 SSAOUpscale(SSAOVaryings input) : SV_Target
{
    float2 uv = input.position.xy * _SSAOFullSize.zw;
    int2 fullPixel = SSAOFullPixel(uv);
    if (SSAOBackground(_GDepth.Load(int3(fullPixel, 0)))) return 1.0;
    float visibility = 1.0;
    if (_SSAOUpsampleQuality == 0)
    {
        visibility = _SSAOInput.SampleLevel(ssao_linear_clamp_sampler, uv, 0).r;
    }
    else if (_SSAOUpsampleQuality == 1)
    {
        float2 cornerUV = (floor(uv * _SSAOSize.xy - 0.5) + 0.5) * _SSAOSize.zw;
        float2 blend = (uv - cornerUV) * _SSAOSize.xy;
        float4 s00 = _SSAOInput.SampleLevel(ssao_point_clamp_sampler, cornerUV, 0);
        float4 s10 = _SSAOInput.SampleLevel(ssao_point_clamp_sampler, cornerUV + float2(_SSAOSize.z,0), 0);
        float4 s01 = _SSAOInput.SampleLevel(ssao_point_clamp_sampler, cornerUV + float2(0,_SSAOSize.w), 0);
        float4 s11 = _SSAOInput.SampleLevel(ssao_point_clamp_sampler, cornerUV + _SSAOSize.zw, 0);
        float4 weights = float4((1-blend.y)*(1-blend.x), (1-blend.y)*blend.x,
            blend.y*(1-blend.x), blend.y*blend.x);
        float4 depths = float4(SSAOUnpackDepth(s00.gba), SSAOUnpackDepth(s10.gba),
            SSAOUnpackDepth(s01.gba), SSAOUnpackDepth(s11.gba));
        float maximum = max(max(depths.x,depths.y),max(depths.z,depths.w));
        float minimum = min(min(depths.x,depths.y),min(depths.z,depths.w));
        if (maximum / max(minimum, 1e-6) > 1.02)
        {
            float depth = SSAOLinearDepthAtPixel(fullPixel);
            weights /= abs(depths - depth) + 1e-6; // UE's .0001 cm epsilon in metres.
        }
        visibility = dot(weights, float4(s00.x,s10.x,s01.x,s11.x)) / max(dot(weights,1.0), 1e-8);
    }
    else
    {
        float depth = SSAOLinearDepthAtPixel(fullPixel);
        float sum = 0.0, weightSum = 0.0;
        [unroll] for (int y = -1; y <= 1; ++y)
        {
            [unroll] for (int x = -1; x <= 1; ++x)
            {
                // UE's nine-tap variant uses the bilinear AO sampler before
                // unpacking depth; the four-tap branch samples texel centres.
                float4 sample = _SSAOInput.SampleLevel(ssao_linear_clamp_sampler,
                    uv + float2(x,y) * _SSAOSize.zw, 0);
                float weight = rcp(abs(depth - SSAOUnpackDepth(sample.gba)) + 1e-6);
                sum += sample.x * weight;
                weightSum += weight;
            }
        }
        visibility = sum / max(weightSum, 1e-8);
    }
    return saturate(visibility).xxxx;
}

#endif
