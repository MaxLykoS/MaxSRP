#ifndef MAX_LIGHT_INPUT_INCLUDED
#define MAX_LIGHT_INPUT_INCLUDED

#include "HLSLSupport.cginc"
#include "./CommonInput.hlsl"

#define POINT_LIGHT_COUNT 32

float4x4 _MainLightMatrixWorldToShadowMap;
float4 _DirectionalLightColor;
float4 _DirectionalLightDirection;

//非主光源的位置和范围,xyz代表位置，w代表范围
float4 _PointLightPositionAndRanges[POINT_LIGHT_COUNT];
//非主光源的颜色
half4 _PointLightColors[POINT_LIGHT_COUNT];
int _PointLightCount;

struct DirLight
{
    float3 direction;
    half4 color;
};

struct PointLight
{
    float4 positionRange;
    half4 color;
};

DirLight GetMainLight()
{
    DirLight light;
    light.direction = _DirectionalLightDirection;
    light.color = _DirectionalLightColor;
    return light;
}

PointLight GetPointLight(uint index)
{
    PointLight light;
    float4 positionRange = _PointLightPositionAndRanges[index];
    half4 color = _PointLightColors[index];
    light.positionRange = positionRange;
    light.color = color;
    return light;
}

float DistanceAtten(float distanceSqr, float rangeSqr)
{
    float factor = saturate(1 - distanceSqr * rcp(rangeSqr));
    factor = factor * factor;
    return factor * rcp(max(distanceSqr, 0.001));
}

UNITY_DECLARE_TEX2D(_ScreenSpaceShadowMapBlur);  // 采一次就行

#endif