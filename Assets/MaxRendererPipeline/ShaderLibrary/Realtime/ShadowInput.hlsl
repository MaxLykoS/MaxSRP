#ifndef MAX_SHADOW_INPUT_INCLUDED
#define MAX_SHADOW_INPUT_INCLUDED

sampler2D _MainShadowMap0;
sampler2D _MainShadowMap1;
sampler2D _MainShadowMap2;
sampler2D _MainShadowMap3;
sampler2D _NoiseTexture;

UNITY_DECLARE_TEX2D(_ScreenSpaceShadowMapBeforeBlur);

//主灯光 世界空间->投影空间变换矩阵
float4x4 _ShadowVPMatrix0;
float4x4 _ShadowVPMatrix1;
float4x4 _ShadowVPMatrix2;
float4x4 _ShadowVPMatrix3;

float _Split0;
float _Split1;
float _Split2;
float _Split3;

float _OrthoWidth0;
float _OrthoWidth1;
float _OrthoWidth2;
float _OrthoWidth3;

float _OrthoDistance;
float _ShadowMapResolution;
float _LightSize;

float _ScreenWidth;
float _ScreenHeight;
float _NoiseTextureResolution;

float _ShadingPointNormalBias0;
float _ShadingPointNormalBias1;
float _ShadingPointNormalBias2;
float _ShadingPointNormalBias3;

float _DepthNormalBias0;
float _DepthNormalBias1;
float _DepthNormalBias2;
float _DepthNormalBias3;

float _PcssSearchRadius0;
float _PcssSearchRadius1;
float _PcssSearchRadius2;
float _PcssSearchRadius3;

float _PcssFilterRadius0;
float _PcssFilterRadius1;
float _PcssFilterRadius2;
float _PcssFilterRadius3;
#endif