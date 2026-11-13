#ifndef MAX_AERIAL_PERSPECTIVE_INCLUDED
#define MAX_AERIAL_PERSPECTIVE_INCLUDED

// One native 3D RGBA volume: RGB in-scattering, A average RGB transmittance.
// Match UE's compact AP storage and consume it with one filtered read.
UNITY_DECLARE_TEX3D(_AerialPerspectiveLUT);
float4 _AerialPerspectiveParams; // volume depth after start (metres), voxel width, height, depth
float _AerialPerspectiveStartDepth; // metres; producer and consumer must use the same start
float _AerialPerspectiveEnabled;

// Invalid cached fog data must leave existing surface lighting intact.
float ValidAerialTransmission(float value)
{
    return isfinite(value) ? saturate(value) : 1.0;
}

float3 ValidAerialScattering(float3 value)
{
    return float3(isfinite(value.x) ? max(value.x, 0.0) : 0.0,
        isfinite(value.y) ? max(value.y, 0.0) : 0.0,
        isfinite(value.z) ? max(value.z, 0.0) : 0.0);
}

float3 ComposeAerialPerspective(float3 surfaceRadiance, float transmittance, float3 scattering)
{
    return surfaceRadiance * ValidAerialTransmission(transmittance) + ValidAerialScattering(scattering);
}

// Earlier MaxSRP layout (retained reconstruction notes): reserve z=0 for
// neutral scattering=0/transmission=1, use d(z)=(z/(N-1))^2*D, and sample at
// sqrt(d/D)*(N-1). After finding adjacent slices, interpolate in physical
// distance, (d-d0)/(d1-d0), rather than the nonlinear texture coordinate.
// Every nonzero voxel used 32 uniform midpoint integration steps. Restoring
// that path requires changing both this lookup and the LUT producer together.
//
// The later flattened-2D layout used these same squared texel-centre distances,
// but addressed each z slice as a 32-pixel-wide strip. It took four 2D samples:
// two neighbouring depth slices from each of two RGB textures, then lerped
// with sqrt(d/D)*N-0.5. The disabled helpers below preserve that exact address
// and depth-interpolation logic; restoring it also requires the strip producer.
//
// Native 3D storage now provides that depth interpolation in hardware. All
// depth texels remain centred at squared distances; no neutral slice is stored.
float AerialSliceDistance(float slice)
{
    float fraction = (slice + 0.5) / _AerialPerspectiveParams.w;
    return fraction * fraction * _AerialPerspectiveParams.x;
}

#if 0 // Legacy flattened-2D addressing and manually filtered depth.
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
    float d = max(0.0, distance - _AerialPerspectiveStartDepth);
    // Emulate a clamped, linearly filtered Texture3D sample at w=sqrt(d/D).
    // Texture centres lie at (z+0.5)/N, hence the half-texel subtraction.
    float coordinate = sqrt(saturate(d / _AerialPerspectiveParams.x)) * _AerialPerspectiveParams.w - 0.5;
    coordinate = clamp(coordinate, 0.0, _AerialPerspectiveParams.w - 1.0);
    slice0 = floor(coordinate);
    slice1 = min(slice0 + 1.0, _AerialPerspectiveParams.w - 1.0);
    weight = coordinate - slice0;
}
#endif

float3 AerialPerspectiveUVW(float2 screenUV, float distance)
{
    float d = max(0.0, distance - _AerialPerspectiveStartDepth);
    // Native clamped 3D filtering uses texel centres at (z+0.5)/N. Do not
    // subtract a half texel here: the sampler supplies that interpolation.
    return float3(screenUV, sqrt(saturate(d / _AerialPerspectiveParams.x)));
}

float AerialNearWeight(float distance)
{
    float d = max(0.0, distance - _AerialPerspectiveStartDepth);
    float nonLinearSlice = sqrt(saturate(d / _AerialPerspectiveParams.x)) * _AerialPerspectiveParams.w;
    // UE's sqrt(0.5)-slice fade, linear in physical distance, plus its 1 cm
    // fade at the volume start. Fade the packed opacity towards neutral T=1.
    return saturate(2.0 * nonLinearSlice * nonLinearSlice) * saturate(d * 100.0);
}

float3 ApplyAerialPerspective(float3 surfaceRadiance, float2 screenUV, float distance)
{
    if (_AerialPerspectiveEnabled < 0.5 || distance <= _AerialPerspectiveStartDepth)
        return surfaceRadiance;
    float3 uvw = AerialPerspectiveUVW(screenUV, distance);
    float4 aerial = UNITY_SAMPLE_TEX3D_LOD(_AerialPerspectiveLUT, uvw, 0);
    float nearWeight = AerialNearWeight(distance);
    float3 scattering = aerial.rgb * nearWeight;
    float transmittance = lerp(1.0, aerial.a, nearWeight);
    return ComposeAerialPerspective(surfaceRadiance, transmittance, scattering);
}

// Previous two-volume native-3D core, retained as a restoration prompt:
// L = sample3D(_AerialPerspectiveLUT, uvw).rgb;
// T.rgb = sample3D(_AerialTransmittanceLUT, uvw).rgb;
// return surfaceRadiance * lerp(1, T.rgb, nearWeight) + L * nearWeight;
// Restore the second RGB volume, its CPU bindings and producer UAV together.

#endif
