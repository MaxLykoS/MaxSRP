#ifndef MAX_SHADOW_INCLUDED
#define MAX_SHADOW_INCLUDED
#include "../Lighting/LightInput.hlsl"
#include "./ShadowInput.hlsl"
#include "../Core/SpaceTransform.hlsl"
#include "../Core/MaxViewDirection.hlsl"

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

// UE 5.8.4 reference: ShadowFilteringCommon.ush, Manual5x5PCF and
// CalculateShadowVisibilityTransmittanceFactor (opaque). Desktop quality 5
// and MobileLightingCommon.ush quality 3 both use this 5x5 bilinear box.
// The 6x6 footprint is evaluated in nine 2x2 gathers, with each texel's
// actual location used for the receiver-plane correction.
float FilterShadowBox5x5(Texture2D<float> shadowMap, SamplerState shadowSampler, float2 uv,
    float receiverDepth, float comparisonScale, float2 receiverGradient, float4 tile, float resolution)
{
    float2 center = uv * resolution - 0.5;
    float2 fraction = frac(center);
    float2 first = floor(center) - 2.0;
    float2 gradientPerTexel = receiverGradient / resolution;
    const float2 nextTexel = float2(1.0, 1.0);

    float visibility = 0.0;
    [unroll] for (int y = 0; y < 6; y += 2)
    [unroll] for (int x = 0; x < 6; x += 2)
    {
        float2 pair = first + float2(x, y);
        float2 gatherOrigin = clamp(pair, 0.0, resolution - 2.0);
        float2 atlasUV = tile.zw + (gatherOrigin + 1.0) * (tile.xy / resolution);
        float4 depths = shadowMap.GatherRed(shadowSampler, atlasUV);

        // Distribute the pair's weights onto the texels it actually names.
        // At a tile edge, replicate the edge texel within the tile.
        float2 firstWeight = 1.0 - fraction * float2(x == 0, y == 0);
        float2 secondWeight = 1.0 - (1.0 - fraction) * float2(x == 4, y == 4);
        float2 weight1 = firstWeight * (clamp(pair, 0.0, resolution - 1.0) - gatherOrigin)
            + secondWeight * (clamp(pair + nextTexel, 0.0, resolution - 1.0) - gatherOrigin);
        float2 weight0 = firstWeight + secondWeight - weight1;
        // Gather order: lower left, lower right, upper right, upper left.
        float4 weights = float4(weight0.x * weight1.y, weight1.x * weight1.y,
            weight1.x * weight0.y, weight0.x * weight0.y);
        float baseDepth = receiverDepth + dot(gradientPerTexel, gatherOrigin - center);
        float4 compareDepths = saturate(baseDepth + float4(gradientPerTexel.y,
            gradientPerTexel.x + gradientPerTexel.y, gradientPerTexel.x, 0.0));
#if defined(UNITY_REVERSED_Z)
        float4 lit = saturate(1.0 + (compareDepths - depths) * comparisonScale);
#else
        float4 lit = saturate(1.0 + (depths - compareDepths) * comparisonScale);
#endif
        visibility += dot(weights, lit);
    }
    return saturate(visibility * (1.0 / 25.0));
}

float FilterDirectionalShadow(float4 pW, Texture2D<float> shadowMap, SamplerState shadowSampler, float4x4 shadowVP,
    float orthoWidth, float halfDepthRange, float receiverTransitionScale, float worldDepthBias,
    float2 receiverGradient, float4 tile, float resolution)
{
    float4 clip = mul(shadowVP, pW);
    float3 ndc = clip.xyz / clip.w;
    float deviceDepth = ClipDepthToDeviceDepth(ndc.z);
    float2 uv = ShadowClipToUV(ndc.xy);
    if (any(uv < 0.0) || any(uv > 1.0) || deviceDepth < 0.0 || deviceDepth > 1.0) return 1.0;

    // UE 5.8.4 ShadowRendering.cpp, ComputeTransitionSize: CSMDepthBias *
    // radius / resolution * light ShadowBias. Defaults are 10 and 0.5.
    // orthoWidth is the diameter.
    float transitionDepth = max(2.5 * orthoWidth / (resolution * 2.0 * halfDepthRange), 1e-5);
    float receiverDepth = BiasedShadowDepth(deviceDepth, worldDepthBias, halfDepthRange);
    return FilterShadowBox5x5(shadowMap, shadowSampler, uv, receiverDepth,
        receiverTransitionScale / transitionDepth, receiverGradient, tile, resolution);
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
// Microsoft Cascaded Shadow Maps, "Calculating a Per-Texel Depth Bias with DDX and DDY for Large PCFs".
// https://learn.microsoft.com/windows/win32/dxtecharts/cascaded-shadow-maps#calculating-a-per-texel-depth-bias-with-ddx-and-ddy-for-large-pcfs
float2 ShadowReceiverPlaneGradient(float4x4 shadowVP, float3 receiverDx, float3 receiverDy)
{
    float3 worldDx = receiverDx / max(length(receiverDx), 1e-12);
    float3 worldDy = receiverDy / max(length(receiverDy), 1e-12);
    float3 receiverNormal = cross(worldDx, worldDy);
    // Orthographic shadow depth has a world-space light axis in its third row.
    // Within about 0.57 degrees of grazing, a finite plane gradient can still
    // extrapolate hundreds of metres across the filter and reveal real blockers.
    // Use only the existing normal/depth bias there, independent of cascade scale.
    float3 depthAxis = shadowVP[2].xyz;
    float normalAxisLengthSquared = dot(receiverNormal, receiverNormal) * dot(depthAxis, depthAxis);
    float normalDotAxis = dot(receiverNormal, depthAxis);
    const float minimumReceiverCosine = 0.01;
    if (!isfinite(normalAxisLengthSquared) || normalAxisLengthSquared <= 1e-20 ||
        normalDotAxis * normalDotAxis <= minimumReceiverCosine * minimumReceiverCosine * normalAxisLengthSquared)
        return float2(0, 0);

    float3 dx = mul(shadowVP, float4(worldDx, 0)).xyz;
    float3 dy = mul(shadowVP, float4(worldDy, 0)).xyz;
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

float SampleDirectionalCascade(int cascade, float3 positionWS, float3 normalWS, float biasScale, float transitionScale, float3 receiverDx, float3 receiverDy)
{
    [branch] if (cascade == 0)
        return FilterDirectionalShadow(float4(positionWS + normalWS * _ShadingPointNormalBias0, 1), _MainShadowAtlas, sampler_MainShadowAtlas,
            _ShadowVPMatrix0, _OrthoWidth0, _ShadowDepthHalfRange0, transitionScale, _DepthNormalBias0 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix0, receiverDx, receiverDy), _ShadowAtlasTile0, _ShadowResolution0);
    [branch] if (cascade == 1)
        return FilterDirectionalShadow(float4(positionWS + normalWS * _ShadingPointNormalBias1, 1), _MainShadowAtlas, sampler_MainShadowAtlas,
            _ShadowVPMatrix1, _OrthoWidth1, _ShadowDepthHalfRange1, transitionScale, _DepthNormalBias1 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix1, receiverDx, receiverDy), _ShadowAtlasTile1, _ShadowResolution1);
    [branch] if (cascade == 2)
        return FilterDirectionalShadow(float4(positionWS + normalWS * _ShadingPointNormalBias2, 1), _MainShadowAtlas, sampler_MainShadowAtlas,
            _ShadowVPMatrix2, _OrthoWidth2, _ShadowDepthHalfRange2, transitionScale, _DepthNormalBias2 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix2, receiverDx, receiverDy), _ShadowAtlasTile2, _ShadowResolution2);
    return FilterDirectionalShadow(float4(positionWS + normalWS * _ShadingPointNormalBias3, 1), _MainShadowAtlas, sampler_MainShadowAtlas,
        _ShadowVPMatrix3, _OrthoWidth3, _ShadowDepthHalfRange3, transitionScale, _DepthNormalBias3 * biasScale, ShadowReceiverPlaneGradient(_ShadowVPMatrix3, receiverDx, receiverDy), _ShadowAtlasTile3, _ShadowResolution3);
}

float3 ShadowReceiverBiasNormal(float3 positionWS, float3 receiverDx, float3 receiverDy)
{
    // Normal maps describe BRDF detail, not the receiver's geometric surface.
    // Using them for a world-space offset makes flat ground warp cast shadows.
    float3 dx = receiverDx / max(length(receiverDx), 1e-12);
    float3 dy = receiverDy / max(length(receiverDy), 1e-12);
    float3 normal = cross(dx, dy);
    float lengthSquared = dot(normal, normal);
    // At discontinuities/degenerate footprints use only the existing depth bias.
    if (!all(isfinite(normal)) || lengthSquared <= 1e-8) return float3(0, 0, 0);
    normal *= rsqrt(lengthSquared);
    return dot(normal, _WorldSpaceCameraPos - positionWS) < 0 ? -normal : normal;
}
float MainLightShadow(float3 positionWS, float3 normalWS, float3 receiverDx, float3 receiverDy, float3 receiverNormalWS)
{
    if (_MainLightShadowsAvailable < 0.5 || _CascadeDepthRange.z <= 0.0) return 1.0;
    float nDotL = dot(normalWS, normalize(_DirectionalLightDirection.xyz));
    if (nDotL <= 0.0) return 1.0; // Direct diffuse/specular already have zero incident cosine.
    float eyeDepth = -mul(_ShadowReceiverView, float4(positionWS, 1)).z;
    if (eyeDepth < _CascadeDepthRange.x || eyeDepth >= _CascadeDepthRange.y) return 1.0;
    float depth = saturate((eyeDepth - _CascadeDepthRange.x) * _CascadeDepthRange.z);
    // ShadowProjectionPixelShader.usf attenuates the deferred comparison scale
    // at grazing angles. MobileLightingCommon.ush uses the cascade transition.
    float transitionScale = lerp(lerp(0.1, 1.0, saturate(nDotL)), 1.0, _ShadowPcfMobile);
    int cascade = depth <= _Split0 ? 0 : depth <= _Split1 ? 1 : depth <= _Split2 ? 2 : 3;
    float3 biasNormal;
    float receiverNormalLengthSquared = dot(receiverNormalWS, receiverNormalWS);
    if (isfinite(receiverNormalLengthSquared) && receiverNormalLengthSquared > 1e-8)
    {
        // A geometry-pass normal supplies a plane even at a screen-space depth
        // discontinuity. The projection test can reject its gradient independently.
        biasNormal = receiverNormalWS * rsqrt(receiverNormalLengthSquared);
        if (dot(biasNormal, GetSurfaceViewDirection(positionWS)) < 0.0) biasNormal = -biasNormal;
        float3 axis = abs(biasNormal.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0);
        receiverDx = normalize(cross(axis, biasNormal));
        receiverDy = cross(biasNormal, receiverDx);
    }
    else
    {
        biasNormal = ShadowReceiverBiasNormal(positionWS, receiverDx, receiverDy);
    }
    float biasScale = 2.0 - saturate(dot(biasNormal, normalize(_DirectionalLightDirection.xyz)));
    float visibility = SampleDirectionalCascade(cascade, positionWS, biasNormal, biasScale, transitionScale, receiverDx, receiverDy);
    if (cascade < 3)
    {
        float boundary = cascade == 0 ? _Split0 : cascade == 1 ? _Split1 : _Split2;
        float width = cascade == 0 ? _CascadeBlendWidths.x : cascade == 1 ? _CascadeBlendWidths.y : _CascadeBlendWidths.z;
        float blend = ShadowCascadeBlendWeight(depth, boundary, width);
        [branch] if (blend > 0.0)
            visibility = lerp(visibility, SampleDirectionalCascade(cascade + 1, positionWS, biasNormal, biasScale, transitionScale, receiverDx, receiverDy), blend);
    }
    return lerp(visibility, 1.0, ShadowDistanceFadeWeight(eyeDepth, _ShadowDistanceFade.x, _ShadowDistanceFade.y));
}

float MainLightShadow(float3 positionWS, float3 normalWS, float3 receiverDx, float3 receiverDy)
{
    return MainLightShadow(positionWS, normalWS, receiverDx, receiverDy, 0.0);
}

// Editor probes also use the production plane correction. Runtime supplies
// derivatives explicitly before fragment background control flow diverges.
float MainLightShadow(float3 positionWS, float3 normalWS)
{
    float3 dx, dy;
    ShadowReceiverDerivatives(positionWS, dx, dy);
    return MainLightShadow(positionWS, normalWS, dx, dy);
}

struct ShadowCasterAttributes
{
    float4 pO : POSITION;
};

struct ShadowCasterVaryings
{
    float4 pH : SV_POSITION;
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
