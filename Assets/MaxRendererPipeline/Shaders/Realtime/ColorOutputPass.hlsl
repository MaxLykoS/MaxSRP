#ifndef MAX_COLOR_OUTPUT_PASS_INCLUDED
#define MAX_COLOR_OUTPUT_PASS_INCLUDED

#include "UnityCG.cginc"

UNITY_DECLARE_TEX2D(_SceneColorHDR);
UNITY_DECLARE_TEX2D(_SceneDepthHDR);
float4 _ColorOutputParameters; // exposure, tone mapping, Y flip, dithering: 0 off / 1 linear / 2 sRGB

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

float3 ColorOutputDitherNoise(float2 pixelPosition)
{
    // Independent RGB sequences avoid color-dependent chroma error variance.
    // A single shared scalar leaves rings of changing chroma noise even with
    // a triangular PDF. Integer hashing also avoids IGN's repeating diagonals.
    uint2 pixel = (uint2)floor(pixelPosition);
    uint seed = pixel.x * 0x1f123bb5u + pixel.y * 0x5f356495u;
    uint3 bits = seed + uint3(0x68bc21ebu, 0x02e5be93u, 0x967a889bu);
    bits ^= bits >> 16;
    bits *= 0x7feb352du;
    bits ^= bits >> 15;
    bits *= 0x846ca68bu;
    bits ^= bits >> 16;
    float3 noise = (bits >> 8) * (1.0 / 16777216.0);
    // Stable in screen space; triangular density on [-1,1], no temporal sparkle.
    noise = noise * 2.0 - 1.0;
    noise = sign(noise) * (1.0 - sqrt(max(0.0, 1.0 - abs(noise))));
    return noise;
}

float3 ColorOutputEncodeSRGB(float3 color)
{
    return float3(
        color.r <= 0.0031308 ? 12.92 * color.r : 1.055 * pow(color.r, 1.0 / 2.4) - 0.055,
        color.g <= 0.0031308 ? 12.92 * color.g : 1.055 * pow(color.g, 1.0 / 2.4) - 0.055,
        color.b <= 0.0031308 ? 12.92 * color.b : 1.055 * pow(color.b, 1.0 / 2.4) - 0.055);
}

float3 ColorOutputDecodeSRGB(float3 encoded)
{
    return float3(
        encoded.r <= 0.04045 ? encoded.r / 12.92 : pow((encoded.r + 0.055) / 1.055, 2.4),
        encoded.g <= 0.04045 ? encoded.g / 12.92 : pow((encoded.g + 0.055) / 1.055, 2.4),
        encoded.b <= 0.04045 ? encoded.b / 12.92 : pow((encoded.b + 0.055) / 1.055, 2.4));
}

float3 DitherColorOutput(float3 color, float2 pixelPosition)
{
    if (_ColorOutputParameters.w < 0.5) return color;
    float3 offset = ColorOutputDitherNoise(pixelPosition) / 255.0;
    color = saturate(color);
    if (_ColorOutputParameters.w < 1.5) return saturate(color + offset);

    // Dither by at most +/- one 8-bit DISPLAY code, not one linear-light code.
    // Use the exact sRGB transfer pair; the destination still performs the final encode.
    float3 encoded = ColorOutputEncodeSRGB(color);
    encoded = saturate(encoded + offset);
    // Choose the final SDR code here. Hardware FLOAT -> sRGB8 is allowed
    // approximate thresholds, which can reintroduce color-dependent mean error
    // after otherwise unbiased dithering. Code centers survive that conversion
    // and the Editor's FP16 intermediate without shifting quantization decisions.
    encoded = floor(encoded * 255.0 + 0.5) / 255.0;
    return ColorOutputDecodeSRGB(encoded);
}

float4 ColorOutputFragment(ColorOutputVaryings input, out float depthOut : SV_Depth) : SV_Target
{
    float4 source = UNITY_SAMPLE_TEX2D(_SceneColorHDR, input.uv);
    depthOut = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_SceneDepthHDR, input.uv));
    float3 color = max(source.rgb, 0.0) * _ColorOutputParameters.x;
    if (_ColorOutputParameters.y > 0.5) color = ToneMapACES(color);
    color = DitherColorOutput(color, input.position.xy);
    // In the project's Linear color space, the sRGB destination performs display encoding.
    // Linear RenderTexture destinations retain linear values; do not apply another gamma curve.
    return float4(color, source.a);
}

#endif
