#ifndef MAX_CLOUD_LIGHTING_INCLUDED
#define MAX_CLOUD_LIGHTING_INCLUDED
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "CloudLayer.hlsl"
float3 _MaxCloudBakeSun;
int _MaxCloudSteps;
struct CloudBakeVaryings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
CloudBakeVaryings CloudBakeVertex(uint id : SV_VertexID)
{
    CloudBakeVaryings o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * 2 - 1, 0, 1);
#if UNITY_UV_STARTS_AT_TOP
    o.uv.y = 1 - o.uv.y;
#endif
    return o;
}
float2 CloudBakeFragment(CloudBakeVaryings input) : SV_Target
{
    float shape = sqrt(MaxCloudShapeAtUV(input.uv));
    float startHeight = 0.2 + 0.45 * shape;
    float stepLength = _MaxCloudGeometry.y / max(abs(_MaxCloudBakeSun.y), 0.15) / max(_MaxCloudSteps, 1);
    float opticalDepth = 0;
    [loop] for (int j = 0; j < _MaxCloudSteps; ++j)
    {
        float distance = (j + 0.5) * stepLength;
        float2 uv = input.uv + _MaxCloudBakeSun.xz * distance / _MaxCloudShape.w;
        float height = startHeight + _MaxCloudBakeSun.y * distance / _MaxCloudGeometry.y;
        float localShape = sqrt(MaxCloudShapeAtUV(uv));
        float volume = saturate((localShape - height) * 6) * step(0, height);
        opticalDepth += volume * stepLength * _MaxCloudShape.z / _MaxCloudGeometry.y;
    }
    return float2(exp(-opticalDepth), lerp(1, 0.45, shape));
}
#endif
