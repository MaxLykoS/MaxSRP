#ifndef MAX_SHADOW_INCLUDED
#define MAX_SHADOW_INCLUDED

#include "./LightInput.hlsl"
#include "./ShadowInput.hlsl"
#include "./SpaceTransform.hlsl"

UNITY_DECLARE_TEX2D(_MaxMainShadowMap);

float4 _ShadowParams; //x is depthBias,y is normal bias,z is strength, w is cascadeCount

#define ACTIVED_CASADE_COUNT _ShadowParams.w

///将世界坐标转换到采样ShadowMapTexture的映射,返回值的xy为uv，z为深度
float3 WorldToShadowMapPos(float3 positionWS)
{
    for(int i = 0; i < ACTIVED_CASADE_COUNT; i ++)
    {
        float4 cullingSphere = _MaxCascadeCullingSpheres[i];
        float3 center = cullingSphere.xyz;
        float radiusSqr = cullingSphere.w * cullingSphere.w;
        float3 d = (positionWS - center);
        //计算世界坐标是否在包围球内。
        if(dot(d,d) <= radiusSqr)
        {
            //如果是，就利用这一级别的Cascade来进行采样
            float4x4 worldToCascadeMatrix = _MaxWorldToMainLightCascadeShadowMapSpaceMatrices[i];
            float4 shadowMapPos = mul(worldToCascadeMatrix,float4(positionWS,1));
            shadowMapPos /= shadowMapPos.w;  // 透视除法，变换到NDC空间  //dx范围是[0, 1]，opengl是[-1, 1]
            return shadowMapPos;
        }
    }
    //表示超出ShadowMap. 不显示阴影。
    #if UNITY_REVERSED_Z
    return float3(0,0,1);
    #else
    return float3(0,0,0);
    #endif
}

float PCF_3x3(float2 sampleUV, float curDepth, float bias)
{
    float shadow = 0.0;
    float2 texelSize = float2(1.0 / 2048.0, 1.0 / 2048.0);
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            float pcfDepth = UNITY_SAMPLE_TEX2D(_MaxMainShadowMap, sampleUV + float2(x, y) * texelSize);
            float curShadow = (curDepth + bias > pcfDepth) ? 1.0 : 0.0;

            shadow = shadow + curShadow;
        }
    }
    shadow = shadow / 9.0f;
    return shadow;
}

///检查世界坐标是否位于主灯光的阴影之中(0表示不在阴影中，大于0表示在阴影中,数值代表了阴影强度)
float GetMainLightShadowVisibility(float3 positionWS,float3 normalWS, float3 lightDir)
{
    // no lights at all
    if (_ShadowParams.z == 0)
        return 0;
    float3 shadowMapPos = WorldToShadowMapPos(positionWS + normalWS * _ShadowParams.y);
    float curDepth = shadowMapPos.z;
    float2 sampleUV = shadowMapPos.xy;

    float bias = max(0.05 * (1.0 - dot(normalWS, lightDir)), 0.005);
    float visibility = PCF_3x3(sampleUV, curDepth, bias);
    return visibility;

/*
#if UNITY_REVERSED_Z
    // depthToLight < depth 表示在阴影之中
    return clamp(step(depthToLight + _ShadowParams.x, depth), 0, _ShadowParams.z);
#else
    // depthToLight > depth表示在阴影之中
    return clamp(step(depth, depthToLight - _ShadowParams.x), 0, _ShadowParams.z);
#endif*/
}


/**
======= Shadow Caster Region =======
**/

struct ShadowCasterAttributes
{
    float4 pO   : POSITION;
};

struct ShadowCasterVaryings
{
    float4 pH   : SV_POSITION;
};

ShadowCasterVaryings ShadowCasterVertex(ShadowCasterAttributes input)
{
    ShadowCasterVaryings output;
    output.pH = UnityObjectToClipPos(input.pO);
    return output;
}

half4 ShadowCasterFragment(ShadowCasterVaryings input) : SV_Target
{
    return 0;
}
#endif