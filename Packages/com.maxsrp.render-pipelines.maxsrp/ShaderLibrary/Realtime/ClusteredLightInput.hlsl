#ifndef MAX_CLUSTERED_LIGHT_INPUT_INCLUDED
#define MAX_CLUSTERED_LIGHT_INPUT_INCLUDED

// Shared CPU/GPU layout: two Vector4 values, 32 bytes per point light.
struct MaxPointLightData
{
    float4 positionRange;
    float4 color;
};

StructuredBuffer<MaxPointLightData> _MaxPointLights;
int _MaxPointLightCount;
float _MaxUsePointLightBuffer;
float _MaxClusterEnabled;

// Canonical CPU projection: Unity view space looks along -Z, clip Z is [-1,1],
// UV Y points up. These matrices never contain temporal jitter or GPU flips.
float4 _MaxClusterGrid;    // Integer-valued XYZ cell count, W index capacity.
float4 _MaxClusterScreen;  // Viewport width, height, XY tile size, unused.
float4 _MaxClusterDepth;   // Near, far, orthographic flag, unused.
float4x4 _MaxClusterWorldToView;
float4x4 _MaxClusterProjection;
float4x4 _MaxClusterInvProjection;

#if defined(MAX_CLUSTER_COMPUTE)
RWStructuredBuffer<uint> _MaxClusterCounts;
RWStructuredBuffer<uint> _MaxClusterLightIndices;
#else
StructuredBuffer<uint> _MaxClusterCounts;
StructuredBuffer<uint> _MaxClusterLightIndices;
#endif

bool MaxClusterParametersValid()
{
    return all(isfinite(_MaxClusterGrid)) && all(_MaxClusterGrid >= 1.0)
        && all(isfinite(_MaxClusterScreen.xyz))
        && all(_MaxClusterScreen.xyz > 0.0)
        && all(isfinite(_MaxClusterDepth.xyz))
        && _MaxClusterDepth.y > _MaxClusterDepth.x
        && (_MaxClusterDepth.z > 0.5 || _MaxClusterDepth.x > 0.0);
}

float MaxClusterSliceDepth(uint slice)
{
    float fraction = (float)slice / (float)_MaxClusterGrid.z;
    if (_MaxClusterDepth.z > 0.5)
        return lerp(_MaxClusterDepth.x, _MaxClusterDepth.y, fraction);
    return _MaxClusterDepth.x * exp2(log2(_MaxClusterDepth.y / _MaxClusterDepth.x) * fraction);
}

// Returns false for any unsupported/invalid lookup. The caller then evaluates
// the complete light buffer, including overflow cells, rather than losing light.
bool GetPointLightList(float3 positionWS, out uint start, out uint count)
{
    start = 0;
    count = 0;
    if (_MaxUsePointLightBuffer < 0.5 || _MaxClusterEnabled < 0.5 || !MaxClusterParametersValid())
        return false;

    float4 positionVS = mul(_MaxClusterWorldToView, float4(positionWS, 1.0));
    float eyeDepth = -positionVS.z;
    float4 clip = mul(_MaxClusterProjection, positionVS);
    if (!all(isfinite(clip)) || !isfinite(eyeDepth) || abs(clip.w) < 1e-8
        || eyeDepth < _MaxClusterDepth.x || eyeDepth > _MaxClusterDepth.y)
        return false;

    float2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (!all(isfinite(uv)) || any(uv < 0.0) || any(uv > 1.0))
        return false;

    float depthFraction;
    if (_MaxClusterDepth.z > 0.5)
        depthFraction = (eyeDepth - _MaxClusterDepth.x) / (_MaxClusterDepth.y - _MaxClusterDepth.x);
    else
        depthFraction = log2(eyeDepth / _MaxClusterDepth.x) / log2(_MaxClusterDepth.y / _MaxClusterDepth.x);
    if (!isfinite(depthFraction))
        return false;

    uint3 cell;
    cell.xy = min((uint2)floor(uv * _MaxClusterScreen.xy / _MaxClusterScreen.z), (uint2)_MaxClusterGrid.xy - 1u);
    cell.z = min((uint)max(floor(depthFraction * _MaxClusterGrid.z), 0.0), (uint)_MaxClusterGrid.z - 1u);
    uint clusterIndex = (cell.z * (uint)_MaxClusterGrid.y + cell.y) * (uint)_MaxClusterGrid.x + cell.x;
    count = _MaxClusterCounts[clusterIndex];
    if (count > (uint)_MaxClusterGrid.w)
        return false;
    start = clusterIndex * (uint)_MaxClusterGrid.w;
    return true;
}

uint GetPointLightListIndex(uint start, uint offset)
{
    return _MaxClusterLightIndices[start + offset];
}

#endif
