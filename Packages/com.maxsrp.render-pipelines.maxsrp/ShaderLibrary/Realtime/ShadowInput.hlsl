#ifndef MAX_SHADOW_INPUT_INCLUDED
#define MAX_SHADOW_INPUT_INCLUDED

Texture2D _MainShadowAtlas; SamplerComparisonState sampler_MainShadowAtlas;
float4 _ShadowAtlasTile0, _ShadowAtlasTile1, _ShadowAtlasTile2, _ShadowAtlasTile3;
float _ShadowResolution0, _ShadowResolution1, _ShadowResolution2, _ShadowResolution3;
int _PcssCascadeCount;
Texture2D _NoiseTexture;
SamplerState sampler_PointRepeat;

SamplerState sampler_PointClamp;
int _PcssBlockerSamples;
int _PcssFilterSamples;
float _MainLightShadowsAvailable;
float4x4 _ShadowReceiverView;
float4x4 _ShadowNoiseWorldToLight;
float _ShadowNoiseWorldTexelSize;
float _ShadowTexelResolution;
float4 _CascadeBlendWidths;
float4 _ShadowDistanceFade;

//主灯光 世界空间->投影空间变换矩阵
float4x4 _ShadowVPMatrix0;
float4x4 _ShadowVPMatrix1;
float4x4 _ShadowVPMatrix2;
float4x4 _ShadowVPMatrix3;

// Cumulative cascade endpoints in the normalized camera depth range.
float4 _CascadeDepthRange; // near, far, inverse span, unused
float _Split0;
float _Split1;
float _Split2;
float _Split3;

float _OrthoWidth0;
float _OrthoWidth1;
float _OrthoWidth2;
float _OrthoWidth3;

float _OrthoDistance;
float _ShadowDepthHalfRange0;
float _ShadowDepthHalfRange1;
float _ShadowDepthHalfRange2;
float _ShadowDepthHalfRange3;
float4x4 _ShadowCasterVP;
float _ShadowLightTanAngularRadius;

float _ScreenWidth;
float _ScreenHeight;
float4 _ShadowNoiseTextureSize;

float _ShadingPointNormalBias0;
float _ShadingPointNormalBias1;
float _ShadingPointNormalBias2;
float _ShadingPointNormalBias3;

float _DepthNormalBias0;
float _DepthNormalBias1;
float _DepthNormalBias2;
float _DepthNormalBias3;

float _PcssMaxRadius0;
float _PcssMaxRadius1;
float _PcssMaxRadius2;
float _PcssMaxRadius3;

#endif