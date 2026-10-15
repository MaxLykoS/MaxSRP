#ifndef MAX_COLOR_OUTPUT_PASS_INCLUDED
#define MAX_COLOR_OUTPUT_PASS_INCLUDED

#include "UnityCG.cginc"

UNITY_DECLARE_TEX2D(_SceneColorHDR);
UNITY_DECLARE_TEX2D(_SceneDepthHDR);
float4 _ColorOutputParameters; // exposure multiplier, tone mapping mode, RT-to-backbuffer Y flip, unused

struct ColorOutputVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_POSITION;
};

ColorOutputVaryings ColorOutputVertex(uint vertexID : SV_VertexID)
{
    ColorOutputVaryings output;
    output.uv = float2((vertexID << 1) & 2, vertexID & 2);
    output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
#if UNITY_UV_STARTS_AT_TOP
    output.uv.y = 1.0 - output.uv.y;
#endif
    if (_ColorOutputParameters.z > 0.5) output.uv.y = 1.0 - output.uv.y;
    return output;
}

// Narkowicz's fitted ACES filmic curve; input/output are linear RGB.
// This is a compact curve approximation, not the full ACES color-management transform.
float ToneMapACESChannel(float value)
{
    // Reorder the same rational curve for large inputs, avoiding Inf/Inf.
    // Positive infinity is an over-range highlight and maps to white.
    if (isnan(value) || value <= 0.0) return 0.0;
    if (value > 1.0)
    {
        float inverse = 1.0 / value;
        return saturate((2.51 + 0.03 * inverse) /
            (2.43 + 0.59 * inverse + 0.14 * inverse * inverse));
    }
    return saturate((value * (2.51 * value + 0.03)) /
        (value * (2.43 * value + 0.59) + 0.14));
}

float3 ToneMapACES(float3 color)
{
    return float3(ToneMapACESChannel(color.r), ToneMapACESChannel(color.g), ToneMapACESChannel(color.b));
}

float4 ColorOutputFragment(ColorOutputVaryings input, out float depthOut : SV_Depth) : SV_Target
{
    float4 source = UNITY_SAMPLE_TEX2D(_SceneColorHDR, input.uv);
    depthOut = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_SceneDepthHDR, input.uv));
    float3 color = max(source.rgb, 0.0) * _ColorOutputParameters.x;
    if (_ColorOutputParameters.y > 0.5) color = ToneMapACES(color);
    // In the project's Linear color space, the sRGB destination performs display encoding.
    // Linear RenderTexture destinations retain linear values; do not apply another gamma curve.
    return float4(color, source.a);
}

#endif
