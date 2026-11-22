#ifndef DIRECT_DELTA_WORLD_VT_OVERLAY_COMMON_INCLUDED
#define DIRECT_DELTA_WORLD_VT_OVERLAY_COMMON_INCLUDED

// 道路/贴花的临时合成契约，不是物理池格式。
// RGB 在 shader 内始终是线性量；只有 Target0 的 sRGB RT 负责底色存储转换。
// 世界法线采用仿射 XYZ 编码，禁止在此对 oct RG 做硬件 alpha 混合。
struct DDOverlayMrtOutputs
{
    float4 colorOpacity : SV_Target0;        // Albedo RGB / Opacity，sRGB RT。
    float4 normalOpacity : SV_Target1;       // world normal XYZ * .5 + .5 / Opacity。
    float4 attributesOpacity : SV_Target2;   // AO / Roughness / HeightCode / Opacity。
};

float DDOverlayHeightCode(float materialHeight)
{
    // 沿用旧 stage 的 1..255 编码。真实 Height=0 也是合法材质，不能写字节 0。
    // 该高度只留在临时图，原 BC3 编码器不会把它存进物理池。
    return (1.0 + round(saturate(materialHeight) * 254.0)) / 255.0;
}

DDOverlayMrtOutputs DDOverlayPackTargets(float3 albedo, float3 worldNormal,
    float ao, float roughness, float heightCode, float opacity)
{
    DDOverlayMrtOutputs output;
    // 预乘 source-over：与 Blend One OneMinusSrcAlpha 配对，不能再乘 SrcAlpha。
    // 基底 opacity=1；覆盖层各附件 alpha 只用于本次混合，不是最终覆盖率。
    // BaseColor的范围约定在预乘之前执行；直接等UNorm RT截断会改变半透明覆盖结果。
    output.colorOpacity = float4(saturate(albedo), 1) * opacity;
    output.normalOpacity = float4(worldNormal * .5 + .5, 1) * opacity;
    output.attributesOpacity = float4(ao, roughness, heightCode, 1) * opacity;
    return output;
}

#endif
