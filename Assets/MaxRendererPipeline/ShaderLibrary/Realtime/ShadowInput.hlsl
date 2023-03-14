#ifndef MAX_SHADOW_INPUT_INCLUDED
#define MAX_SHADOW_INPUT_INCLUDED

#define MAX_CASADESHADOW_COUNT 4

CBUFFER_START(MaxShadow)

//主灯光 世界空间->投影空间变换矩阵
float4x4 _MaxWorldToMainLightCascadeShadowMapSpaceMatrices[MAX_CASADESHADOW_COUNT];

float4 _MaxCascadeCullingSpheres[MAX_CASADESHADOW_COUNT];

CBUFFER_END

#endif