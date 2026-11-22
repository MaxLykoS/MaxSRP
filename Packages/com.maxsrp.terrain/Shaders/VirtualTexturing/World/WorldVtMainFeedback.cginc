#ifndef DIRECT_DELTA_WORLD_VT_MAIN_FEEDBACK_INCLUDED
#define DIRECT_DELTA_WORLD_VT_MAIN_FEEDBACK_INCLUDED

// 唯一反馈路径：在地形GBuffer PS中稀疏写请求，仍与材质输出共用一次绘制。
// MaxSRP四个MRT占OM槽0..3，D3D11像素UAV必须从u4开始；C#同步绑定槽4。
#if defined(DD_WORLD_VT_MAIN_FEEDBACK)
RWStructuredBuffer<uint> _DDVtFeedbackRaw : register(u4);
float4 _DDVtFeedbackSampling; // 每轴采样间隔、块内 X/Y 相位、原始缓冲 uint 容量。

void DDVtWriteMainFeedback(float2 worldXZ, uint desiredMip, float4 screenPosition)
{
    uint capacity = (uint)_DDVtFeedbackSampling.w;
    if (capacity <= 1u || !DDVtInsideWorld(worldXZ)) return;

    // 直接沿当前 GPU 渲染目标的 SV_Position 格网轮换采样。
    // Unity 的 HDR 相机可能先画到相机尺寸的中间 RT：右半屏相机的
    // pixelRect.x 是 640，实际 draw 的 SV_Position.x 却从 0 开始。
    // 因此不能减去 CPU 的 pixelRect，也不在这里重做视口范围检查；
    // GPU 已裁剪到真实视口，且这个 UAV 只在 Delta 地形 draw 期间绑定。
    // 不同目标原点只会平移采样相位，仍在 divisor² 帧中遍历整个格网。
    uint2 pixel = (uint2)floor(screenPosition.xy);
    uint divisor = max((uint)_DDVtFeedbackSampling.x, 1u);
    uint2 phase = (uint2)_DDVtFeedbackSampling.yz;
    // 受支持的 /4、/8、/16 均为 2 的幂，用掩码选择像素，避免逐片元整数除法。
    if (any((pixel & (divisor - 1u)) != phase)) return;

    // 记录像素想要的精度。即使消费端正在采粗父页或直接回退，
    // 仍将目标地址送回 CPU，让 AVT 扩张并填充它真正需要的细页。
    DDVtTableAddress address = DDVtResolveAddress(worldXZ, desiredMip);
    uint key = DDVtFeedbackKey(address);
    uint index;
    InterlockedAdd(_DDVtFeedbackRaw[0], 1u, index);
    index++;
    if (index < capacity) _DDVtFeedbackRaw[index] = key;
}
#endif

#endif
