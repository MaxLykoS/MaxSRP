#ifndef MAX_SHADOW_INPUT_INCLUDED
#define MAX_SHADOW_INPUT_INCLUDED

#define CASADESHADOW_COUNT 4

UNITY_DECLARE_TEX2D(_MainShadowMap);
float4 _ShadowParams; //x is depthBias,y is normal bias,z is strength, w is cascadeCount

//主灯光 世界空间->投影空间变换矩阵
float4x4 _WorldToMainLightCascadeShadowMapSpaceMatrices[CASADESHADOW_COUNT];

// The culling sphere.The first three components of the vector describe the sphere
// center, and the last component specifies the radius.
float4 _CascadeCullingSpheres[CASADESHADOW_COUNT]; 

int _ShadowMapWidth;

#endif