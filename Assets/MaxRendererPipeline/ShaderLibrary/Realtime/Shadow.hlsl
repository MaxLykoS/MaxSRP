#ifndef MAX_SHADOW_INCLUDED
#define MAX_SHADOW_INCLUDED

#include "./LightInput.hlsl"
#include "./ShadowInput.hlsl"
#include "./SpaceTransform.hlsl"

/*
#define N_SAMPLE 16
static float2 poissonDisk[16] = {
    float2( -0.94201624, -0.39906216 ),
    float2( 0.94558609, -0.76890725 ),
    float2( -0.094184101, -0.92938870 ),
    float2( 0.34495938, 0.29387760 ),
    float2( -0.91588581, 0.45771432 ),
    float2( -0.81544232, -0.87912464 ),
    float2( -0.38277543, 0.27676845 ),
    float2( 0.97484398, 0.75648379 ),
    float2( 0.44323325, -0.97511554 ),
    float2( 0.53742981, -0.47373420 ),
    float2( -0.26496911, -0.41893023 ),
    float2( 0.79197514, 0.19090188 ),
    float2( -0.24188840, 0.99706507 ),
    float2( -0.81409955, 0.91437590 ),
    float2( 0.19984126, 0.78641367 ),
    float2( 0.14383161, -0.14100790 )
};
*/

#define N_SAMPLE 64
static float2 poissonDisk[N_SAMPLE] = {
    float2(-0.5119625f, -0.4827938f),
    float2(-0.2171264f, -0.4768726f),
    float2(-0.7552931f, -0.2426507f),
    float2(-0.7136765f, -0.4496614f),
    float2(-0.5938849f, -0.6895654f),
    float2(-0.3148003f, -0.7047654f),
    float2(-0.42215f, -0.2024607f),
    float2(-0.9466816f, -0.2014508f),
    float2(-0.8409063f, -0.03465778f),
    float2(-0.6517572f, -0.07476326f),
    float2(-0.1041822f, -0.02521214f),
    float2(-0.3042712f, -0.02195431f),
    float2(-0.5082307f, 0.1079806f),
    float2(-0.08429877f, -0.2316298f),
    float2(-0.9879128f, 0.1113683f),
    float2(-0.3859636f, 0.3363545f),
    float2(-0.1925334f, 0.1787288f),
    float2(0.003256182f, 0.138135f),
    float2(-0.8706837f, 0.3010679f),
    float2(-0.6982038f, 0.1904326f),
    float2(0.1975043f, 0.2221317f),
    float2(0.1507788f, 0.4204168f),
    float2(0.3514056f, 0.09865579f),
    float2(0.1558783f, -0.08460935f),
    float2(-0.0684978f, 0.4461993f),
    float2(0.3780522f, 0.3478679f),
    float2(0.3956799f, -0.1469177f),
    float2(0.5838975f, 0.1054943f),
    float2(0.6155105f, 0.3245716f),
    float2(0.3928624f, -0.4417621f),
    float2(0.1749884f, -0.4202175f),
    float2(0.6813727f, -0.2424808f),
    float2(-0.6707711f, 0.4912741f),
    float2(0.0005130528f, -0.8058334f),
    float2(0.02703013f, -0.6010728f),
    float2(-0.1658188f, -0.9695674f),
    float2(0.4060591f, -0.7100726f),
    float2(0.7713396f, -0.4713659f),
    float2(0.573212f, -0.51544f),
    float2(-0.3448896f, -0.9046497f),
    float2(0.1268544f, -0.9874692f),
    float2(0.7418533f, -0.6667366f),
    float2(0.3492522f, 0.5924662f),
    float2(0.5679897f, 0.5343465f),
    float2(0.5663417f, 0.7708698f),
    float2(0.7375497f, 0.6691415f),
    float2(0.2271994f, -0.6163502f),
    float2(0.2312844f, 0.8725659f),
    float2(0.4216993f, 0.9002838f),
    float2(0.4262091f, -0.9013284f),
    float2(0.2001408f, -0.808381f),
    float2(0.149394f, 0.6650763f),
    float2(-0.09640376f, 0.9843736f),
    float2(0.7682328f, -0.07273844f),
    float2(0.04146584f, 0.8313184f),
    float2(0.9705266f, -0.1143304f),
    float2(0.9670017f, 0.1293385f),
    float2(0.9015037f, -0.3306949f),
    float2(-0.5085648f, 0.7534177f),
    float2(0.9055501f, 0.3758393f),
    float2(0.7599946f, 0.1809109f),
    float2(-0.2483695f, 0.7942952f),
    float2(-0.4241052f, 0.5581087f),
    float2(-0.1020106f, 0.6724468f)
};

#define ACTIVED_CASADE_COUNT _ShadowParams.w

///将世界坐标转换到采样ShadowMapTexture的映射,返回值的xy为uv，z为深度，w为cascade级别
float4 WorldToShadowMapPos(float3 positionWS)
{
    for (int i = 0; i < ACTIVED_CASADE_COUNT; i++)
    {
        float4 cullingSphere = _CascadeCullingSpheres[i];
        float3 center = cullingSphere.xyz;
        float radiusSqr = cullingSphere.w * cullingSphere.w;
        float3 d = (positionWS - center);
        //计算世界坐标是否在包围球内。
        if(dot(d,d) <= radiusSqr)
        {
            //如果是，就利用这一级别的Cascade来进行采样
            float4x4 worldToCascadeMatrix = _WorldToMainLightCascadeShadowMapSpaceMatrices[i];
            float4 shadowMapPos = mul(worldToCascadeMatrix,float4(positionWS,1));
            shadowMapPos /= shadowMapPos.w;  // 透视除法，变换到NDC空间  //dx范围是[0, 1]，opengl是[-1, 1]
            shadowMapPos.w = i;
            return shadowMapPos;
        }
    }

    return float4(0, 0, 0, 0);
}

float PCF_3x3(float2 sampleUV, float curDepth, float bias)
{
    float shadow = 0.0;
    float2 texelSize = float2(1.0 / float(_ShadowMapWidth), 1.0 / float(_ShadowMapWidth));
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            float pcfDepth = UNITY_SAMPLE_TEX2D(_MainShadowMap, sampleUV + float2(x, y) * texelSize).r;
            float curShadow = (curDepth + bias > pcfDepth) ? 1.0 : 0.0;

            shadow = shadow + curShadow;
        }
    }
    shadow = shadow / 9.0f;
    return shadow;
}

float GetVisibility01(float2 sampleUV, float curDepth, float bias)
{
    float pcfDepth = UNITY_SAMPLE_TEX2D(_MainShadowMap, sampleUV).r;

    return (curDepth + bias > pcfDepth) ? 1.0 : 0.0;
}

// PCSS **********************
/*float2 RotateVec2(float2 v, float angle)
{
    float s = sin(angle);
    float c = cos(angle);

    return float2(v.x * c + v.y * s, -v.x * s + v.y * c);
}

float GetBlockerDepth(float curDepth, float2 sampleUV, float bias, float rotateAngle)
{
    float dBlocker = 0;
    int count = 0.005;
    float2 texelSize = float2(1.0 / float(_ShadowMapWidth), 1.0 / float(_ShadowMapWidth));

    for (int i = 0; i < N_SAMPLE; ++i)
    {
        float2 offset = RotateVec2(poissonDisk[i], rotateAngle);
        float2 sampleDepth = sampleUV + offset;
        float shadowMapDepth = UNITY_SAMPLE_TEX2D(_MainShadowMap, sampleDepth).r;
        if ((curDepth + bias > shadowMapDepth))
        {
            dBlocker += shadowMapDepth;
            ++count;
        }
    }
    return float2(dBlocker / count, count);
}

float PCSS(float curDepth, float bias, float sampleUV, float rotateAngle,
    float pcssSearchRadius, float pcssFilterRadius)
{
    float searchWidth = pcssSearchRadius / _OrthoWidth;
    float2 blocker = GetBlockerDepth(curDepth, sampleUV, bias, rotateAngle);
    float averageDepth = blocker.x;
    float blockCount = blocker.y;
    if (blockCount < 1) return 1;  // 没有遮挡

    // 世界空间
    float receiverDepth = curDepth * 2 * _OrthoDistance;
    float blockerDepth = averageDepth * 2 * _OrthoDistance;

    // 计算世界空间下filter半径
    float radius = (receiverDepth - blockerDepth) * pcssFilterRadius / blockerDepth;

    // 深度图上的filter半径
    radius = radius / _OrthoWidth;

    float shadow = 0;
    // PCF
    for (int i = 0; i < N_SAMPLE; ++i)
    {
        float2 offset = poissonDisk[i];
        offset = RotateVec2(offset, rotateAngle);
        float2 uv = uv + offset * radius;

        float sampleDepth = UNITY_SAMPLE_TEX2D(_MainShadowMap, uv).r;
        if (sampleDepth > curDepth) shadow += 1.0f;
    }
    shadow /= N_SAMPLE;
    return shadow;
}*/
// *****************************************

float GetMainLightShadowVisibility(float3 positionWS,float3 normalWS, float3 lightDir)
{
    // no lights at all
    if (_ShadowParams.z == 0)
        return 0;
    float4 shadowMapPos = WorldToShadowMapPos(positionWS + normalWS * _ShadowParams.y);
    float curDepth = shadowMapPos.z;
    float2 sampleUV = shadowMapPos.xy;
    int cascade = shadowMapPos.w;

    float bias = max(0.05 * (1.0 - dot(normalWS, lightDir)), 0.005);

    float visibility = 0;
    if (cascade == 0)
        visibility = PCF_3x3(sampleUV, curDepth, bias);
    else
        visibility = GetVisibility01(sampleUV, curDepth, bias);
    return visibility;
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