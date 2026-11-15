#ifndef MAX_TERRAIN_GEOMETRY_PASSES_INCLUDED
#define MAX_TERRAIN_GEOMETRY_PASSES_INCLUDED

// 这两个Pass只重建几何，不取材质、不查VT、不写反馈。
// 宿主负责绑定每个CSM tile和共享MotionVectors目标。
float4x4 _MaxTerrainShadowVP;
float4x4 _MaxUnjitteredVP, _MaxPreviousVP, _MaxPreviousView;
float4 _MeshPreviousObserver;

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
    output.position = mul(UNITY_MATRIX_VP, float4(world, 1));
    output.current = mul(_MaxUnjitteredVP, float4(world, 1));
    output.previous = mul(_MaxPreviousVP, float4(previousWorld, 1));
    output.previousDepth = -mul(_MaxPreviousView, float4(previousWorld, 1)).z;
    output.invalid = max(1 - valid, max(currentDiagnostic.z, previousDiagnostic.z));
    output.invalid = max(output.invalid, 1 - min(currentDiagnostic.w, previousDiagnostic.w));
    return output;
}

float4 MaxTerrainMotionFragment(MaxTerrainMotionVaryings input) : SV_Target
{
    // RG是无jitter纹理UV位移；Z为上一眼空间正深度；W=-1显式拒绝历史。
    // 新patch、quarter拓扑变化、源重置或缺失旧高度足迹都不能伪装成零速度。
    if (input.invalid > 0.01 || input.previous.w <= 0 || input.current.w <= 0 || input.previousDepth <= 0)
        return float4(0, 0, 0, -1);
    float2 velocity = 0.5 * (input.current.xy / input.current.w - input.previous.xy / input.previous.w);
    #if UNITY_UV_STARTS_AT_TOP
        velocity.y = -velocity.y;
    #endif
    return float4(velocity, input.previousDepth, 1);
}

#endif
