#ifndef MAX_AERIALPERSPECTIVE_PASS_INCLUDED
#define MAX_AERIALPERSPECTIVE_PASS_INCLUDED

#include "AerialPerspectiveLUTCommon.hlsl"

// Retained raster reference. Production native-3D volumes are populated by
// AerialPerspectiveLUT.compute; this pass keeps the prior fragment entry for
// source comparison and can still render one packed voxel slice to a 2D target.
float _AtmosphereAerialSlice;

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert(uint vertexID : SV_VertexID)
{
    v2f o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

float4 frag(v2f i) : SV_Target
{
    // Legacy 2D strip producer: slice=floor(SV_Position.x/width), then
    // screenUV.x=(SV_Position.x-slice*width)/width with target width=width*N.
    // The retained single-slice reference uses a width x height viewport.
    float2 screenUV = i.vertex.xy / _AerialPerspectiveParams.yz;
    AerialVoxelResult voxel = EvaluateAerialPerspectiveVoxel(screenUV, _AtmosphereAerialSlice);
    float transmittance = dot(voxel.transmittance.rgb, float3(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0));
    return float4(voxel.scattering.rgb, transmittance);
    // Legacy MRT core: o.scattering=voxel.scattering (SV_Target0),
    // o.transmittance=voxel.transmittance (SV_Target1), return o.
}

#endif
