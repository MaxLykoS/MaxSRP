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

float4 _MaxDDGISettings; // enabled, unused, gain, legacy DDGI-only display
float3 _MaxDDGILightColor;
float3 _MaxDDGIOrigin, _MaxDDGISpacing, _MaxDDGICounts;
float4 _MaxDDGIParams; // gamma, normal bias, TOD blend, data flags
float4 _MaxDDGIAtlasLayout; // compact tile columns and rows per slice in xy
float3 _MaxDDGISamplingStride;
Texture2DArray<float4> _MaxDDGIIrradiance0, _MaxDDGIIrradiance1, _MaxDDGIProbeData;
Texture2DArray<float2> _MaxDDGIDistance;
SamplerState MaxDDGI_LinearClamp;

float2 MaxDDGIOct(float3 direction)
{
    float reciprocalL1 = rcp(max(dot(abs(direction), 1.0f), 1e-20f));
    float2 oct = direction.xy * reciprocalL1;
    if (direction.z < 0.0f)
        oct = (1.0f - abs(oct.yx)) * float2(oct.x >= 0.0f ? 1.0f : -1.0f,
                                           oct.y >= 0.0f ? 1.0f : -1.0f);
    return oct;
}

float3 MaxDDGIUV(int3 tileCoords, float2 oct, float interior, int2 atlasTiles)
{
    // Each probe tile includes a one-texel border.
    float tile = interior + 2.0f;
    float2 dimensions = tile * float2(atlasTiles);
    float2 pixel = float2(tileCoords.xy) * tile + tile * 0.5f;
    return float3((pixel + oct * (interior * 0.5f)) / dimensions, float(tileCoords.z));
}

float3 MaxDDGIEdgeDistance(float3 position, float3 origin, float3 spacing, float3 counts)
{
    float3 halfExtent = spacing * (counts - 1.0f) * 0.5f;
    return (halfExtent - abs(position - origin)) / spacing;
}

float MaxDDGIOuterFade(float3 edgeDistance)
{
    float3 axes = saturate(1.0f + edgeDistance);
    return axes.x * axes.y * axes.z;
}

// Returns linear irradiance.
float3 MaxSampleDDGIVolume(float3 position, float3 normal, float3 origin,
    float3 spacing, int3 counts, float4 parameters, float4 atlasLayout, float3 samplingStride,
    Texture2DArray<float4> irradiance0, Texture2DArray<float4> irradiance1,
    Texture2DArray<float2> distanceAtlas, Texture2DArray<float4> probeDataAtlas)
{
    float3 biasedPosition = position + normal * parameters.y;
    float3 start = origin - spacing * (counts - 1) * 0.5f;
    int3 stride = max(int3(samplingStride), 1);
    int3 last = counts - 1;
    // Select neighbors on the platform's sampling grid, then address the baked
    // grid directly. Include its last probe when the final cell is shorter.
    int3 baseCoords = clamp(int3((biasedPosition - start) / (spacing * stride)) * stride, 0, last);
    int3 upperCoords = min(baseCoords + stride, last);
    float3 cellSize = spacing * max(upperCoords - baseCoords, 1);
    float3 alpha = saturate((biasedPosition - (start + baseCoords * spacing)) / cellSize);
    float2 irradianceOct = MaxDDGIOct(normal);
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
        int3 coords = baseCoords + offset * (upperCoords - baseCoords);
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
        float3 distanceUV = MaxDDGIUV(tileCoords, MaxDDGIOct(-biasedDelta), 14.0f, atlasTiles);
        // The baked distance atlas stores both moments divided by two.
        float2 moments = 2.0f * distanceAtlas.SampleLevel(MaxDDGI_LinearClamp, distanceUV, 0.0f);
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

        float3 uv = MaxDDGIUV(tileCoords, irradianceOct, 6.0f, atlasTiles);
        if (parameters.z < 1.0f)
            accumulated0 += pow(irradiance0.SampleLevel(MaxDDGI_LinearClamp, uv, 0.0f).rgb,
                parameters.x * 0.5f) * weight;
        if (parameters.z > 0.0f)
            accumulated1 += pow(irradiance1.SampleLevel(MaxDDGI_LinearClamp, uv, 0.0f).rgb,
                parameters.x * 0.5f) * weight;
        totalWeight += weight;
    }
    if (totalWeight == 0.0f) return 0.0f;

    float3 average0 = accumulated0 / totalWeight;
    float3 average1 = accumulated1 / totalWeight;
    // Spatially reconstruct each endpoint in the SDK's sqrt(E) domain, then
    // interpolate the decoded linear irradiance across solar time.
    return 6.283185307179586f * lerp(average0 * average0, average1 * average1, parameters.z);
}

float3 MaxSampleBakedDDGI(float3 position, float3 normal)
{
    float fade = MaxDDGIOuterFade(MaxDDGIEdgeDistance(position, _MaxDDGIOrigin, _MaxDDGISpacing, _MaxDDGICounts));
    [branch]
    if (fade <= 0.0f) return 0.0f;
    return MaxSampleDDGIVolume(position, normal, _MaxDDGIOrigin, _MaxDDGISpacing,
        int3(_MaxDDGICounts), _MaxDDGIParams, _MaxDDGIAtlasLayout, _MaxDDGISamplingStride,
        _MaxDDGIIrradiance0, _MaxDDGIIrradiance1, _MaxDDGIDistance, _MaxDDGIProbeData) * fade;
}

#endif
