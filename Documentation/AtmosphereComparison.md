# 大气现状差异：MaxSRP 与 UE

核查日期：2026-11-13。对照当前工作区与本机 UE **5.8.4 / CL 58902052**，按现有 SkyView / AP 查表方案比较。PC 与 Mobile 的 SkyView LUT 均为既定设计。

已对齐的介质参数、单位、地面反弹、太阳角尺寸、积分分段、LUT 格式、AP 通道布局和覆盖范围不再列入差异表。本文保留当前仍有意义的模型、功能和预算区别。

## 1. 当前状态

Max Mobile 的主要采样预算与资源格式已对齐 UE 移动预设。Max PC 保留 CS 相函数、解析多散射反馈与 32 个球面方向的项目选择。PC 优先改进有明确画质收益的模型与算法，采样和存储预算按实际收益选择。

两档 AP 都通过 Compute 直接生成**一张原生 3D RGBA16F 纹理**，RGB 保存入散射，Alpha 保存平均透射率；运行时只做一次硬件三线性过滤查询。覆盖 **96 km**，默认从相机前方 **100 m** 开始，起点至少为相机近裁剪距离。该布局已与 UE 快速 AP 对齐。

## 2. 仍保留的 PC 模型差异

以下是 Max PC 的明确设计选择。Max Mobile 已在这两项上对齐 UE。

| 项目 | UE 当前实现 | Max PC |
|---|---|---|
| Mie 相函数 | HG | CS |
| 多散射反馈与闭合 | 简单段积分，有限五项累加 | 解析段积分，无限几何级数闭合 |

这些选择会改变散射分布或多散射能量估计。增加采样数只能减小数值离散误差，不能消除模型差异。“解析”指段内介质系数恒定时的积分。

依据：[Max 相函数](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:62>)、[Max 多散射](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl>)、[UE 反馈分支](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:785>)。

## 3. AP 的单体积布局与平均透射率

```text
T_average = (T.r + T.g + T.b) / 3
AP.rgba = (L_scatter.rgb, T_average)
L_out.rgb = L_surface.rgb × T_average + L_scatter.rgb
```

体素积分内部仍求出 RGB 透射率，写入体积时才取算术平均，与 UE 快速 AP 的存储约定一致。近端淡入将 RGB 入散射淡到 0、平均透射率淡到 1，保持起点之前的表面颜色。

平均值舍弃了背景物体红、绿、蓝被空气削弱程度的差异。例如 `T=(0.9,0.7,0.4)` 原本让白色物体自身的贡献偏暖，平均后则按约 `0.667` 等比例压暗。**空气入散射仍保存完整 RGB，因此雾和天光仍可呈现颜色。**

这是两档共同采用的存储取舍，已与 UE 对齐，不再作为待解决的通道差异。原来的双体积方案需要两次 3D 查询；现在只存一张 RGBA16F、查询一次。

按每视图一套数据计算，PC 的 AP 逻辑存储为 **256 KiB**（原双体积 512 KiB），Mobile 为 **32 KiB**（原 64 KiB）。这些数字只计算 AP 纹理内容，不包含其他大气资源或驱动分配开销。

依据：[UE AP 输出的平均透射率](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:1701>)、[Max AP 合成与查询](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl>)。

## 4. 其他仍存在的差异

- **全天 TOD 接入**：Max 当前只输入一份有效光源，按 6 / 18 点切换日月，夜间复用白天轨迹；UE 公共大气代码支持第二个大气光源。太阳暮光与月光的共同参与、独立月面和星空属于另一个功能范围。
- **参数自由度**：UE 可独立调整 Mie 散射与吸收等参数；Max 目前共用气溶胶倍率。默认输入一致时，这不是当前主要的画面差异。
- **HDR 与调度**：UE 使用 PreExposure，Max 使用固定光度缩放和现有最终曝光。Max 按参数和相机视图缓存 LUT；这些差异应按数值范围、失效依赖和更新开销单独评估。
- **局部边界细节**：近地观察参考高度保护为 Max 1 m、UE 5 m；Max 光盘目前为硬边，UE 光盘有软边。AP 的近地路径边界处理也保留本项目的射线截断规则，不将资源格式对齐表述成逐像素结果完全相同。

依据：[Max 日月输入](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/GI/MaxBakedSolarGI.cs:148>)、[Max 资源与视图缓存](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs>)、[UE 参考高度](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Public/Rendering/SkyAtmosphereCommonData.cpp:277>)。

## 5. 本轮资源约定

PC 的天空、多散射和透射率 LUT 使用 **R11G11B10 浮点格式**；Mobile 的天空与多散射使用 **RGBA16F**，透射率使用 **RGBA8**。不支持所需紧凑格式的设备使用受支持的浮点回退格式。

透射率 LUT 按 UE 的方式存储 `sqrt(T)`，查询后平方还原；该编码对两档统一生效。它有助于小格式表示低透射率，仍有格式量化和过滤误差。

两档 AP 为 **一张原生 3D RGBA16F 体积**，保存 RGB 入散射与 Alpha 平均透射率；范围和起点已按第 1 节对齐。这里不再重复列已经一致的 UE 格式数据。

依据：[UE 格式选择与分配](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1284>)、[UE 透射率编解码](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphereCommon.ush:233>)、[Max 资源分配](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs>)。

## 6. 采样预算：只保留仍需区分的数字

以下数字都指 **LUT 生成阶段**。UE 数字采用组件默认倍率 1、没有额外项目覆盖的口径。

| 计算对象 | Max PC 当前 | UE 桌面预设参考 | 需要记住的区别 |
|---|---|---|---|
| SkyView 的一个方向纹素 | 8–32 步 | 常用查表预算为 4–32 步 | Max 的最小预算更高；不是所有方向都使用 32 步 |
| 一个多散射纹素 | **32 个球面方向，每方向 20 步** | 默认 2 个方向；每方向常用 15 步，最高预设为 20 步 | 32 个方向是本项目选择；不会在每个屏幕像素重新追踪这些方向 |
| AP 中的一个空间点 | 32 层，从近到远分别为 2、4……64 步 | 常用体积为 16 层、2–32 步；最高预设可为 32 层、2–64 步 | 每个空间点独立积分从起点到该点的整段路径 |

**AP 最远 64 步与天空最多 32 步属于不同数据的生成预算。** AP 并非每跨一层再累计 64 次；它为各个目标空间点分别求解。

Mobile 的主要数值预算已对齐 UE 移动预设：96×50 SkyView、每纹素 1–8 步、多散射上下两方向各 15 步、AP 四层分别 1 / 2 / 3 / 4 步。这组相同值不再展开比较。

实际成本还取决于表尺寸、格式和重新生成的频率。静态缓存帧、相机转动帧和 TOD 连续变化帧应分别测量；源码中的步数或查询次数不能直接换算成整帧性能倍率。

依据：[Max 数值预算](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereSampling.hlsl:8>)、[Max AP Compute 生成](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUT.compute>)、[AP 体素积分](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUTCommon.hlsl>)、[UE 桌面预设](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/BaseScalability.ini:901>)、[UE 预算合并](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1422>)、[移动预设](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/Android/AndroidScalability.ini:173>)。

## 7. 维护入口

- 档位入口：[DefaultAtmosphere.asset](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/DefaultAtmosphere.asset:15>) 的 Platform。资源分配、覆盖范围和失效依赖位于 [MaxProceduralSkybox.cs](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs>)，数值预算位于 [AtmosphereSampling.hlsl](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AtmosphereSampling.hlsl>)。
- AP 生产入口是 `AerialPerspectiveLUT.compute` 的 `BuildAerialPerspective`，引用保存在 [MaxRendererPipelineAsset.cs](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipelineAsset.cs:70>) 的 Aerial Perspective Compute 字段；体素数学位于 `AerialPerspectiveLUTCommon.hlsl`。旧 Fragment 包装保留作参考，不作为当前生成路径。
- 原均匀经纬映射、64 方向表、固定 32 步中点积分和旧 AP 布局的核心逻辑，仍可在 [Helper.hlsl](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl>)、[RayMarching.hlsl](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl>) 与 [AerialPerspective.hlsl](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl>) 的停用代码或恢复说明中找到。
