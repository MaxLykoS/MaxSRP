#ifndef MAX_TERRAIN_GEOMETRY_PASSES_INCLUDED
#define MAX_TERRAIN_GEOMETRY_PASSES_INCLUDED
#include "Packages/com.maxsrp.render-pipelines.maxsrp/ShaderLibrary/Realtime/Core/Packing.hlsl"

// The host binds the jittered raster VP for GBuffer and Motion. Use a custom
// uniform because Unity can overwrite its built-in VP when executing a draw.
float4x4 _MaxRasterVP;
float4x4 _MaxTerrainShadowVP;
float4x4 _MaxUnjitteredVP, _MaxPreviousVP, _MaxPreviousView;
float4 _MeshPreviousObserver;

float4 MaxTerrainRasterPosition(float3 world)
{
    return mul(_MaxRasterVP, float4(world, 1));
}

Varyings MaxTerrainGBufferVertex(Attributes input)
{
    Varyings output = Vert(input);
    output.position = MaxTerrainRasterPosition(output.world);
    return output;
}

Varyings MaxTerrainShadowVertex(Attributes input)
{
    Varyings output = Vert(input);
    // C#已经使用GL.GetGPUProjectionMatrix(projection,true)，这里不再翻Y。
    output.position = mul(_MaxTerrainShadowVP, float4(output.world, 1));
    return output;
}

float4 MaxTerrainShadowFragment(Varyings input) : SV_Target
{
    clip(input.diagnostic.w - 0.5);
    clip(0.01 - input.diagnostic.z);
    return 0;
}

struct MaxTerrainMotionVaryings
{
    float4 position : SV_POSITION;
    float4 current : TEXCOORD0;
    float4 previous : TEXCOORD1;
    float previousDepth : TEXCOORD2;
    float invalid : TEXCOORD3;
    float moved : TEXCOORD4;
};

MaxTerrainMotionVaryings MaxTerrainMotionVertex(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float4 patch = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshPatch);
    float4 parameters = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshParams);
    float4 previousParameters = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshPreviousParams);
    float valid = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshMotionValidity);
    float4 currentDiagnostic, previousDiagnostic;
    float3 world = MeshReconstructWorld(input, patch, parameters, _MeshObserver, currentDiagnostic);
    // 固定源高度数据不随帧改变，可通过当前Clipmap重采旧morph位置。
    // 若旧位置的四点足迹已经离开当前窗口，重建函数会标记非法，明确拒绝历史。
    float3 previousWorld = MeshReconstructWorld(input, patch, previousParameters,
        _MeshPreviousObserver, previousDiagnostic);
    MaxTerrainMotionVaryings output;
    output.position = MaxTerrainRasterPosition(world);
    output.current = mul(_MaxUnjitteredVP, float4(world, 1));
    output.previous = mul(_MaxPreviousVP, float4(previousWorld, 1));
    output.previousDepth = -mul(_MaxPreviousView, float4(previousWorld, 1)).z;
    output.moved = any(abs(world - previousWorld) > 0.000001) ? 1 : 0;
    output.invalid = max(1 - valid, max(currentDiagnostic.z, previousDiagnostic.z));
    output.invalid = max(output.invalid, 1 - min(currentDiagnostic.w, previousDiagnostic.w));
    return output;
}

MAX_VELOCITY_TARGET MaxTerrainMotionFragment(MaxTerrainMotionVaryings input) : SV_Target
{
    // New patches and missing previous height samples must reject history.
    bool reject = input.invalid > 0.01 || input.previous.w <= 0 || input.current.w <= 0 || input.previousDepth <= 0;
    return MaxWriteVelocity(input.current, input.previous, input.moved > 0, reject);
}

#endif
