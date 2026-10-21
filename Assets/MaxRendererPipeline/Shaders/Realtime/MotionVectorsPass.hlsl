#ifndef MAX_MOTION_VECTORS_INCLUDED
#define MAX_MOTION_VECTORS_INCLUDED
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
float4x4 _MaxUnjitteredVP, _MaxPreviousVP, _MaxPreviousView;
struct MotionAttributes { float4 position : POSITION; float3 previousPosition : TEXCOORD4; };
struct MotionVaryings
{
    float4 position : SV_POSITION;
    float4 current : TEXCOORD0;
    float4 previous : TEXCOORD1;
    float previousDepth : TEXCOORD2;
};
MotionVaryings MaxMotionVertex(MotionAttributes input)
{
    MotionVaryings o;
    float4 world = mul(unity_ObjectToWorld, input.position);
    float4 oldPosition = unity_MotionVectorsParams.x == 1 ? float4(input.previousPosition, 1) : input.position;
    float4 oldWorld = unity_MotionVectorsParams.w == 0 ? world : mul(unity_MatrixPreviousM, oldPosition);
    o.position = mul(unity_MatrixVP, world);
    o.current = mul(_MaxUnjitteredVP, world);
    o.previous = mul(_MaxPreviousVP, oldWorld);
    o.previousDepth = -mul(_MaxPreviousView, oldWorld).z;
    return o;
}
float4 MaxMotionFragment(MotionVaryings input) : SV_Target
{
    // ForceNoMotion must reject history rather than pretend a moving surface is static.
    if (unity_MotionVectorsParams.y == 0 || input.previous.w <= 0 || input.current.w <= 0)
        return float4(0, 0, 0, -1);
    float2 velocity = 0.5 * (input.current.xy / input.current.w - input.previous.xy / input.previous.w);
#if UNITY_UV_STARTS_AT_TOP
    velocity.y = -velocity.y;
#endif
    return float4(velocity, input.previousDepth, 1);
}
#endif
