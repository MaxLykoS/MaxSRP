# 环境遮蔽 AO：PC XeGTAO / Mobile UE SSAO

本文保留 `GTAO.md` 文件名，作为统一 AO 配置与维护入口。PC 使用现有 Intel XeGTAO，Mobile 使用本机 UE **5.8.4 / CL 58902052** 的移动 SSAO 路径；两档已经对应不同实现，不再只是同一算法的两组参数。

## 配置与启用

- 独立配置资产为 `Assets/MaxRendererPipeline/DefaultAO.asset`，类型是 `MaxAOConfig`，由管线资产的 `AOConfiguration` 引用。选中配置可分别编辑 PC / Mobile 参数；创建其他配置使用 `Create > MaxSRP > Ambient Occlusion Configuration`。
- PC（GTAO）与 Mobile（SSAO）参数各有独立的 `Enabled`，默认均开启；它与场景组件启停共同控制 AO。关闭当前档会停止 AO 计算并释放资源，同时停用 `AOOnly` 显示；切换当前档开关会使受影响相机的共享 TAA 历史重新累积，修改非当前档开关不影响当前历史。
- 场景组件为 `MaxSRP/Ambient Occlusion`（`MaxAOSettings`），只负责所属场景的 AO 启停与 `AOOnly`。原场景的配置引用以隐藏兼容字段保留，当前管线的 AO 引用优先生效。
- **Platform 只在当前 MaxRendererPipelineAsset 上选择**，同时控制大气、后处理及其他模块；AO 配置中没有独立档位开关。总控与运行时刷新方式见 [RenderConfiguration.md](RenderConfiguration.md)。
- Game 相机使用所属场景，Scene 视图使用活动场景。Preview、Reflection、XR 及正交相机不执行 AO；缺少配置、当前档 Shader 或设备所需格式时停用该相机 AO 并释放其资源。
- `AOOnly` 将当前算法输出的可见度直接显示为灰度，最终输出旁路曝光、Tone Mapping 和 Dithering，便于检查遮挡本身。

配置、组件与场景引用沿用原资产的 `.meta` GUID。配置资产持有 GTAO Compute 和 SSAO Shader 的序列化引用；通过“管线资产 → AO 配置 → Shader”依赖链保留 Player 资源，不依靠运行时名称查找。

## PC：Intel XeGTAO

默认预算为 **Ultra、全分辨率、FP32 深度与运算、五层深度 mip**。Strength 保持 1、世界半径保持 1 米；提升搜索预算不改变遮蔽半径或通过增加强度制造更重的 AO。

| 质量 | 每像素 slice 数 | 每侧 step 数 | 双侧名义深度搜索样本数 |
|---|---:|---:|---:|
| Low（0） | 1 | 2 | 4 |
| Medium（1） | 2 | 2 | 8 |
| High（2） | 3 | 3 | 18 |
| Ultra（3，默认） | 9 | 3 | 54 |

这些数量只统计 horizon 搜索，不包含中心深度、法线、深度预处理或去噪访问。分辨率和缓冲格式不随质量档改变。

`DenoisePasses` 可选 0–3：0 对应上游关闭去噪，1 为 Sharp，2 为 Medium，3 为 Soft。默认 **1**；更多去噪轮数会变软，不将增加模糊当作更高质量。0 仍执行上游必要的最终转换，并使用中心权重 `1e4`、固定噪声，避免将 packed uint 工作值直接当作可见度。2/3 轮在 packed 工作域中 ping-pong，只有末轮恢复完整可见度；额外 scratch 仅在多轮时分配。

本轮保持现有 Intel 核心文件 `XeGTAO.hlsl`、`XeGTAOConstants.hlsl` 不动；质量选择、多个去噪 dispatch 和 Unity 输出格式适配位于 wrapper，不修改核心积分。固定上游版本为 [`a5b1686c7ea37788eeb3576b5be47f7c03db532c`](https://github.com/GameTechDev/XeGTAO/tree/a5b1686c7ea37788eeb3576b5be47f7c03db532c)，MIT 许可证保留在 Shader 目录。

维护入口：[MaxGTAOPass.cs](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxGTAOPass.cs>)、[MaxGTAO.compute](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/GTAO/MaxGTAO.compute>)。上游依据：[质量入口](https://github.com/GameTechDev/XeGTAO/blob/a5b1686c7ea37788eeb3576b5be47f7c03db532c/Source/Rendering/Shaders/vaGTAO.hlsl#L90-L130)、[去噪调度](https://github.com/GameTechDev/XeGTAO/blob/a5b1686c7ea37788eeb3576b5be47f7c03db532c/Source/Rendering/Effects/vaGTAO.cpp#L333-L348)。

## Mobile：UE 5.8 SSAO

Mobile 使用栅格全屏 Pass，不要求 GTAO 的 Compute / UAV 工作格式。默认按本地 UE 移动 SSAO 调度使用**全分辨率**；可在 Inspector 启用半宽高计算及重建。这里是 UE 5.8 SSAO，不是旧 UE 4.27 Mobile GTAO，也不是 HypeHype 的自研 GTAO。

### 默认参数与单位

| 参数 | 默认值 | 含义 |
|---|---:|---|
| Strength | 0.5 | 沿用本项目间接光 AO 混合入口 |
| RadiusInWorldSpace | false | 默认采用随视深度变化的 UE 屏幕半径模式 |
| ScreenRadius | 200 | UE 屏幕半径参数；不是 200 米，也不是固定 2 米 |
| Radius | 2 米 | 仅开启世界半径模式时使用 |
| Quality | Medium（2） | 当前 12 次名义双侧深度搜索，包含法线感知 quad 滤波 |
| Power | 2 | 可见度幂次调整，在 quad 滤波前应用 |
| Bias | 0.03 米 | UE 3 厘米的校准参数；实际偏移随视深度缩放 |
| FadeDistance / FadeRadius | 80 / 50 米 | 距离淡出控制 |
| HalfResolution | false | 默认全分辨率；开启后宽高各减半 |
| UpsampleQuality | 2 | 半分辨率时使用九 tap 双边重建 |
| Blur | true | 只在无共享 TAA 且全分辨率时增加 UE Smooth Pass |

### 搜索与滤波路径

| 质量 | 采样方向基数 × 每侧步数 × 双侧 | 名义深度搜索数 | quad 滤波 |
|---|---|---:|---|
| VeryLow（0） | 3 × 1 × 2 | 6 | 无 |
| Low（1） | 3 × 1 × 2 | 6 | 法线感知 |
| Medium（2） | 3 × 2 × 2 | 12 | 法线感知 |
| High（3） | 3 × 3 × 2 | 18 | 无 |
| VeryHigh（4） | 6 × 3 × 2 | 36 | 无 |

此表不包含 HZB、中心深度、法线和过滤读取，不能把搜索数直接当作总 texture sample 数。UE 质量 1/2 的 quad 滤波属于求值阶段本身，与 `Blur` 控制的额外 Smooth Pass 分开。

D3D11 / D3D12、Metal、Vulkan 且满足 Shader Model 4.5 要求的设备使用 `MAX_SSAO_INLINE_QUAD` 变体，保留基于精细导数的内联 quad 滤波。默认栅格变体仅要求 Shader Model 3.5，不要求 Compute / UAV；GLES3 和无法启用高档变体的设备通过额外栅格 Pass 显式读取 quad 邻域。因此 GLES 的 Pass 数可能更多，不能宣称各后端成本完全一致。

半分辨率开启时，`UpsampleQuality` 为 0：双线性，1：四 tap 双边，2：九 tap 双边。half 工作纹理使用 RGBA8：R 保存 AO，GBA 按 UE `PackR24F` 约定保存线性深度；编码时由米换算为 UE 厘米，解码后恢复米。该格式不支持渲染 / 采样 / 线性过滤时回退到 RGBA16F，但仍保存相同的归一化 packed 深度字节，并非改用 half 原生深度。最终输出为全分辨率标量可见度，默认使用 R8，格式不支持时回退到 R16F。

额外 Smooth Pass 仅在 `Blur=true`、没有共享 TAA、且全分辨率时执行；半分辨率使用自身的重建路径。`Blur=false` 不会关闭质量 1/2 内置的 quad 滤波。空间噪声使用 UE 原始量化方向表和时间偏移；有共享 TAA 时跟随它的相位，无 TAA 时固定相位。

### UE 源码对应与适配边界

- [PostProcessAmbientOcclusionMobile.cpp](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/PostProcess/PostProcessAmbientOcclusionMobile.cpp:356>)：移动 AO 的全 / 半分辨率、无 TAA Smooth 与上采样调度。
- [PostProcessAmbientOcclusion.usf](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/PostProcessAmbientOcclusion.usf:252>)：`MainPSandCS` 求值、质量采样集、淡出 / Power、quad 滤波；同文件第 560 行附近为 Smooth，第 606 行附近为半分辨率重建。
- [PostProcessAmbientOcclusionCommon.ush](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/PostProcessAmbientOcclusionCommon.ush:243>)：`WedgeWithNormal` 的 UE SSAO 近似。

移植保留上述数学与主要分支，但适配了 MaxSRP 的数据与单位：使用现有 GBuffer 世界法线并转换到视空间；米替代 UE 厘米；保留 half 工作纹理的 UE packed 24-bit 深度约定；只建立当前参数实际使用的 half-resolution HZB mip 0，采用 2×2 最远深度约定。未接入完整多级 AO mip 混合、UE Depth Bounds Test 和 Shading Model 排除掩码。不能将此移植表述为 UE 全部 AO 功能或整个渲染流程逐像素等价。

维护入口：[MaxSSAOPass.cs](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxSSAOPass.cs>)、[MaxSSAO.hlsl](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/SSAO/MaxSSAO.hlsl>)、[MaxSSAO.shader](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/SSAO/MaxSSAO.shader>)。Shader 名为 `Hidden/MaxSRP/MobileSSAO`；源码内保留对应源文件与移植差异说明。

## 共享 Lighting、TAA 与资源生命周期

两条路径输出相同的 0–1 标量可见度，使用既有 Lighting 接口。AO 与烘焙天空可见度取较小值，再按 Strength 混合，避免相同遮挡重复相乘；太阳 Probe GI 使用该屏幕可见度调制，人工光 Lightmap 和实时直射沿用既有路径。Strength 在公共 Lighting 中应用，不在 SSAO 求值和 Lighting 两处重复应用；这保留本项目对烘焙可见度的组合规则。

两档均不保存独立 AO 时间历史。AO 在 Lighting 前计算，随场景颜色进入共享 TAA；投影和噪声使用当前相机的公共 jitter / 相位。管线总控切档在 Render 边界重建并重新建立历史；AO 配置引用、当前档参数、Shader 和 AOOnly 仍进入 HistoryKey，使受影响相机重新累积。更改非当前档参数不影响正在渲染的档位。

每个相机独立持有 AO 资源。切到另一算法时释放不再使用的路径；尺寸 / 格式变化、RT 丢失、关闭 AO 和销毁相机均沿用资源生命周期处理。屏幕空间 AO 不能包含屏外或深度缓冲中不可见的遮挡几何。

## 验证与范围

`Tools/ValidateLightingShaderRetention.ps1` 检查配置 / Shader 默认引用并编译 Player / Editor C#；`Tools/ValidateShaders.ps1` 检查 GTAO Compute、SSAO 栅格入口及内联 quad / 普通 Z / 底部原点分支，并检查默认 SSAO 变体可编译为不含 Compute / UAV 的 SM4。

以下 GPU 记录属于管线总控配置迁移前的历史结果，不表示本轮配置重构已经重跑这些检查。

2026-11-14：Player / Editor C# API 编译、默认引用及 Shader / 变体编译检查通过。使用 Unity 2022.3.62f2c1、RTX 3080 Ti、D3D11，在仓库外的临时工程直接调用生产 Pass 的 `Record`，完成 **301 项 GPU 检查**：PC 四档质量与 0–3 轮去噪；Mobile 五档质量、全 / 半分辨率与三种上采样、无 TAA Smooth；96×64 和 97×65 尺寸；平面、深度阶跃、背景、距离淡出及缺少配置时的资源释放。内联 quad 路径通过后，又仅在临时工程中强制后端能力选择为显式 quad，重跑同一套 301 项检查，也全部通过；这验证了回退算法与调度，仍是 D3D11，不能当作 GLES 真机结果。检查覆盖有限数值、可见度范围和遮挡趋势，不代表真实场景逐像素匹配 UE。PC 平面斜视边角的小幅自遮挡与原版 XeGTAO 独立 CPU 计算一致，未为通过测试改动其积分。

同日解除外部场景重载提示后，在原工程 BistroTOD 的 1920×1080 Game 相机完成 **PC → Mobile → PC 实时切档**：PC 持有四张 GTAO RT，Mobile 持有 960×540 R32F HZB 和 1920×1080 R8 可见度；切档后上一算法的 RT 均释放。两档 GPU 读回均为有限的 0–1 可见度，包含有效遮挡。切换时 LightingHistoryKey 改变，TAA Sample 重置后重新累积；恢复 PC 时再次重置。SSAO Shader 消息为空，Console 无 Error；保留了导入重试的旧警告和 Intel 核心循环展开提示。最终恢复 PC / Ultra / 一轮去噪，配置和场景没有因验证变脏。

本轮测试源与测试场景不加入工程，报告和 AO 输出图位于已忽略目录 `OfflineBaseline/AmbientOcclusion/`，实际场景快照记录在 `Bistro-live-validation.json`。

后续验收应覆盖 PC / Mobile 切档、0–3 轮 GTAO 去噪、SSAO 全 / 半分辨率、三种重建、无 TAA Smooth、GLES 显式 quad 回退、背景 / 奇数尺寸 / 深度突变及参数更改后的历史失效。桌面 D3D11 编译或离屏检查不能代替 Metal / Vulkan / GLES 移动真机的画质、带宽与功耗验证。
