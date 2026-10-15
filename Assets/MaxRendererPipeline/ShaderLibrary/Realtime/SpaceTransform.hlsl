#ifndef MAX_SPACE_TRANSFORM_INCLUDE
#define MAX_SPACE_TRANSFORM_INCLUDE

#include "./CommonInput.hlsl"

// Texture depth is always [0,1]; GL clip depth is [-1,1].
float DeviceDepthToClipDepth(float depth)
{
#if defined(UNITY_REVERSED_Z)
    return depth;
#else
    return lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
}

float ClipDepthToDeviceDepth(float depth)
{
#if defined(UNITY_REVERSED_Z)
    return depth;
#else
    return (depth - UNITY_NEAR_CLIP_VALUE) / (1.0 - UNITY_NEAR_CLIP_VALUE);
#endif
}

float4 ObjectToHClipPosition(float3 positionOS)
{
    float4 positionWS = mul(unity_ObjectToWorld,float4(positionOS,1));
    return mul(unity_MatrixVP,positionWS);
}

float3 TransformObjectToWorld(float3 positionOS)
{
    float4 positionWS = mul(unity_ObjectToWorld,float4(positionOS,1));
    return positionWS.xyz;
}

float3 TransformObjectToWorldNormal(float3 normalOS)
{
    // Row-vector multiplication applies the inverse transpose to the normal.
    return normalize(mul(normalOS, (float3x3)unity_WorldToObject));
}

float3 TransformObjectToWorldDir(float3 directionOS)
{
    return mul((float3x3)unity_ObjectToWorld, directionOS);
}

float4 TransformWorldToHClip(float3 positionWS)
{
    return mul(unity_MatrixVP,float4(positionWS,1));
}

#define UnityObjectToClipPos ObjectToHClipPosition

#endif
