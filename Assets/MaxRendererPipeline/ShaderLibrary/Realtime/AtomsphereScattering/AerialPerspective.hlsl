#ifndef MAX_AERIAL_PERSPECTIVE_INCLUDED
#define MAX_AERIAL_PERSPECTIVE_INCLUDED

UNITY_DECLARE_TEX2D(_AerialPerspectiveLUT);
UNITY_DECLARE_TEX2D(_AerialTransmittanceLUT);
float4 _AerialPerspectiveParams; // maximum ray distance, voxel width, height, depth
float _AerialPerspectiveEnabled;

// Invalid cached fog data must leave existing surface lighting intact.
float3 ValidAerialTransmission(float3 value)
{
    return float3(isfinite(value.x) ? saturate(value.x) : 1.0,
        isfinite(value.y) ? saturate(value.y) : 1.0,
        isfinite(value.z) ? saturate(value.z) : 1.0);
}

float3 ValidAerialScattering(float3 value)
{
    return float3(isfinite(value.x) ? max(value.x, 0.0) : 0.0,
        isfinite(value.y) ? max(value.y, 0.0) : 0.0,
        isfinite(value.z) ? max(value.z, 0.0) : 0.0);
}

float3 ComposeAerialPerspective(float3 surfaceRadiance, float3 transmittance, float3 scattering)
{
    return surfaceRadiance * ValidAerialTransmission(transmittance) + ValidAerialScattering(scattering);
}

// Include a neutral slice at distance zero; concentrate slices near the camera.
float AerialSliceDistance(float slice)
{
    float fraction = slice / (_AerialPerspectiveParams.w - 1.0);
    return fraction * fraction * _AerialPerspectiveParams.x;
}

float2 AerialSliceUV(float2 screenUV, float slice)
{
    // Clamp within this slice's texel centres so bilinear filtering cannot
    // mix the right edge with the next slice's unrelated leftmost ray.
    float2 pixel = clamp(screenUV * _AerialPerspectiveParams.yz,
        0.5, _AerialPerspectiveParams.yz - 0.5);
    return float2((slice * _AerialPerspectiveParams.y + pixel.x) /
        (_AerialPerspectiveParams.y * _AerialPerspectiveParams.w),
        pixel.y / _AerialPerspectiveParams.z);
}

void AerialDepthSlices(float distance, out float slice0, out float slice1, out float weight)
{
    float d = clamp(distance, 0.0, _AerialPerspectiveParams.x);
    float coordinate = sqrt(d / _AerialPerspectiveParams.x) * (_AerialPerspectiveParams.w - 1.0);
    slice0 = floor(coordinate);
    slice1 = min(slice0 + 1.0, _AerialPerspectiveParams.w - 1.0);
    float d0 = AerialSliceDistance(slice0);
    float d1 = AerialSliceDistance(slice1);
    // Interpolate in physical distance, rather than the squared slice coordinate.
    weight = d1 > d0 ? saturate((d - d0) / max(d1 - d0, 1e-6)) : 0.0;
}

float3 ApplyAerialPerspective(float3 surfaceRadiance, float2 screenUV, float distance)
{
    if (_AerialPerspectiveEnabled < 0.5) return surfaceRadiance;
    float slice0, slice1, weight;
    AerialDepthSlices(distance, slice0, slice1, weight);
    float2 uv0 = AerialSliceUV(screenUV, slice0);
    float2 uv1 = AerialSliceUV(screenUV, slice1);
    float3 scattering = lerp(UNITY_SAMPLE_TEX2D_LOD(_AerialPerspectiveLUT, uv0, 0).rgb,
        UNITY_SAMPLE_TEX2D_LOD(_AerialPerspectiveLUT, uv1, 0).rgb, weight);
    float3 transmittance = lerp(UNITY_SAMPLE_TEX2D_LOD(_AerialTransmittanceLUT, uv0, 0).rgb,
        UNITY_SAMPLE_TEX2D_LOD(_AerialTransmittanceLUT, uv1, 0).rgb, weight);
    return ComposeAerialPerspective(surfaceRadiance, transmittance, scattering);
}

#endif
