#ifndef DIRECT_DELTA_WORLD_VT_BC3_COMMON_INCLUDED
#define DIRECT_DELTA_WORLD_VT_BC3_COMMON_INCLUDED

// PS 参考编码器与批量 CS 共用同一份 BC3 算法。端点选择、并列误差的选择顺序、
// alpha selector 以及 invalid/mixed 的旧规则均保持，不能单独修改某个入口。
// 输入为压缩域：底色 RGB 已是 sRGB 编码值，其余通道是线性 [0,1]。
// uint4 依次保存一个 BC3 块的16个原始字节，不是可直接用于材质的像素。

float3 EncodeSrgb(float3 linearRgb)
{
    linearRgb = saturate(linearRgb);
    return lerp(1.055 * pow(linearRgb, 1.0 / 2.4) - 0.055,
        12.92 * linearRgb, step(linearRgb, 0.0031308));
}

uint PackRgb565(float3 rgb)
{
    uint3 code = (uint3)round(saturate(rgb) * float3(31.0, 63.0, 31.0));
    return (code.r << 11) | (code.g << 5) | code.b;
}

float3 UnpackRgb565(uint code)
{
    uint r = code >> 11;
    uint g = (code >> 5) & 63u;
    uint b = code & 31u;
    return float3((r << 3) | (r >> 2), (g << 2) | (g >> 4),
        (b << 3) | (b >> 2)) / 255.0;
}

bool IsValid(uint validMask, uint index)
{
    return (validMask & (1u << index)) != 0u;
}

float3 FarthestRgb(float4 pixels[16], uint validMask, float3 origin)
{
    float maxDistance = -1.0;
    float3 farthest = origin;
    [unroll] for (uint i = 0u; i < 16u; ++i)
    {
        float3 delta = pixels[i].rgb - origin;
        float distanceSquared = dot(delta, delta);
        if (IsValid(validMask, i) && distanceSquared > maxDistance)
        {
            maxDistance = distanceSquared;
            farthest = pixels[i].rgb;
        }
    }
    return farthest;
}

uint4 EncodeBc3(float4 pixels[16], uint validMask, bool encodeValidity)
{
    // 无效纹素不参与端点拟合，避免它们的调试色或占位法线污染有效区域。
    float4 minimum = 1.0;
    float4 maximum = 0.0;
    [unroll] for (uint i = 0u; i < 16u; ++i)
    {
        if (IsValid(validMask, i))
        {
            minimum = min(minimum, pixels[i]);
            maximum = max(maximum, pixels[i]);
        }
    }

    // 选两份距离较远的真实样本，使颜色轴能跟随通道之间的变化方向。
    float3 endpoint0 = FarthestRgb(pixels, validMask, (minimum.rgb + maximum.rgb) * 0.5);
    float3 endpoint1 = FarthestRgb(pixels, validMask, endpoint0);
    bool mixedValidity = encodeValidity && validMask != 0xffffu;
    if (mixedValidity)
    {
        // 数据图的 B 端点分别固定为 1 和 0。RGB 索引虽共享，
        // 四个颜色中仍然有两个能表示有效、两个能表示无效。
        endpoint0.b = 1.0;
        endpoint1.b = 0.0;
        // 有效像素只能用第一个端点及靠近它的插值项。延长另一端的
        // AO/粗糙度范围，尽量让这个插值项也覆盖原始有效样本。
        endpoint1.rg = saturate(3.0 * endpoint1.rg - 2.0 * endpoint0.rg);
    }

    uint color0 = PackRgb565(endpoint0);
    uint color1 = PackRgb565(endpoint1);
    // BC3 的颜色块总有四个颜色。排序后按实际端点的 B 判断有效性。
    uint colorHigh = max(color0, color1);
    uint colorLow = min(color0, color1);
    float3 c0 = UnpackRgb565(colorHigh);
    float3 c1 = UnpackRgb565(colorLow);
    float3 c2 = (2.0 * c0 + c1) / 3.0;
    float3 c3 = (c0 + 2.0 * c1) / 3.0;

    uint alpha0 = (uint)round(maximum.a * 255.0);
    uint alpha1 = (uint)round(minimum.a * 255.0);
    float alphaScale = 7.0 / max((float)alpha0 - (float)alpha1, 1.0);
    uint alphaIndicesLow = 0u;
    uint alphaIndicesHigh = 0u;
    uint colorIndices = 0u;
    [unroll] for (uint selectorIndex = 0u; selectorIndex < 16u; ++selectorIndex)
    {
        bool valid = IsValid(validMask, selectorIndex);
        uint selector = 0u;
        if (valid)
        {
            float3 d0 = pixels[selectorIndex].rgb - c0;
            float3 d1 = pixels[selectorIndex].rgb - c1;
            float3 d2 = pixels[selectorIndex].rgb - c2;
            float3 d3 = pixels[selectorIndex].rgb - c3;
            float e0 = !mixedValidity || c0.b > 0.5 ? dot(d0, d0) : 8.0;
            float e1 = !mixedValidity || c1.b > 0.5 ? dot(d1, d1) : 8.0;
            float e2 = !mixedValidity || c2.b > 0.5 ? dot(d2, d2) : 8.0;
            float e3 = !mixedValidity || c3.b > 0.5 ? dot(d3, d3) : 8.0;
            selector = e0 <= e1 ? 0u : 1u;
            float error = min(e0, e1);
            if (e2 < error)
            {
                selector = 2u;
                error = e2;
            }
            if (e3 < error) selector = 3u;
        }
        else if (mixedValidity)
        {
            // 无效纹素直接选 B=0 的端点，保证解码后仍然触发父页回退。
            selector = c0.b < 0.5 ? 0u : 1u;
        }
        colorIndices |= selector << (selectorIndex * 2u);

        uint alphaSelector = 0u;
        if (valid && alpha0 != alpha1)
        {
            float level = clamp((alpha0 - pixels[selectorIndex].a * 255.0) * alphaScale, 0.0, 7.0);
            uint nearest = (uint)round(level);
            // 端点 0、1 排在前面，索引 2..7 对应从端点 0 到 1 的插值。
            alphaSelector = nearest == 0u ? 0u : (nearest == 7u ? 1u : nearest + 1u);
        }
        if (selectorIndex < 8u) alphaIndicesLow |= alphaSelector << (selectorIndex * 3u);
        else alphaIndicesHigh |= alphaSelector << ((selectorIndex - 8u) * 3u);
    }

    // BC3 的 16 字节依次是 A 端点和索引、RGB565 端点和索引。
    // uint4 在这里是压缩块字节容器，GPU Copy 后由 BC3 采样器解码。
    return uint4(alpha0 | (alpha1 << 8) | (alphaIndicesLow << 16),
        (alphaIndicesLow >> 16) | (alphaIndicesHigh << 8),
        colorHigh | (colorLow << 16), colorIndices);
}

#endif
