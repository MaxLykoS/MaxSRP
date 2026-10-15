#ifndef MAX_AERIALPERSPECTIVE_PASS_INCLUDED
#define MAX_AERIALPERSPECTIVE_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"
#include "../../../ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl"

// Dedicated inputs survive utility Blits that may overwrite Unity's camera matrices.
float4x4 _AerialInverseProjection;
float4x4 _AerialCameraToWorld;
float4 _AerialCameraPosition; // world position, orthographic flag

struct v2f
{
    float4 vertex : SV_POSITION;
};

struct AerialOutputs
{
    float4 scattering : SV_Target0;
    float4 transmittance : SV_Target1;
};

v2f vert(uint vertexID : SV_VertexID)
{
    v2f o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

AerialOutputs frag(v2f i)
{
    AtmosphereParameter param = GetAtmosphereParameter();
    float slice = floor(i.vertex.x / _AerialPerspectiveParams.y);
    float2 screenUV = float2((i.vertex.x - slice * _AerialPerspectiveParams.y) /
        _AerialPerspectiveParams.y, i.vertex.y / _AerialPerspectiveParams.z);
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
        float3 cameraPlanePosition = float3(viewPosition.xy / viewPosition.w, 0.0);
        eyePos += mul((float3x3)_AerialCameraToWorld, cameraPlanePosition);
        eyePos.y = max(eyePos.y, param.PlanetRadius + 1.0);
        viewDir = normalize(mul((float3x3)_AerialCameraToWorld, float3(0.0, 0.0, -1.0)));
    }
    float3 lightDir = normalize(_AtmosphereSunDirection);
    float distance = AerialSliceDistance(slice);
    float3 transmittance;
    float3 scattering = GetAtmosphereSegment(param, eyePos, viewDir, lightDir, distance, transmittance);
    AerialOutputs o;
    o.scattering = float4(scattering, 1.0);
    o.transmittance = float4(transmittance, 1.0);
    return o;
}

#endif
