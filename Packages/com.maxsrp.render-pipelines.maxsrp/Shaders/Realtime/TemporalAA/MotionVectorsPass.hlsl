#ifndef MAX_MOTION_VECTORS_INCLUDED
#define MAX_MOTION_VECTORS_INCLUDED
#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/Core/Packing.hlsl"
float4x4 _MaxUnjitteredVP, _MaxPreviousVP;
struct MotionAttributes { float4 position : POSITION; float3 previousPosition : TEXCOORD4; };
struct MotionVaryings
{
    float4 position : SV_POSITION;
    float4 current : TEXCOORD0;
    float4 previous : TEXCOORD1;
    nointerpolation float dynamicPrimitive : TEXCOORD3;
};
MotionVaryings MaxMotionVertex(MotionAttributes input)
{
    MotionVaryings o;
    float4 world = mul(unity_ObjectToWorld, input.position);
    float4 oldPosition = unity_MotionVectorsParams.x == 1 ? float4(input.previousPosition, 1) : input.position;
    float4 oldWorld = unity_MotionVectorsParams.w == 0 ? world : mul(unity_MatrixPreviousM, oldPosition);
    o.position = mul(_MaxRasterVP, world);
    o.current = mul(_MaxUnjitteredVP, world);
    o.previous = mul(_MaxPreviousVP, oldWorld);
    // UE VelocityRendering.cpp omits stationary rigid primitives. Compare
    // translations in metres, converting UE's 0.0001 cm matrix tolerance.
    float4x4 change = abs(unity_ObjectToWorld - unity_MatrixPreviousM);
    bool moved = any(change[0] > float4(0.0001, 0.0001, 0.0001, 0.000001)) ||
        any(change[1] > float4(0.0001, 0.0001, 0.0001, 0.000001)) ||
        any(change[2] > float4(0.0001, 0.0001, 0.0001, 0.000001));
    o.dynamicPrimitive = unity_MotionVectorsParams.w != 0 &&
        (unity_MotionVectorsParams.x == 1 || moved) ? 1 : 0;
    return o;
}
MAX_VELOCITY_TARGET MaxMotionFragment(MotionVaryings input) : SV_Target
{
    // ForceNoMotion must reject history rather than pretend a moving surface is static.
    bool reject = unity_MotionVectorsParams.y == 0 || input.previous.w <= 0 || input.current.w <= 0;
    return MaxWriteVelocity(input.current, input.previous, input.dynamicPrimitive > 0.5, reject);
}
#endif
