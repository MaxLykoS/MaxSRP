#ifndef MAX_SHADOW_INCLUDED
#define MAX_SHADOW_INCLUDED

#include "./LightInput.hlsl"
#include "./ShadowInput.hlsl"
#include "./SpaceTransform.hlsl"

// PCSS **********************
#define N_SAMPLE 64
static float2 poissonDisk[N_SAMPLE] = 
{
    float2(-0.5119625f, -0.4827938f),
    float2(-0.2171264f, -0.4768726f),
    float2(-0.7552931f, -0.2426507f),
    float2(-0.7136765f, -0.4496614f),
    float2(-0.5938849f, -0.6895654f),
    float2(-0.3148003f, -0.7047654f),
    float2(-0.42215f, -0.2024607f),
    float2(-0.9466816f, -0.2014508f),
    float2(-0.8409063f, -0.03465778f),
    float2(-0.6517572f, -0.07476326f),
    float2(-0.1041822f, -0.02521214f),
    float2(-0.3042712f, -0.02195431f),
    float2(-0.5082307f, 0.1079806f),
    float2(-0.08429877f, -0.2316298f),
    float2(-0.9879128f, 0.1113683f),
    float2(-0.3859636f, 0.3363545f),
    float2(-0.1925334f, 0.1787288f),
    float2(0.003256182f, 0.138135f),
    float2(-0.8706837f, 0.3010679f),
    float2(-0.6982038f, 0.1904326f),
    float2(0.1975043f, 0.2221317f),
    float2(0.1507788f, 0.4204168f),
    float2(0.3514056f, 0.09865579f),
    float2(0.1558783f, -0.08460935f),
    float2(-0.0684978f, 0.4461993f),
    float2(0.3780522f, 0.3478679f),
    float2(0.3956799f, -0.1469177f),
    float2(0.5838975f, 0.1054943f),
    float2(0.6155105f, 0.3245716f),
    float2(0.3928624f, -0.4417621f),
    float2(0.1749884f, -0.4202175f),
    float2(0.6813727f, -0.2424808f),
    float2(-0.6707711f, 0.4912741f),
    float2(0.0005130528f, -0.8058334f),
    float2(0.02703013f, -0.6010728f),
    float2(-0.1658188f, -0.9695674f),
    float2(0.4060591f, -0.7100726f),
    float2(0.7713396f, -0.4713659f),
    float2(0.573212f, -0.51544f),
    float2(-0.3448896f, -0.9046497f),
    float2(0.1268544f, -0.9874692f),
    float2(0.7418533f, -0.6667366f),
    float2(0.3492522f, 0.5924662f),
    float2(0.5679897f, 0.5343465f),
    float2(0.5663417f, 0.7708698f),
    float2(0.7375497f, 0.6691415f),
    float2(0.2271994f, -0.6163502f),
    float2(0.2312844f, 0.8725659f),
    float2(0.4216993f, 0.9002838f),
    float2(0.4262091f, -0.9013284f),
    float2(0.2001408f, -0.808381f),
    float2(0.149394f, 0.6650763f),
    float2(-0.09640376f, 0.9843736f),
    float2(0.7682328f, -0.07273844f),
    float2(0.04146584f, 0.8313184f),
    float2(0.9705266f, -0.1143304f),
    float2(0.9670017f, 0.1293385f),
    float2(0.9015037f, -0.3306949f),
    float2(-0.5085648f, 0.7534177f),
    float2(0.9055501f, 0.3758393f),
    float2(0.7599946f, 0.1809109f),
    float2(-0.2483695f, 0.7942952f),
    float2(-0.4241052f, 0.5581087f),
    float2(-0.1020106f, 0.6724468f)
};

float2 RotateVec2(float2 v, float angle)
{
    float s = sin(angle);
    float c = cos(angle);

    return float2(v.x * c + v.y * s, -v.x * s + v.y * c);
}

bool IsShadowOccluded(float receiverDepth, float sampleDepth)
{
#if defined(UNITY_REVERSED_Z)
    return sampleDepth > receiverDepth;
#else
    return sampleDepth < receiverDepth;
#endif
}

float ShadowDistanceFromNear(float depth, float depthRange)
{
#if defined(UNITY_REVERSED_Z)
    return (1.0 - depth) * depthRange;
#else
    return depth * depthRange;
#endif
}

// Receiver bias is in world units, independent of cascade depth range and Z convention.
float BiasedShadowDepth(float depth, float worldBias, float halfDepthRange)
{
    float bias = max(worldBias, 0.0) / max(2.0 * halfDepthRange, 1e-5);
#if defined(UNITY_REVERSED_Z)
    return depth + bias;
#else
    return depth - bias;
#endif
}

float2 GetAverageBlockerDepth(float2 sampleUV, sampler2D shadowMap, float curDepth, float searchWidth, float rotateAngle)
{
    float averageDepth = 0.0;
    int count = 0;
    for (int i = 0; i < N_SAMPLE; ++i)
    {
        float2 uvo = sampleUV + RotateVec2(poissonDisk[i], rotateAngle) * searchWidth;
        if (any(uvo < 0.0) || any(uvo > 1.0)) continue;
        float sampleDepth = tex2D(shadowMap, uvo).r;
        if (IsShadowOccluded(curDepth, sampleDepth))
        {
            averageDepth += sampleDepth;
            count += 1;
        }
    }
    if (count == 0) return float2(0.0, 0.0);
    return float2(averageDepth / count, count);
}

float2 ShadowClipToUV(float2 ndc)
{
    float2 uv = ndc * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    return uv;
}

float PCSSFilterRadius(float receiverDepth, float blockerDepth, float depthRange,
    float filterRadius, float orthoWidth)
{
    // Depth24 can quantize a near-plane blocker to distance zero. Keep both
    // the denominator and UV radius finite, including that endpoint.
    float minDepth = max(depthRange / 16777215.0, 1e-4);
    float penumbra = max(receiverDepth - blockerDepth, 0.0) / max(blockerDepth, minDepth);
    return saturate(penumbra * max(filterRadius, 0.0) / max(orthoWidth, 1e-5));
}

float PCSS(float4 pW, sampler2D shadowMap, float4x4 shadowVPMatrix,
    float orthoWidth, float orthoDistance, float shadowMapResolution, float rotateAngle,
    float pcssSearchRadius, float pcssFilterRadius, float worldDepthBias)
{
    float4 shadowNDC = mul(shadowVPMatrix, pW);
    shadowNDC /= shadowNDC.w;
    float curDepth = ClipDepthToDeviceDepth(shadowNDC.z);
    float2 sampleUV = ShadowClipToUV(shadowNDC.xy);
    if (any(sampleUV < 0.0) || any(sampleUV > 1.0) || curDepth < 0.0 || curDepth > 1.0) return 1.0;
    curDepth = BiasedShadowDepth(curDepth, worldDepthBias, orthoDistance);
    float searchWidth = saturate(max(pcssSearchRadius, 0.0) / max(orthoWidth, 1e-5));
    float2 blocker = GetAverageBlockerDepth(sampleUV, shadowMap, curDepth, searchWidth, rotateAngle);
    if (blocker.y < 1.0) return 1.0;
    float depthRange = 2.0 * orthoDistance;
    float receiverDepth = ShadowDistanceFromNear(curDepth, depthRange);
    float blockerDepth = ShadowDistanceFromNear(blocker.x, depthRange);
    float radius = PCSSFilterRadius(receiverDepth, blockerDepth, depthRange, pcssFilterRadius, orthoWidth);
    float shadow = 0.0;
    for (int i = 0; i < N_SAMPLE; ++i)
    {
        float2 uvo = sampleUV + RotateVec2(poissonDisk[i], rotateAngle) * radius;
        if (any(uvo < 0.0) || any(uvo > 1.0)) continue;
        if (IsShadowOccluded(curDepth, tex2D(shadowMap, uvo).r)) shadow += 1.0;
    }
    return 1.0 - shadow / N_SAMPLE;
}

float HardShadow(float3 pW, sampler2D shadowMap, float4x4 shadowVPMatrix,
    float worldDepthBias, float halfDepthRange)
{
    float4 shadowNDC = mul(shadowVPMatrix, float4(pW, 1.0));
    shadowNDC /= shadowNDC.w;
    float2 sampleUV = ShadowClipToUV(shadowNDC.xy);
    float curDepth = ClipDepthToDeviceDepth(shadowNDC.z);
    if (any(sampleUV < 0.0) || any(sampleUV > 1.0) || curDepth < 0.0 || curDepth > 1.0) return 1.0;
    curDepth = BiasedShadowDepth(curDepth, worldDepthBias, halfDepthRange);
    return IsShadowOccluded(curDepth, tex2D(shadowMap, sampleUV).r) ? 0.0 : 1.0;
}
/**
======= Shadow Caster Region =======
**/

struct ShadowCasterAttributes
{
    float4 pO   : POSITION;
};

struct ShadowCasterVaryings
{
    float4 pH   : SV_POSITION;
};

ShadowCasterVaryings ShadowCasterVertex(ShadowCasterAttributes input)
{
    ShadowCasterVaryings output;
    output.pH = mul(_ShadowCasterVP, mul(unity_ObjectToWorld, float4(input.pO.xyz, 1.0)));
    return output;
}

half4 ShadowCasterFragment(ShadowCasterVaryings input) : SV_Target
{
    return 0;
}
#endif