#ifndef MAX_SHADOW_INCLUDED
#define MAX_SHADOW_INCLUDED
#include "./LightInput.hlsl"
#include "./ShadowInput.hlsl"
#include "./SpaceTransform.hlsl"

// Frozen pre-reconstruction kernel, compiled only by the Editor comparison probe.
#if defined(MAX_PCSS_LEGACY_VOGEL)
static const float2 kPcssDisk[112] = {
    // Complete 16-tap disk.
    float2(0.176776695f, 0.000000000f),
    float2(-0.225772188f, 0.206825818f),
    float2(0.034558052f, -0.393771179f),
    float2(0.284571220f, 0.371172764f),
    float2(-0.522223187f, -0.092373929f),
    float2(0.494695392f, -0.314684715f),
    float2(-0.165465927f, 0.615525001f),
    float2(-0.315561468f, -0.607594404f),
    float2(0.684642162f, 0.250030219f),
    float2(-0.712256086f, 0.294008958f),
    float2(0.343354499f, -0.733728620f),
    float2(0.253730241f, 0.808931990f),
    float2(-0.764745892f, -0.443185876f),
    float2(0.897133984f, -0.197232390f),
    float2(-0.547506905f, 0.778772232f),
    float2(-0.126486773f, -0.976089697f),
    // Complete 32-tap disk.
    float2(0.125000000f, 0.000000000f),
    float2(-0.159645045f, 0.146247939f),
    float2(0.024436233f, -0.278438271f),
    float2(0.201222239f, 0.262458779f),
    float2(-0.369267557f, -0.065318231f),
    float2(0.349802466f, -0.222515696f),
    float2(-0.117002079f, 0.435241902f),
    float2(-0.223135654f, -0.429634123f),
    float2(0.484115115f, 0.176798064f),
    float2(-0.503641109f, 0.207895728f),
    float2(0.242788294f, -0.518824483f),
    float2(0.179414374f, 0.572001296f),
    float2(-0.540757006f, -0.313379738f),
    float2(0.634369523f, -0.139464360f),
    float2(-0.387145845f, 0.550675126f),
    float2(-0.089439655f, -0.690199644f),
    float2(0.549071757f, 0.462758258f),
    float2(-0.738878471f, 0.030554945f),
    float2(0.538955125f, -0.536332334f),
    float2(-0.036058186f, 0.779791515f),
    float2(-0.512817532f, -0.614526793f),
    float2(0.812359593f, 0.109301834f),
    float2(-0.688310641f, 0.478908616f),
    float2(0.188086057f, -0.836061382f),
    float2(0.435033266f, 0.759191055f),
    float2(-0.850448410f, -0.271316239f),
    float2(0.826102401f, -0.381680263f),
    float2(-0.357888202f, 0.855155562f),
    float2(-0.319407334f, -0.888033758f),
    float2(0.849908635f, 0.446688159f),
    float2(-0.944034646f, 0.248844503f),
    float2(0.536595808f, -0.834529771f),
    // Complete 64-tap disk.
    float2(0.088388348f, 0.000000000f),
    float2(-0.112886094f, 0.103412909f),
    float2(0.017279026f, -0.196885589f),
    float2(0.142285610f, 0.185586382f),
    float2(-0.261111594f, -0.046186964f),
    float2(0.247347696f, -0.157342357f),
    float2(-0.082732964f, 0.307762501f),
    float2(-0.157780734f, -0.303797202f),
    float2(0.342321081f, 0.125015110f),
    float2(-0.356128043f, 0.147004479f),
    float2(0.171677249f, -0.366864310f),
    float2(0.126865120f, 0.404465995f),
    float2(-0.382372946f, -0.221592938f),
    float2(0.448566992f, -0.098616195f),
    float2(-0.273753452f, 0.389386116f),
    float2(-0.063243386f, -0.488044848f),
    float2(0.388252363f, 0.327219502f),
    float2(-0.522465978f, 0.021605608f),
    float2(0.381098824f, -0.379244231f),
    float2(-0.025496988f, 0.551395868f),
    float2(-0.362616754f, -0.434536062f),
    float2(0.574424977f, 0.077288068f),
    float2(-0.486709121f, 0.338639530f),
    float2(0.132996927f, -0.591184673f),
    float2(0.307614972f, 0.536829143f),
    float2(-0.601357838f, -0.191849552f),
    float2(0.584142610f, -0.269888702f),
    float2(-0.253065174f, 0.604686297f),
    float2(-0.225855092f, -0.627934692f),
    float2(0.600976159f, 0.315856226f),
    float2(-0.667533300f, 0.175959636f),
    float2(0.379430535f, -0.590101660f),
    float2(0.120699093f, 0.702313483f),
    float2(-0.572007808f, -0.442994998f),
    float2(0.731701930f, -0.060620009f),
    float2(-0.505760271f, 0.546712034f),
    float2(0.003684028f, -0.755181387f),
    float2(0.514304945f, 0.566946138f),
    float2(-0.772294866f, -0.071576115f),
    float2(0.625787308f, -0.474950256f),
    float2(-0.142380569f, 0.782649522f),
    float2(-0.428883941f, -0.681539482f),
    float2(0.785920133f, 0.215388125f),
    float2(-0.733485521f, 0.376412659f),
    float2(0.289861676f, -0.781852102f),
    float2(0.317911469f, 0.780941610f),
    float2(-0.770263923f, -0.365042448f),
    float2(0.823263301f, -0.253820875f),
    float2(-0.440156530f, 0.751049086f),
    float2(-0.184643240f, -0.859851367f),
    float2(0.724177356f, 0.514421672f),
    float2(-0.890157361f, 0.110938598f),
    float2(0.587054262f, -0.689695435f),
    float2(0.033319900f, 0.913688833f),
    float2(-0.647726957f, -0.657276418f),
    float2(0.930014131f, 0.047552248f),
    float2(-0.724323114f, 0.598471827f),
    float2(0.130975323f, -0.938766725f),
    float2(0.542204890f, 0.787449273f),
    float2(-0.939649022f, -0.216211044f),
    float2(0.845936750f, -0.479273945f),
    float2(-0.302491897f, 0.932435602f),
    float2(-0.410097094f, -0.899101147f),
    float2(0.916975868f, 0.389027966f),
};
#endif

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

float BiasedShadowDepth(float depth, float worldBias, float halfDepthRange)
{
    float bias = max(worldBias, 0.0) / max(2.0 * halfDepthRange, 1e-5);
#if defined(UNITY_REVERSED_Z)
    return depth + bias;
#else
    return depth - bias;
#endif
}

float2 ShadowClipToUV(float2 ndc)
{
    float2 uv = ndc * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    return uv;
}

float PCSSFilterRadius(float receiverDepth, float blockerDepth, float tanAngularRadius,
    float maxPenumbraRadius, float orthoWidth)
{
    // Directional light: the shadow camera's arbitrary near-plane origin cancels.
    // This is a disk-radius approximation, capped in world units for finite sampling.
    float separation = max(receiverDepth - blockerDepth, 0.0);
    float radiusWorld = min(separation * max(tanAngularRadius, 0.0), max(maxPenumbraRadius, 0.0));
    return saturate(radiusWorld / max(orthoWidth, 1e-5));
}

#if defined(MAX_PCSS_LEGACY_VOGEL)
float2 PcssLegacyDiskSample(int index, int sampleCount, float2 rotation)
{
    int offset = sampleCount <= 16 ? 0 : sampleCount <= 32 ? 16 : 48;
    float2 v = kPcssDisk[offset + index];
    return float2(v.x * rotation.x - v.y * rotation.y, v.x * rotation.y + v.y * rotation.x);
}
#endif

float2 PcssSobolSquare(int index, float2 phase)
{
    // Two-dimensional base-2 digital net. A digital shift randomizes the entire
    // point set instead of only rotating a fixed disk. 6 bits cover 16/32/64 taps.
    uint sampleIndex = (uint)index;
    uint x = reversebits(sampleIndex);
    uint y = 0u;
    uint direction = 0x80000000u;
    [unroll] for (int bit = 0; bit < 6; ++bit)
    {
        if ((sampleIndex & (1u << bit)) != 0u) y ^= direction;
        direction ^= direction >> 1;
    }
    x ^= (uint)(phase.x * 65536.0) << 16;
    y ^= (uint)(phase.y * 65536.0) << 16;
    // Keep the conversion away from both endpoints, including float rounding.
    return (float2(x >> 9, y >> 9) + 0.5) * (1.0 / 8388608.0);
}

float2 PcssSquareToDisk(float2 square)
{
    // Concentric square-to-disk mapping preserves the stratification near edges.
    float2 p = square * 2.0 - 1.0;
    if (abs(p.x) + abs(p.y) < 1e-7) return float2(0, 0);
    float radius, angle;
    if (abs(p.x) > abs(p.y))
    {
        radius = p.x;
        angle = 0.7853981633974483 * (p.y / p.x);
    }
    else
    {
        radius = p.y;
        angle = 1.5707963267948966 - 0.7853981633974483 * (p.x / p.y);
    }
    float sine, cosine;
    sincos(angle, sine, cosine);
    return radius * float2(cosine, sine);
}

float2 PcssDiskSample(int index, int sampleCount, float2 phase)
{
    return PcssSquareToDisk(PcssSobolSquare(index, phase));
}

#if defined(MAX_PCSS_LEGACY_VOGEL)
    #define PcssDiskSample PcssLegacyDiskSample
#endif

float2 GetAverageBlockerDepth(float2 sampleUV, Texture2D shadowMap, float curDepth, float searchWidth, float2 rotation, float2 receiverGradient)
{
    float averageDepth = 0.0;
    int count = 0;
    [loop] for (int i = 0; i < _PcssBlockerSamples; ++i)
    {
        float2 uv = i == 0 ? sampleUV : sampleUV + PcssDiskSample(i, _PcssBlockerSamples, rotation) * searchWidth;
        float correction = dot(receiverGradient, uv - sampleUV);
        float sampleDepth = shadowMap.SampleLevel(sampler_PointClamp, uv, 0).r;
        if (IsShadowOccluded(curDepth + correction, sampleDepth)) { averageDepth += sampleDepth - correction; ++count; }
    }
    if (count == 0) return float2(0.0, 0.0);
    return float2(averageDepth / count, count);
}

float PCSS(float4 pW, Texture2D shadowMap, SamplerComparisonState shadowSampler, float4x4 shadowVPMatrix,
    float orthoWidth, float halfDepthRange, float2 rotation, float maxPenumbraRadius, float worldDepthBias, float2 receiverGradient)
{
    float4 shadowNDC = mul(shadowVPMatrix, pW);
    shadowNDC /= shadowNDC.w;
    float curDepth = ClipDepthToDeviceDepth(shadowNDC.z);
    float2 uv = ShadowClipToUV(shadowNDC.xy);

    if (any(uv < 0.0) || any(uv > 1.0) || curDepth < 0.0 || curDepth > 1.0) return 1.0;
    float depthRange = 2.0 * halfDepthRange;
    float receiverDepth = ShadowDistanceFromNear(curDepth, depthRange);
    curDepth = BiasedShadowDepth(curDepth, worldDepthBias, halfDepthRange);
    float searchWidth = PCSSFilterRadius(depthRange, 0.0, _ShadowLightTanAngularRadius, maxPenumbraRadius, orthoWidth);
    if (searchWidth <= 0.0)
        return shadowMap.SampleCmpLevelZero(shadowSampler, uv, curDepth);
    float2 blocker = GetAverageBlockerDepth(uv, shadowMap, curDepth, searchWidth, rotation, receiverGradient);
    if (blocker.y < 1.0) return 1.0;
    float blockerDepth = ShadowDistanceFromNear(blocker.x, depthRange);
    float radius = PCSSFilterRadius(receiverDepth, blockerDepth, _ShadowLightTanAngularRadius, maxPenumbraRadius, orthoWidth);
    if (radius <= 0.0) return shadowMap.SampleCmpLevelZero(shadowSampler, uv, curDepth);
    float visibility = 0.0;
    [loop] for (int i = 0; i < _PcssFilterSamples; ++i)
    {
        float2 sampleUV = uv + PcssDiskSample(i, _PcssFilterSamples, rotation) * radius;
        float correction = dot(receiverGradient, sampleUV - uv);
        // Bilinear depth comparison smooths sub-texel edges without blurring geometry boundaries.
        visibility += shadowMap.SampleCmpLevelZero(shadowSampler, sampleUV, curDepth + correction);
    }
    return visibility / _PcssFilterSamples;
}

float ShadowCascadeBlendWeight(float depth, float boundary, float width)
{
    if (width <= 0.0) return 0.0;
    float t = saturate((depth - boundary + width) / width);
    return t * t * (3.0 - 2.0 * t);
}

float ShadowDistanceFadeWeight(float depth, float start, float end)
{
    float t = saturate((depth - start) / max(end - start, 1e-5));
    return t * t * (3.0 - 2.0 * t);
}

// Device-depth change per shadow UV on the geometric receiver plane. Transform
// world-space derivatives as DIRECTIONS: subtracting projected depths loses
// precision when the shadow camera is far from a close-up receiver.
float2 ShadowReceiverPlaneGradient(float4x4 shadowVP, float3 receiverDx, float3 receiverDy)
{
    float3 dx = mul(shadowVP, float4(receiverDx / max(length(receiverDx), 1e-12), 0)).xyz;
    float3 dy = mul(shadowVP, float4(receiverDy / max(length(receiverDy), 1e-12), 0)).xyz;
    dx.xy *= 0.5; dy.xy *= 0.5;
#if UNITY_UV_STARTS_AT_TOP
    dx.y = -dx.y; dy.y = -dy.y;
#endif
#if !defined(UNITY_REVERSED_Z)
    dx.z /= 1.0 - UNITY_NEAR_CLIP_VALUE;
    dy.z /= 1.0 - UNITY_NEAR_CLIP_VALUE;
#endif
    float determinant = dx.x * dy.y - dx.y * dy.x;
    float scale = max(length(dx.xy) * length(dy.xy), 1e-20);
    float2 gradient = float2(0, 0);
    if (abs(determinant) > 1e-5 * scale)
    {
        gradient = float2(dx.z * dy.y - dy.z * dx.y, dx.x * dy.z - dy.x * dx.z) / determinant;
        if (!all(isfinite(gradient))) gradient = float2(0, 0);
    }
    return gradient;
}

void ShadowReceiverDerivatives(float3 positionWS, out float3 receiverDx, out float3 receiverDy)
{
    // Call before background returns or cascade divergence. This is a local
    // plane fit, not an average of neighboring shadow visibility.
    receiverDx = ddx_fine(positionWS);
    receiverDy = ddy_fine(positionWS);
    float distance = max(length(positionWS - _WorldSpaceCameraPos), 0.01);
    float orthoFootprint = max(unity_OrthoParams.x / _ScreenParams.x, unity_OrthoParams.y / _ScreenParams.y);
    float limit = max(0.05 * distance, 16 * orthoFootprint * unity_OrthoParams.w);
    if (!all(isfinite(receiverDx)) || !all(isfinite(receiverDy)) ||
        max(length(receiverDx), length(receiverDy)) > limit)
    {
        // Discontinuous depth / silhouette: retain the existing constant bias.
        receiverDx = float3(0, 0, 0); receiverDy = float3(0, 0, 0);
    }
}

float SampleDirectionalCascade(int cascade, float3 positionWS, float3 normalWS, float biasScale, float2 rotation, float3 receiverDx, float3 receiverDy)
{
    [branch] if (cascade == 0)
        return PCSS(float4(positionWS + normalWS * _ShadingPointNormalBias0, 1), _MainShadowMap0, sampler_MainShadowMap0,
            _ShadowVPMatrix0, _OrthoWidth0, _ShadowDepthHalfRange0, rotation, _PcssMaxRadius0, _DepthNormalBias0 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix0, receiverDx, receiverDy));
    [branch] if (cascade == 1)
        return PCSS(float4(positionWS + normalWS * _ShadingPointNormalBias1, 1), _MainShadowMap1, sampler_MainShadowMap1,
            _ShadowVPMatrix1, _OrthoWidth1, _ShadowDepthHalfRange1, rotation, _PcssMaxRadius1, _DepthNormalBias1 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix1, receiverDx, receiverDy));
    [branch] if (cascade == 2)
        return PCSS(float4(positionWS + normalWS * _ShadingPointNormalBias2, 1), _MainShadowMap2, sampler_MainShadowMap2,
            _ShadowVPMatrix2, _OrthoWidth2, _ShadowDepthHalfRange2, rotation, _PcssMaxRadius2, _DepthNormalBias2 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix2, receiverDx, receiverDy));
    return PCSS(float4(positionWS + normalWS * _ShadingPointNormalBias3, 1), _MainShadowMap3, sampler_MainShadowMap3,
        _ShadowVPMatrix3, _OrthoWidth3, _ShadowDepthHalfRange3, rotation, _PcssMaxRadius3, _DepthNormalBias3 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix3, receiverDx, receiverDy));
}

float4 _MaxTemporalPhase; // enabled, base-2/base-3 temporal phase, reserved
float MainLightShadow(float3 positionWS, float3 normalWS, float3 receiverDx, float3 receiverDy)
{
    if (_MainLightShadowsAvailable < 0.5 || _CascadeDepthRange.z <= 0.0) return 1.0;
    float nDotL = dot(normalWS, normalize(_DirectionalLightDirection.xyz));
    if (nDotL <= 0.0) return 1.0; // Direct diffuse/specular already have zero incident cosine.
    float eyeDepth = -mul(_ShadowReceiverView, float4(positionWS, 1)).z;
    if (eyeDepth < _CascadeDepthRange.x || eyeDepth >= _CascadeDepthRange.y) return 1.0;
    float depth = saturate((eyeDepth - _CascadeDepthRange.x) * _CascadeDepthRange.z);
    // Fixed world phase without TAA; temporal digital shifts when full-frame TAA accumulates.
    float2 lightXY = mul(_ShadowNoiseWorldToLight, float4(positionWS, 1)).xy;
    float2 noiseUV = lightXY / max(_ShadowNoiseWorldTexelSize * _ShadowNoiseTextureSize.xy, 1e-5);
    // Random rotation is data: interpolation creates coherent angular contours.
    // Explicit point/repeat sampling also protects against a replacement texture's filter settings.
    float noise = _NoiseTexture.SampleLevel(sampler_PointRepeat, frac(noiseUV), 0).r;
#if defined(MAX_PCSS_LEGACY_VOGEL)
    float angle = noise * 6.28318530718;
    float sine, cosine;
    sincos(angle, sine, cosine);
    float2 rotation = float2(cosine, sine);
#else
    // Two decorrelated blue-noise shifts, fixed in world space without TAA.
    // Avoid replacing the second dimension with white noise at close range.
    float noiseY = _NoiseTexture.SampleLevel(sampler_PointRepeat,
        frac(noiseUV + float2(17, 37) / max(_ShadowNoiseTextureSize.xy, 1.0)), 0).r;
    float2 rotation = (float2(noise, noiseY) * 255.0 + 0.5) / 256.0;
    if (_MaxTemporalPhase.x > 0.5) rotation = frac(rotation + _MaxTemporalPhase.yz);
#endif
    int cascade = depth <= _Split0 ? 0 : depth <= _Split1 ? 1 : depth <= _Split2 ? 2 : 3;
    float biasScale = 2.0 - saturate(nDotL);
    float visibility = SampleDirectionalCascade(cascade, positionWS, normalWS, biasScale, rotation, receiverDx, receiverDy);
    if (cascade < 3)
    {
        float boundary = cascade == 0 ? _Split0 : cascade == 1 ? _Split1 : _Split2;
        float width = cascade == 0 ? _CascadeBlendWidths.x : cascade == 1 ? _CascadeBlendWidths.y : _CascadeBlendWidths.z;
        float blend = ShadowCascadeBlendWeight(depth, boundary, width);
        [branch] if (blend > 0.0)
            visibility = lerp(visibility, SampleDirectionalCascade(cascade + 1, positionWS, normalWS, biasScale, rotation, receiverDx, receiverDy), blend);
    }
    return lerp(visibility, 1.0, ShadowDistanceFadeWeight(eyeDepth, _ShadowDistanceFade.x, _ShadowDistanceFade.y));
}

// Editor probes also use the production plane correction. Runtime supplies
// derivatives explicitly before fragment background control flow diverges.
float MainLightShadow(float3 positionWS, float3 normalWS)
{
    float3 dx, dy;
    ShadowReceiverDerivatives(positionWS, dx, dy);
    return MainLightShadow(positionWS, normalWS, dx, dy);
}

#if defined(MAX_PCSS_DIAGNOSTIC_QUAD)
// Historical spatial-reuse comparison, Editor probes only. Runtime uses raw PCSS.
float4 PcssQuadReadX(float4 value, float2 pixelPosition)
{
    float direction = ((uint)pixelPosition.x & 1u) == 0u ? 1.0 : -1.0;
    return value + direction * ddx_fine(value);
}

float4 PcssQuadReadY(float4 value, float2 pixelPosition)
{
    float direction = ((uint)pixelPosition.y & 1u) == 0u ? 1.0 : -1.0;
    return value + direction * ddy_fine(value);
}

float PcssNeighborWeight(float3 position, float3 normal, float key, float valid,
    float3 neighborPosition, float3 neighborNormal, float neighborKey, float neighborValid)
{
    if (valid < 0.5 || neighborValid < 0.5 || abs(key - neighborKey) > 0.1) return 0.0;
    float normalWeight = saturate((dot(normal, neighborNormal) - 0.95) / 0.05);
    float3 offset = neighborPosition - position;
    float planeError = max(abs(dot(offset, normal)), abs(dot(offset, neighborNormal)));
    // A subpixel coplanar neighbor is useful. A parallel but offset surface is not.
    float tolerance = max(0.0005, min(0.01, 0.1 * length(offset)));
    float planeWeight = saturate(1.0 - planeError / tolerance);
    return normalWeight * planeWeight * planeWeight;
}

float PcssFilterQuad(float3 positionWS, float3 normalWS, float2 pixelPosition,
    float key, float valid, float visibility, float width)
{
    float4 center = float4(positionWS, visibility);
    float4 normalKey = float4(normalWS, key);
    float4 metadata = float4(valid, 0, 0, 0);
    float4 px = PcssQuadReadX(center, pixelPosition), py = PcssQuadReadY(center, pixelPosition);
    float4 pxy = PcssQuadReadY(px, pixelPosition);
    float4 nx = PcssQuadReadX(normalKey, pixelPosition), ny = PcssQuadReadY(normalKey, pixelPosition);
    float4 nxy = PcssQuadReadY(nx, pixelPosition);
    float4 mx = PcssQuadReadX(metadata, pixelPosition), my = PcssQuadReadY(metadata, pixelPosition);
    float4 mxy = PcssQuadReadY(mx, pixelPosition);
    float wx = PcssNeighborWeight(positionWS, normalWS, key, valid, px.xyz, nx.xyz, nx.w, mx.x);
    float wy = PcssNeighborWeight(positionWS, normalWS, key, valid, py.xyz, ny.xyz, ny.w, my.x);
    float wxy = PcssNeighborWeight(positionWS, normalWS, key, valid, pxy.xyz, nxy.xyz, nxy.w, mxy.x);
    float average = (visibility + wx * px.w + wy * py.w + wxy * pxy.w) / (1.0 + wx + wy + wxy);
    float2 lightXY = mul(_ShadowNoiseWorldToLight, float4(positionWS, 1)).xy;
    float footprint = max(length(ddx_fine(lightXY)), length(ddy_fine(lightXY)));
    float strength = 0.75 * saturate(1.0 - footprint * _ShadowTexelResolution / max(width, 1e-5));
    return lerp(visibility, average, strength);
}

float MainLightShadowFiltered(float3 positionWS, float3 normalWS, float2 pixelPosition, float surfaceValid)
{
    // Invoke once for EVERY fragment, before background returns/discard. PCSS may
    // diverge internally; quad derivatives below run only after it reconverges.
    float eyeDepth = -mul(_ShadowReceiverView, float4(positionWS, 1)).z;
    float depth = saturate((eyeDepth - _CascadeDepthRange.x) * _CascadeDepthRange.z);
    int cascade = depth <= _Split0 ? 0 : depth <= _Split1 ? 1 : depth <= _Split2 ? 2 : 3;
    float boundary = cascade == 0 ? _Split0 : cascade == 1 ? _Split1 : _Split2;
    float band = cascade == 0 ? _CascadeBlendWidths.x : cascade == 1 ? _CascadeBlendWidths.y : _CascadeBlendWidths.z;
    float key = cascade * 2.0 + (cascade < 3 && ShadowCascadeBlendWeight(depth, boundary, band) > 0.0 ? 1.0 : 0.0);
    float valid = surfaceValid > 0.5 && _MainLightShadowsAvailable > 0.5 && _CascadeDepthRange.z > 0.0 &&
        eyeDepth >= _CascadeDepthRange.x && eyeDepth < _CascadeDepthRange.y &&
        dot(normalWS, _DirectionalLightDirection.xyz) > 0.0 ? 1.0 : 0.0;
    float visibility = 1.0;
    if (valid > 0.5) visibility = MainLightShadow(positionWS, normalWS);
    float width = cascade == 0 ? _OrthoWidth0 : cascade == 1 ? _OrthoWidth1 : cascade == 2 ? _OrthoWidth2 : _OrthoWidth3;
    return PcssFilterQuad(positionWS, normalWS, pixelPosition, key, valid, visibility, width);
}

#endif
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
