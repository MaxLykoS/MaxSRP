// Copyright (c) 2019-2023, NVIDIA CORPORATION. All rights reserved.
// NVIDIA CORPORATION and its licensors retain all intellectual property and
// proprietary rights in and to this software, related documentation and any
// modifications thereto. Any use, reproduction, disclosure or distribution of
// this software and related documentation without an express license agreement
// from NVIDIA CORPORATION is strictly prohibited.
// This software contains source code provided by NVIDIA Corporation.
// Adapted from Tools/NativeGI/src/experimental_query.hlsl for baked TOD data.
// See Tools/NativeGI/ThirdParty/RTXGI-DDGI/License.txt for the upstream license.
#ifndef MAX_BAKED_SOLAR_PROBE_GI_INCLUDED
#define MAX_BAKED_SOLAR_PROBE_GI_INCLUDED

float4 _MaxSolarProbeSettings; // coarse enabled, fine enabled, gain, solar-only debug
float3 _MaxSolarCoarseOrigin, _MaxSolarCoarseSpacing, _MaxSolarCoarseCounts;
float3 _MaxSolarFineOrigin, _MaxSolarFineSpacing, _MaxSolarFineCounts;
float4 _MaxSolarCoarseParams, _MaxSolarFineParams; // gamma, normal bias, TOD blend, data flags
float4 _MaxSolarCoarseAtlasLayout, _MaxSolarFineAtlasLayout; // compact tile columns and rows per slice in xy
float4 _MaxSolarFineReceiverCenter; // xyz center, w retained fine weight
float3 _MaxSolarFineReceiverExtents;
float2 _MaxSolarFineReceiverForward;
Texture2DArray<float4> _MaxSolarCoarseIrradiance0, _MaxSolarCoarseIrradiance1, _MaxSolarCoarseProbeData;
Texture2DArray<float4> _MaxSolarFineIrradiance0, _MaxSolarFineIrradiance1, _MaxSolarFineProbeData;
Texture2DArray<float2> _MaxSolarCoarseDistance, _MaxSolarFineDistance;
SamplerState MaxSolarProbe_LinearClamp;

float2 MaxSolarProbeOct(float3 direction)
{
    float reciprocalL1 = rcp(max(dot(abs(direction), 1.0f), 1e-20f));
    float2 oct = direction.xy * reciprocalL1;
    if (direction.z < 0.0f)
        oct = (1.0f - abs(oct.yx)) * float2(oct.x >= 0.0f ? 1.0f : -1.0f,
                                           oct.y >= 0.0f ? 1.0f : -1.0f);
    return oct;
}

float3 MaxSolarProbeUV(int3 tileCoords, float2 oct, float interior, int2 atlasTiles)
{
    // Each probe tile includes a one-texel border.
    float tile = interior + 2.0f;
    float2 dimensions = tile * float2(atlasTiles);
    float2 pixel = float2(tileCoords.xy) * tile + tile * 0.5f;
    return float3((pixel + oct * (interior * 0.5f)) / dimensions, float(tileCoords.z));
}

float3 MaxSolarProbeEdgeDistance(float3 position, float3 origin, float3 spacing, float3 counts)
{
    float3 halfExtent = spacing * (counts - 1.0f) * 0.5f;
    return (halfExtent - abs(position - origin)) / spacing;
}

float MaxSolarProbeOuterFade(float3 edgeDistance)
{
    float3 axes = saturate(1.0f + edgeDistance);
    return axes.x * axes.y * axes.z;
}

// Returns linear irradiance in rgb and an active-neighbor indicator in a.
float4 MaxSampleSolarProbeVolume(float3 position, float3 normal, float3 origin,
    float3 spacing, int3 counts, float4 parameters, float4 atlasLayout,
    Texture2DArray<float4> irradiance0, Texture2DArray<float4> irradiance1,
    Texture2DArray<float2> distanceAtlas, Texture2DArray<float4> probeDataAtlas)
{
    float3 biasedPosition = position + normal * parameters.y;
    float3 start = origin - spacing * (counts - 1) * 0.5f;
    int3 baseCoords = clamp(int3((biasedPosition - start) / spacing), int3(0, 0, 0), counts - 1);
    float3 alpha = saturate((biasedPosition - (start + baseCoords * spacing)) / spacing);
    float2 irradianceOct = MaxSolarProbeOct(normal);
    int flags = int(parameters.w);
    bool compact = (flags & 4) != 0;
    int2 atlasTiles = compact ? int2(atlasLayout.xy) : counts.xz;
    int tilesPerLayer = atlasTiles.x * atlasTiles.y;
    float3 accumulated0 = 0.0f, accumulated1 = 0.0f;
    float totalWeight = 0.0f;
    [loop]
    for (int corner = 0; corner < 8; ++corner)
    {
        int3 offset = int3(corner, corner >> 1, corner >> 2) & 1;
        int3 coords = min(baseCoords + offset, counts - 1);
        float4 probeData = probeDataAtlas.Load(int4(coords.x, coords.z, coords.y, 0));
        int3 tileCoords = coords.xzy;
        [branch]
        if (compact)
        {
            // ProbeData stays on the original grid. W maps each active probe to its packed tile.
            int index = int(probeData.w) - 1;
            if (index < 0) continue;
            int layer = index / tilesPerLayer;
            int indexInLayer = index - layer * tilesPerLayer;
            int row = indexInLayer / atlasTiles.x;
            tileCoords = int3(indexInLayer - row * atlasTiles.x, row, layer);
        }
        else if ((flags & 2) != 0 && int(probeData.w) == 1) continue;

        float3 probe = start + coords * spacing;
        if ((flags & 1) != 0) probe += probeData.xyz * spacing;
        float3 delta = probe - position;
        float3 toProbe = delta * rsqrt(max(dot(delta, delta), 1e-20f));
        float3 biasedDelta = probe - biasedPosition;
        float probeDistance = length(biasedDelta);

        float wrap = (dot(toProbe, normal) + 1.0f) * 0.5f;
        float weight = wrap * wrap + 0.2f;
        // Octahedral coordinates are invariant under positive vector scaling.
        float3 distanceUV = MaxSolarProbeUV(tileCoords, MaxSolarProbeOct(-biasedDelta), 14.0f, atlasTiles);
        // The baked distance atlas stores both moments divided by two.
        float2 moments = 2.0f * distanceAtlas.SampleLevel(MaxSolarProbe_LinearClamp, distanceUV, 0.0f);
        float variance = abs(moments.x * moments.x - moments.y);
        float visible = 1.0f;
        if (probeDistance > moments.x)
        {
            float separation = probeDistance - moments.x;
            visible = variance / (variance + separation * separation);
            visible = max(visible * visible * visible, 0.0f);
        }
        weight = max(0.000001f, weight * max(0.05f, visible));
        if (weight < 0.2f) weight *= (weight * weight) / (0.2f * 0.2f);
        float3 trilinear = max(0.001f, lerp(1.0f - alpha, alpha, float3(offset)));
        weight *= trilinear.x * trilinear.y * trilinear.z;

        float3 uv = MaxSolarProbeUV(tileCoords, irradianceOct, 6.0f, atlasTiles);
        if (parameters.z < 1.0f)
            accumulated0 += pow(irradiance0.SampleLevel(MaxSolarProbe_LinearClamp, uv, 0.0f).rgb,
                parameters.x * 0.5f) * weight;
        if (parameters.z > 0.0f)
            accumulated1 += pow(irradiance1.SampleLevel(MaxSolarProbe_LinearClamp, uv, 0.0f).rgb,
                parameters.x * 0.5f) * weight;
        totalWeight += weight;
    }
    if (totalWeight == 0.0f) return 0.0f;

    float3 average0 = accumulated0 / totalWeight;
    float3 average1 = accumulated1 / totalWeight;
    // Spatially reconstruct each endpoint in the SDK's sqrt(E) domain, then
    // interpolate the decoded linear irradiance across solar time.
    return float4(6.283185307179586f * lerp(average0 * average0, average1 * average1, parameters.z), 1.0f);
}

float MaxSolarFineReceiverWeight(float3 position, float receiverUp)
{
    float3 delta = position - _MaxSolarFineReceiverCenter.xyz;
    float2 forward = _MaxSolarFineReceiverForward;
    float2 right = float2(forward.y, -forward.x);
    float3 local = float3(dot(delta.xz, right), delta.y, dot(delta.xz, forward));
    float3 edge = _MaxSolarFineReceiverExtents - abs(local);
    float horizontal = smoothstep(0.0f, 1.5f, min(edge.x, edge.z));
    float vertical = smoothstep(0.0f, 0.2f, edge.y);
    float upward = smoothstep(0.65f, 0.9f, receiverUp);
    return lerp(1.0f, _MaxSolarFineReceiverCenter.w, horizontal * vertical * upward);
}

float3 MaxSampleBakedSolarProbeGI(float3 position, float3 normal, float receiverUp)
{
    float coarseFade = 0.0f;
    [branch]
    if (_MaxSolarProbeSettings.x > 0.5f)
        coarseFade = MaxSolarProbeOuterFade(MaxSolarProbeEdgeDistance(position,
            _MaxSolarCoarseOrigin, _MaxSolarCoarseSpacing, _MaxSolarCoarseCounts));

    float4 fine = 0.0f;
    float fineBlend = 0.0f;
    float receiverWeight = 1.0f;
    [branch]
    if (_MaxSolarProbeSettings.y > 0.5f)
    {
        float3 fineEdge = MaxSolarProbeEdgeDistance(position,
            _MaxSolarFineOrigin, _MaxSolarFineSpacing, _MaxSolarFineCounts);
        // Smooth the horizontal seam over three meters inside the fine volume.
        // Keep the vertical blend one cell wide and retain the fine-only outer fade.
        const float horizontalFadeMeters = 3.0f;
        float2 horizontalEdgeMeters = fineEdge.xz * _MaxSolarFineSpacing.xz;
        float horizontalBlend = smoothstep(0.0f, horizontalFadeMeters,
            min(horizontalEdgeMeters.x, horizontalEdgeMeters.y));
        fineBlend = coarseFade > 0.0f ? min(horizontalBlend, saturate(fineEdge.y))
                                     : MaxSolarProbeOuterFade(fineEdge);
        [branch]
        if (fineBlend > 0.0f)
        {
            fine = MaxSampleSolarProbeVolume(position, normal, _MaxSolarFineOrigin,
                _MaxSolarFineSpacing, int3(_MaxSolarFineCounts), _MaxSolarFineParams, _MaxSolarFineAtlasLayout,
                _MaxSolarFineIrradiance0, _MaxSolarFineIrradiance1, _MaxSolarFineDistance, _MaxSolarFineProbeData);
            fineBlend *= fine.a;
            if (coarseFade >= 1.0f && _MaxSolarFineReceiverCenter.w < 1.0f)
                receiverWeight = MaxSolarFineReceiverWeight(position, receiverUp);
            if (fineBlend >= 1.0f && receiverWeight >= 1.0f) return fine.rgb;
        }
    }

    float4 coarse = 0.0f;
    [branch]
    if (coarseFade > 0.0f)
        coarse = MaxSampleSolarProbeVolume(position, normal, _MaxSolarCoarseOrigin,
            _MaxSolarCoarseSpacing, int3(_MaxSolarCoarseCounts), _MaxSolarCoarseParams, _MaxSolarCoarseAtlasLayout,
            _MaxSolarCoarseIrradiance0, _MaxSolarCoarseIrradiance1, _MaxSolarCoarseDistance,
            _MaxSolarCoarseProbeData);
    if (coarse.a > 0.0f) fineBlend *= receiverWeight;
    return lerp(coarse.rgb * coarseFade, fine.rgb, fineBlend);
}

#endif
