#ifndef MAX_LIGHT_INPUT_INCLUDED
#define MAX_LIGHT_INPUT_INCLUDED

#include "HLSLSupport.cginc"
#include "./CommonInput.hlsl"

#define POINT_LIGHT_COUNT 32

float4x4 _MainLightMatrixWorldToShadowMap;
float4 _DirectionalLightColor;
float4 _DirectionalLightDirection;

//������Դ��λ�úͷ�Χ,xyz����λ�ã�w����Χ
float4 _PointLightPositionAndRanges[POINT_LIGHT_COUNT];
//������Դ����ɫ
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

UNITY_DECLARE_TEX2D(_ScreenSpaceShadowMapBlur);  // ��һ�ξ���

#endif