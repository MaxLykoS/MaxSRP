#define MAX_PCSS_DIAGNOSTIC_QUAD 1
#include "../Shaders/Realtime/PBRLitPass.hlsl"

void PatternSurface(v2f input, out float3 position, out float3 normal, out float valid)
{
    float depth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_GDepth, input.uv));
#if defined(UNITY_REVERSED_Z)
    valid = depth > 0.0 ? 1.0 : 0.0;
#else
    valid = depth < 1.0 ? 1.0 : 0.0;
#endif
    float4 clip = float4(input.uv * 2 - 1, DeviceDepthToClipDepth(depth), 1);
#if UNITY_UV_STARTS_AT_TOP
    clip.y = -clip.y;
#endif
    float4 world = mul(unity_MatrixInvVP, clip);
    position = valid > 0.5 ? world.xyz / world.w : float3(0, 0, 0);
    normal = normalize(UNITY_SAMPLE_TEX2D(_GBuffer1, input.uv).rgb * 2 - 1);
}

float4 PatternRawFragment(v2f input) : SV_Target
{
    float3 position, normal;
    float valid;
    PatternSurface(input, position, normal, valid);
    float visibility = MainLightShadow(position, normal);
    return valid > 0.5 ? float4(visibility.xxx, 1) : float4(1, 0, 1, 1);
}

float4 PatternFragment(v2f input) : SV_Target
{
    float3 position, normal;
    float valid;
    PatternSurface(input, position, normal, valid);
    float visibility = MainLightShadowFiltered(position, normal, input.pH.xy, valid);
    return valid > 0.5 ? float4(visibility.xxx, 1) : float4(1, 0, 1, 1);
}
