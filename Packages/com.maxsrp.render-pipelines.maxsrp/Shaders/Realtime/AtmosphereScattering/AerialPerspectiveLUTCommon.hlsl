#ifndef MAX_AERIALPERSPECTIVE_COMMON_INCLUDED
#define MAX_AERIALPERSPECTIVE_COMMON_INCLUDED

#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl"

// Dedicated inputs survive utility Blits that may overwrite Unity's camera matrices.
float4x4 _AerialInverseProjection;
float4x4 _AerialCameraToWorld;
float4 _AerialCameraPosition; // world position in km, orthographic flag
float _AtmosphereAerialSampleCountPerSlice;

struct AerialVoxelResult
{
    float4 scattering;
    float4 transmittance;
};

// Shared by the native 3D compute producer and the retained raster reference.
AerialVoxelResult EvaluateAerialPerspectiveVoxel(float2 screenUV, float slice)
{
    AtmosphereParameter param = GetAtmosphereParameter();
    float2 ndc = screenUV * 2.0 - 1.0;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    float4 viewPosition = mul(_AerialInverseProjection, float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0));
    float3 viewDir = normalize(mul((float3x3)_AerialCameraToWorld, viewPosition.xyz));
    float3 eyePos = AtmosphereCameraPosition(_AerialCameraPosition.y, param.SeaLevel, param.PlanetRadius);
    if (_AerialCameraPosition.w > 0.5)
    {
        // Parallel rays start at different points on the camera plane.
        float3 cameraPlanePosition = float3(viewPosition.xy / viewPosition.w, 0.0) * ATMOSPHERE_METRES_TO_KM;
        eyePos += mul((float3x3)_AerialCameraToWorld, cameraPlanePosition);
        eyePos.y = max(eyePos.y, param.PlanetRadius + ATMOSPHERE_MIN_ALTITUDE_KM);
        viewDir = normalize(mul((float3x3)_AerialCameraToWorld, float3(0.0, 0.0, -1.0)));
    }
    float3 lightDir = normalize(_AtmosphereSunDirection);
    // AP lookup distances stay in Unity metres; only transport uses km.
    float distance = AerialSliceDistance(slice) * ATMOSPHERE_METRES_TO_KM;
    float startDistance = max(0.0, _AerialPerspectiveStartDepth) * ATMOSPHERE_METRES_TO_KM;
    // Clip on the original observer ray before applying the start offset. If
    // the ground is already closer than StartDepth, the visible path has no
    // atmospheric segment to store; shifting first would start underground.
    // Unlike UE's below-horizon froxel reprojection, this keeps the geometric
    // ray and its original solid-ground boundary unchanged.
    float groundDistance = RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir);
    AerialVoxelResult o;
    o.scattering = float4(0.0, 0.0, 0.0, 1.0);
    o.transmittance = float4(1.0, 1.0, 1.0, 1.0);
    if (groundDistance >= 0.0)
    {
        if (groundDistance <= startDistance) return o;
        distance = min(distance, groundDistance - startDistance);
    }
    eyePos += viewDir * startDistance;
    // UE integrates the entire path to each voxel with (z+1)*k samples, not
    // just the interval between this slice and its predecessor. Former MaxSRP
    // used a neutral z=0 plus 32 uniform midpoint samples at every other slice.
    float sampleCount = max(1.0, (slice + 1.0) * _AtmosphereAerialSampleCountPerSlice);
    float3 transmittance;
    float3 scattering = GetAtmosphereSegment(param, eyePos, viewDir, lightDir, distance, transmittance, sampleCount);
    o.scattering = float4(scattering, 1.0);
    o.transmittance = float4(transmittance, 1.0);
    return o;
}

#endif
