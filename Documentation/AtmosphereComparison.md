# 大气实现对照：UE PC、UE 移动端与 MaxSRP

> 撤回记录（2026-11-12）：已撤回 BistroTOD 全天空接入。本文保留对照时的源码快照结论；涉及 MaxTODSky、星月、夜间增益的内容（尤其第 9 节）是撤回前的历史实现，不代表当前功能。其余散射内核对照继续保留。

核查日期：2026-11-12。本文只做源码和配置对照，没有修改渲染代码，也没有进行新的 UE / Unity 同场景 GPU 测试。

## 1. 先明确比较对象

| 对象 | 本文采用的版本和范围 | 数据代表什么 |
|---|---|---|
| UE PC | 本地 UE **5.8.4**，CL **58902052**，桌面渲染路径；分别列出 EffectsQuality 0 / 1 / 2 / 3 / Cine | 引擎源码默认值与内置质量预设，不能代表所有 UE 项目的实际设置 |
| UE 移动端 | 同一版本的移动渲染路径，重点检查 ES3.1，以及 Android / iOS 的 EffectsQuality 0–3 配置 | 平台预设与路径选择；设备配置、项目配置和组件参数还可覆盖。手机运行桌面渲染器不属于此列 |
| MaxSRP | 当前工作区的大气代码、管线资产及 TOD 天空组件 | 当前实现；大气公式参考论文路线，**不将其描述为 UE 5.8.4 的直接移植** |

**UE PC 也不是物理真值。** 三方都使用实时近似。要判断“物理是否正确”，必须先统一介质、光源和地面边界，再与收敛的独立输运参考比较；不能单凭 UE 的画面或样本数下结论。

下文的“默认”指被引用文件中的默认；UE 表格假设没有额外的项目 / DeviceProfile 覆盖，组件采样缩放为默认。MaxSRP 表格指当前工作区，不保证未来修改后自动同步。

版本依据：[UE Build.version](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Build/Build.version:1>)；Max 当前参数：[管线资产](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset:99>)。

## 2. 如何区分“物理问题”和“性能取舍”

| 分类 | 典型内容 | 改变后意味着什么 | 单纯增加样本能否解决 |
|---|---|---|---|
| **物理输入 / 边界** | 大气密度、太阳照度、太阳大小、地面反照率 | 换了要模拟的世界；参数不同导致画面不同通常合理 | 不能，必须先统一输入 |
| **物理模型近似** | HG / CS 相函数、多散射各向同性假设、用低维函数闭合高阶散射 | 换了对真实输运的近似方式；可能同时出于性能考虑，但不专属于移动端 | 不能消除模型本身的误差 |
| **数值近似** | 射线分段、方向积分、LUT 参数化与插值 | 在既定模型下近似求解；应通过收敛实验检查 | 通常能降低相应离散误差，但不能修正模型 / 输入差异 |
| **质量 / 性能预算** | LUT 尺寸、积分次数、半精度 / 8 位存储、RGB 透射率压成标量 | 用计算、内存或带宽换精度；可能有可见损失 | 要恢复相应分辨率 / 信息，其他位置加样本无效 |
| **调度 / 数据组织** | 缓存、按需重建、展平 2D 或原生 3D 组织 | 若依赖和插值正确，可以保持相同物理模型；浮点 / 过滤结果仍需验证 | 不以增加样本为目标 |
| **表现 / 显示** | 夜间补亮、星空背景、曝光、色调映射 | 改变玩家看到的亮度与风格，不能据此证明输运正确 | 不适用 |

分类可以重叠。例如“两方向近似球面积分”属于数值近似，也是一种性能取舍；“地面反照率为 0”则首先是边界条件，不能混入“移动端少算几步”的讨论。

## 3. 物理输入与基础模型

### 3.1 三方共有的框架

三方均采用球形大气、随高度变化的介质、Rayleigh / Mie 散射与臭氧吸收，并通过 Transmittance、MultiScattering、SkyView、Aerial Perspective（下称 AP，空气透视）LUT 加速。这里模拟的是大尺度、水平近似均匀的大气，不是对真实天气、气溶胶粒径分布和全部光谱的完整模拟。

| 项目 | UE PC | UE 移动端 | MaxSRP 当前 | 分类 / 判断 |
|---|---|---|---|---|
| 行星 / 大气厚度 | 默认 6360 km / 60 km | 共用同一介质模型 | 6360 km / 60 km | 物理输入，当前基本一致 |
| Rayleigh 密度 | 指数，高度尺度 8 km | 同 PC | 指数，高度尺度 8 km | 模型与默认输入一致 |
| Mie 密度 | 指数，高度尺度 1.2 km | 同 PC | 指数，高度尺度 1.2 km | 同上 |
| 臭氧密度 | 默认两段线性构成帐篷形，中心 25 km、半宽 15 km | 同 PC | 三角形，中心 25 km、半宽 15 km | 默认形状基本一致；UE 可独立调整两段参数 |
| 颜色表示 | RGB 系数，非完整光谱积分 | 同 PC | RGB 系数，非完整光谱积分 | 共同的模型近似，不是移动端专属降级 |
| 系数控制 | Rayleigh 可调颜色 / 强度；Mie 散射与吸收可独立调节 | 同 PC | 固定基础 RGB 系数乘 Scale；Mie 散射与吸收共用密度 Scale | 参数自由度不同，不直接等于物理正确性不同 |
| 长度单位 | 大气内部主要用 km | 同 PC | 主要用 m | 单位选择；不能仅凭单位断言谁的浮点精度更好 |

按 **m⁻¹** 统一单位后，默认海平面系数如下；这些是介质输入，不是性能档位：

| 系数 | UE PC / 移动端默认 | MaxSRP 当前基础值 |
|---|---|---|
| Rayleigh RGB 散射 | (5.802, 13.558, 33.100) × 10⁻⁶ | 相同 |
| Mie 散射 | 3.996 × 10⁻⁶，默认灰色 | 相同 |
| Mie 吸收 | 0.444 × 10⁻⁶ | 相同；由消光 4.440 减去散射 3.996 得到 |
| 臭氧 RGB 吸收 | (0.650, 1.881, 0.085) × 10⁻⁶ | 相同 |
| Mie 参数 g | 0.8 | 0.8；但相函数不同，见下节 |

依据：[UE 地球默认参数](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/SkyAtmosphereComponent.cpp:95>)、[UE 介质求值](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphereCommon.ush:321>)、[Max 散射 / 吸收系数](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:35>)。

### 3.2 必须先统一的边界和光源

| 项目 | UE PC | UE 移动端 | MaxSRP 当前 | 如何判断 |
|---|---|---|---|---|
| 地面遮挡 | 球形实心地面，阻断穿过行星的光路 | 同 PC | 同类球面截断 / 遮挡 | 物理边界 |
| **地面向大气反弹** | 多散射 LUT 包含 Lambert 地面反射，默认 GroundAlbedo 约 **0.4** | 同 PC | **没有地面反射项，相当于黑地面边界** | 改变能量输入；不是 UE 移动端的性能降级 |
| 太阳盘角度 | 由方向光输入；默认角直径 0.5357°，即角半径约 0.26785° | 同 PC 的光源参数定义 | 当前 `_SunDiskAngle=1°`，代码含义是**角半径**，即角直径 2° | 当前场景参数不同；比较太阳盘与太阳附近时必须统一 |
| 光源照度 / 色彩 | 由关卡 Atmosphere Light 输入 | 同 PC | 由本项目光度和 TOD 逻辑输入 | 必须统一大气顶 / 地面照度约定，不能只对齐 Inspector 数字 |

这里的 GroundAlbedo 是**行星地表给大气的平均反弹边界**，不是 Bistro 几何体的 DDGI 反弹，也不会自动包含建筑颜色。UE 的默认灰地面也不代表任意真实场景的地表反照率。

公平对照时，可先将 UE 地面反照率设为 0，与现有 Max 黑地面边界一致；也可另行实现并统一反射边界。直接拿 UE 默认 0.4 对比 Max 的 0，不能把亮度差全归咎于积分算法。

依据：[UE GroundAlbedo 默认](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/SkyAtmosphereComponent.cpp:105>)、[UE 地面反射积分](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:781>)、[UE 太阳默认角度](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/DirectionalLightComponent.cpp:1050>)、[UE 角直径转半角](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/DirectionalLightComponent.cpp:619>)、[Max 太阳盘](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl:30>)。

## 4. 物理模型近似：PC 与移动端共用，不能当成移动端阉割

| 项目 | UE PC | UE 移动端 | MaxSRP 当前 | 意义 |
|---|---|---|---|---|
| Rayleigh 相函数 | `3(1+μ²)/(16π)` | 同 PC | 相同 | 基础公式一致 |
| **Mie 相函数** | **Henyey–Greenstein（HG）** | 同 PC | **Cornette–Shanks（CS）** | 两种气溶胶角分布近似；同 g 不代表同分布。没有指定真实气溶胶参考时，不能说换成某一个就物理正确 |
| 多散射的方向性 | 使用各向同性近似构建低维多散射估计 | 同 PC | 后续高阶散射也使用各向同性闭合，但首项构建不同 | 三方都没有完整保留全部高阶散射的方向分布 |
| **多散射首项** | 生成 MultiScattering LUT 时 `MieRayPhase=false`，采用各向同性相函数 | 同 PC | 构建 `G₂` 时调用实际 Rayleigh / CS 相函数，再做角度积分 | 表内数值不同，不能只靠对齐 LUT 分辨率来对齐结果 |
| 多散射反馈系数的段内积分 | 当前分支对反馈系数累加 `Throughput × σs × dt` | 同 PC | 对反馈系数使用包含段内指数透射的解析权重 | 反馈估计的数值近似也不同；不能只比较最终级数公式 |
| **多散射闭合** | 当前宏分支采用 `G × (1+r+r²+r³+r⁴)` | 同 PC | `G₂ / (1−f_ms)` | UE 有限五项因子与 Max 无限几何级数近似不同；不是精确“只算五次散射”与“完整无限散射”的区别 |

这里的 μ 是统一方向约定后的散射角余弦，UE 函数调用中的正负号还取决于方向定义，不能直接凭符号判断错误。

多散射可拆成三层看：**近似模型 → 对该模型做方向 / 距离积分 → 将结果存入 LUT。** 增加方向数只能改进第二层的一部分，不能消除第一层的各向同性和闭合误差。Max 的 64 方向因此不能证明其多散射整体比 UE 更准确；UE PC 高质量画面也不能证明其近似是物理真值。

无限几何级数还要求反馈系数满足收敛条件（逐通道小于 1）；分母形式本身不保证所有介质设置下稳定或准确。这里没有据此断言当前默认参数存在异常。

依据：[UE 相函数](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/ParticipatingMediaCommon.ush:91>)、[Max CS 相函数](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:56>)、[UE 多散射入口与方向积分](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:1240>)、[UE 闭合分支](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:1304>)、[Max 多散射构建](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:97>)。

## 5. 数值求解：采样方式比单个样本数更有意义

| 项目 | UE PC | UE 移动端 | MaxSRP 当前 | 分类 / 影响 |
|---|---|---|---|---|
| Transmittance 光程积分 | 次数由质量配置决定；普通档 10，Cine 30 | 普通平台预设继承 10 | 固定 32 个等长中点区间 | 数值预算；步数不同不是换了物理方程 |
| SkyView 光程积分 | 随光程调整次数，平方距离分段，近端更密 | 同一机制，配置压到 1–8 | 每条射线固定 32 个等长中点区间 | 自适应 / 非均匀采样 vs 均匀采样，不能按次数直接排精度 |
| 段内积分 | 将段内源项 / 消光近似为常量后，解析积分指数透射 | 同 PC | 同类解析段积分，小光学厚度另用 Taylor 展开 | Max 不是简单的 Euler 累加；仍存在段间采样误差 |
| MultiScattering 方向积分 | 默认 **上下两个方向**；单独打开 HighQuality 才用 64 方向 | 共用此选择，平台预设未自动开启 HQ | 固定 **64 球面方向** | 明确的方向积分预算差异，UE PC 默认也使用低方向数近似 |
| MultiScattering 每方向步数 | 普通档 15，Cine 20 | 普通平台预设继承 15 | 固定 32 | 数值预算；不改变第 4 节的模型差异 |
| SkyView 参数化 | 相机局部参考系，上下半球非线性映射，地平线加密 | 同 PC | 世界方向的均匀经纬映射 | 同尺寸纹理的有效分辨率分布不同 |
| AP 深度分布 | 平方分布的切片中心，近端淡入 | 同 PC，但层数更少 | 平方分布，包含零距离中性层，在相邻层间按真实距离插值 | 两边都已做非线性深度分配，并非 Max 缺失该优化 |

UE 当前 SkyView 参考系取自相机 ViewForward / ViewRight，**不是太阳相对参考系**。我们的经纬图不依赖相机转向，因此仅旋转时复用该图本身合理。UE 96×50 的地平线加密图与把我们的经纬图直接降到 96×50，不是相同方案。

边缘处理也要分 LUT：UE SkyView 有 unit-UV / sub-UV 映射；当前 UE Transmittance / MultiScattering 中相应 sub-UV 修正被注释关闭或明确不采用，不能笼统说“我们少了 UE 所有 LUT 的半纹素修复”。

依据：[UE 积分采样](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:567>)、[Max 段积分](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:82>)、[Max 视线积分](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:227>)、[UE SkyView 映射](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:214>)、[UE 相机参考系](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SceneRendering.cpp:1649>)、[Max 经纬映射](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl:7>)、[Max AP 切片](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl:32>)。

## 6. 质量预算：UE PC 不能只用一组“默认值”概括

### 6.1 UE PC 内置 EffectsQuality 预设

以下来自 BaseScalability.ini，列的是 **CVar 配置范围 / 上限，不是实际步数**，也不是实测帧耗。FastSky 为天空 LUT 路径；完整积分为不走 FastSky 等相关路径时使用的预算。运行前还会与组件 `TraceSampleCountScale` 合并，再按光程选择实际步数。

| PC 档位 | FastSky | FastSky 配置范围 | 完整积分配置范围 | AP 层数 | AP 因子配置上限 | Trans 步数 | Trans 小格式 | Multi 每方向步数 | AP 快速作用于不透明物体 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Low / 0 | 1 | 2–16 | 2–16 | 8 | 1 | 10 | 1 | 15 | 1 |
| Medium / 1 | 1 | 4–32 | 4–32 | 16 | 1 | 10 | 0 | 15 | 1 |
| High / 2 | 1 | 4–64 | 4–64 | 16 | 2 | 10 | 0 | 15 | 1 |
| Epic / 3 | 1 | 4–128 | 4–128 | 16 | 4 | 10 | 0 | 15 | 1 |
| Cine | 0 | 4–256（主天空不走此路径） | 8–256 | 32 | 8 | 30 | 0 | 20 | 0 |

- 通用 SkyView 尺寸是 **192×104**，但 Cine 已关闭 FastSky，不能仍用这个尺寸代表其主天空的最终求解质量。
- 这些档位**没有自动开启** `r.SkyAtmosphere.MultiScatteringLUT.HighQuality`；其源码默认是 0。不能把 Epic / Cine 写成默认 64 方向多散射。
- Cine 关闭 AP 快速作用于不透明物体后，AP LUT 的尺寸 / 存储量也不能代表全部不透明空气透视的求解成本。

本地代码的合并规则是：天空有效最大预算 `min(32×Scale, CVarMax)`；AP 有效因子 k 为 `max(1, min(2×Scale, CVarMaxPerSlice))`。组件默认 Scale=1，因此得到下面这张**默认有效预算表**：

| PC 档位，默认组件 Scale=1 | FastSky 有效范围 | 完整积分有效范围 | AP 有效因子 k | 最远 AP 层的路径样本预算 |
|---|---:|---:|---:|---:|
| Low / 0 | 2–16 | 2–16 | 1 | 8 |
| Medium / 1 | 4–32 | 4–32 | 1 | 16 |
| High / 2 | 4–32 | 4–32 | 2 | 32 |
| Epic / 3 | 4–32 | 4–32 | 2 | 32 |
| Cine | 4–32（主天空不用） | 8–32 | 2 | 64 |

**不能说默认 Epic 每条天空射线用 128 步，或 Cine 用 256 步。** 组件倍率足够大时才可能触及相应配置上限；SkyView 仍按射线长度取样，也不是每个 texel 都用有效最大值。

AP 每条路径的预算按 `(z+1)×k` 递增，z 从 0 开始。不是每个 voxel 都只算 k 步，也不是只积分该深度切片的一小段。例如移动端四层 k=1，对应从起点到各层距离的 1 / 2 / 3 / 4 步积分。

依据：[PC 质量预设](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/BaseScalability.ini:798>)、[通用 CVar 默认](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:135>)、[预算与倍率合并](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1422>)、[组件倍率默认](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/SkyAtmosphereComponent.cpp:136>)、[AP 样本预算](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:1677>)。

### 6.2 UE Android / iOS 移动预设与 MaxSRP

当前 Android 和 iOS 的 EffectsQuality 0–3 均覆盖为下表值；其他值继续继承基础档位与源码默认。

| 参数 | UE PC 常用范围 / 默认 | UE Android / iOS 0–3 | MaxSRP 当前 |
|---|---|---|---|
| SkyView 尺寸 | 192×104，FastSky 开启时 | **96×50** | **256×128**；另有一张相同尺寸的环境天空图 |
| SkyView 积分步数 | 依档位与组件倍率，见上方两表 | **1–8**，按光程变化 | 固定 **32**；TOD 启用时日 / 月贡献可各调用一次积分 |
| Transmittance 尺寸 / 步数 | 256×64；普通 10 / Cine 30 | 256×64；继承普通档 **10** | 256×64；**32** |
| Trans 小格式 | Low 开，其余关 | **开** | 关，RGBA32F |
| MultiScattering 尺寸 | 32×32 | 32×32 | 32×32 |
| Multi 积分 | 默认 2 方向 × 15；Cine 20 步；HQ 另开 | 默认 **2×15** | **64×32** |
| AP 横纵 / 深度层数 | 32×32×8 / 16 / 32，依档位 | **32×32×4** | **32×32×32** |
| AP 深度范围 | 通用默认 **96 km** | 未被上述平台预设改写，仍继承 **96 km** | 当前资产 **32 km** |
| AP 积分预算 | 见 k 与档位 | 四层分别 **1 / 2 / 3 / 4** 步 | 每个非零距离 voxel 固定 **32** 步 |
| AP FastApplyOnOpaque | 普通档开，Cine 关 | 开 | PBR 着色阶段查 LUT 合成 |

**这是明确的性能 / 质量取舍区。** 移动端减少天空纹素、光程样本、AP 层数，并选择小格式；这些可能牺牲地平线、暮光与远景精度。Max 的较大预算则不保证结果更物理，因为第 3、4 节仍有输入与模型差异。

配置还可能反向增加预算。例如部分 PowerVR DeviceProfile 强制 `r.SkyAtmosphere.LUT32=1`，所以“UE 移动端永远只用低精度”也不成立；具体作用到哪些 LUT，应以分配代码为准。

依据：[Android 预设](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/Android/AndroidScalability.ini:173>)、[iOS 预设](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/IOS/IOSScalability.ini:173>)、[PowerVR 覆盖](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Config/BaseDeviceProfiles.ini:1300>)、[Max LUT 创建](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:82>)。

## 7. 存储与运行时采样：哪些信息被压缩了

### 7.1 LUT 格式与 AP 通道

| 项目 | UE PC | UE 移动端 / ES3.1 | MaxSRP 当前 | 判断 |
|---|---|---|---|---|
| SkyView 常规格式 | 通常 PF_FloatRGB，D3D12 下为 R11G11B10F / 4 B；需 alpha 时 PF_FloatRGBA / 8 B | PF_FloatRGBA / RGBA16F / 8 B | ARGBFloat / RGBA32F / 16 B | 精度 / 带宽预算，不改变散射方程 |
| Trans 小格式 | Low 使用 RGBA8，其余通常浮点格式 | 平台预设使用 RGBA8 | RGBA32F | UE 透射率用 `sqrt(T)` 编码、读取再平方，有助于降低小格式在低透射率区域的量化误差；并非无损 |
| MultiScattering 常规格式 | 通常 PF_FloatRGB | PF_FloatRGBA | RGBA32F | 同类预算选择 |
| **AP 数据内容** | RGB 入散射 + **平均 RGB 透射率**放 A | 同 PC | 一张 RGB 入散射 + 一张 **RGB 透射率** | UE 省略透射率的颜色差异，是信息压缩；PC 也如此 |
| AP 格式 | 通常 RGBA16F，LUT32 可选 RGBA32F | 通常 RGBA16F，设备覆盖可改 RGBA32F | 两张 RGBA32F | 存储精度不同 |
| AP 组织 | 原生 3D 纹理 | 原生 3D 纹理 | 两张展平 2D，分别为 1024×32 | 组织形式本身不要求牺牲 RGB；合并成 UE 单图方案则会改变信息 |
| AP 应用的逻辑采样指令 | 一次 3D 过滤采样 | 一次 3D 过滤采样 | 两个相邻层 × 两张图，合计四次 2D 过滤采样 | 指令数有差异；一次 3D 采样不等于只读一个 texel，也不能据此推导四倍 GPU 帧耗 |

Max 合成：`L_out.rgb = L_surface.rgb × T.rgb + L_scatter.rgb`。

UE 快速 AP 合成保留 RGB 入散射，但用单个平均透射率调制表面，因此无法完整保留距离消光的色偏。这是实际画质 / 存储取舍，不应描述为完全等价的打包优化。

`r.SkyAtmosphere.LUT32` 的说明文字较宽泛；本地实际 SkyView 格式选择函数没有按此变量切换的分支，AP 分配则明确读取它。不能只凭变量说明认定每一种 LUT 都同步变成 32 位。

依据：[UE 格式选择与分配](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1284>)、[UE Trans 编码](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphereCommon.ush:233>)、[UE AP 通道打包](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphere.usf:1701>)、[UE AP 读取](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/SkyAtmosphereCommon.ush:42>)、[Max 格式](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:124>)、[Max AP 读取与合成](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl:66>)。

格式字节依据：[UE PixelFormat 表](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Core/Private/Misc/PixelFormat.cpp:35>)、[D3D12 格式映射](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/D3D12RHI/Private/D3D12State.cpp:903>)。

### 7.2 只比较 AP 当前一份逻辑数据的字节量

此表**不是总常驻显存**：不含其他 LUT、历史 / 双缓冲、反射捕获、多相机、对齐和驱动分配开销；UE ES3.1 AP 还有双缓冲。深度覆盖范围与 RGB 透射率信息也不同，不能说容量比就是等画质压缩比。

| 配置 | 计算 | 逻辑体积 |
|---|---|---:|
| UE PC Low，RGBA16F | 32×32×8×8 B | 64 KiB |
| UE PC Medium / High / Epic，RGBA16F | 32×32×16×8 B | 128 KiB |
| UE PC Cine 的 AP LUT，RGBA16F | 32×32×32×8 B | 256 KiB |
| UE 移动预设，RGBA16F | 32×32×4×8 B | 32 KiB |
| UE 移动预设，LUT32=1 的 AP | 32×32×4×16 B | 64 KiB |
| MaxSRP，RGB 入散射与 RGB 透射率两张 RGBA32F | 2×32×32×32×16 B | **1024 KiB / 1 MiB** |

依据：[UE AP 分配](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1377>)、[ES3.1 AP 双缓冲](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/RendererScene.cpp:268>)、[Max AP 分配](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:90>)。

## 8. 渲染路径、缓存和曝光

| 项目 | UE PC | UE 移动端 | MaxSRP 当前 | 分类 |
|---|---|---|---|---|
| 可见天空 | 桌面天空渲染路径，可选 FastSky LUT / 完整积分 | SkyPass 绘制 IsSky 天空网格材质，材质查 SkyView LUT；不直接沿用桌面全屏渲染函数 | 全屏三角形，查 SkyView，再叠太阳 / 月盘 / 星点 | 路径与画质预算 |
| 不透明物体的 AP | 普通档快速查 AP，Cine 可完整积分 | **默认不能概括为逐顶点**，见下文 | PBR 像素着色中读取 AP 并合成 | 应用位置与预算 |
| Trans / Multi 更新 | StateVersioning 默认开启，大气状态变化才重建 | 共用状态缓存机制 | 按介质参数缓存，多散射还追踪相函数参数变化 | 调度，正确失效时不必牺牲质量 |
| SkyView / AP 更新 | 进入每视图生成流程，按渲染需求调度 | 共用 LUT 生成流程，由移动路径消费 | Sky 按介质、高度、光源、TOD 状态缓存；AP 还依赖相机矩阵和距离等 | 调度；不能说 Max 每帧都重算全部 LUT |
| HDR 数值范围 | 使用 PreExposure 等机制 | Mobile HDR 也有 PreExposure；Mobile LDR 另有约束 | 固定光度缩放的线性 HDR，最终曝光位于共享 TAA 之后 | 数值范围 / 显示，不是额外光能 |

**移动端 AP 的具体纠正：** 本地 `r.Mobile.DisableVertexFog` 默认 **1**，常规基础 Pass 不走顶点雾，转由独立 MobileFog 像素 Pass 应用；默认 `r.Mobile.PixelFogQuality=1` 的路径包含 AP。将 DisableVertexFog 改为 0 时，代码也有逐顶点 AP 路径。PixelFogQuality=0 又会禁用该像素雾路径中的 AP 等功能。透明材质和天空材质还有各自条件，不能一句话断言“UE 移动天空 / 所有 AP 都逐顶点”。

PreExposure 的目的是把数值放进合适的 HDR 范围，并在后续一致地还原；它与“夜里把天空额外提亮若干 EV”用途不同。迁移时若改动缩放约定，还必须同步考虑历史缓冲；不能给某个新 Pass 单独另加一套曝光或 TAA。

依据：[UE 移动 LUT 生成](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/MobileShadingRenderer.cpp:1414>)、[移动天空材质采样](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Shaders/Private/MaterialTemplate.ush:2369>)、[DisableVertexFog 默认](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/MobileBasePassRendering.cpp:30>)、[MobileFog 路径选择](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/MobileFogRendering.cpp:138>)、[PixelFogQuality 默认](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/FogRendering.cpp:57>)、[UE 状态缓存](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/SkyAtmosphereRendering.cpp:1509>)、[移动 PreExposure](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Renderer/Private/MobileShadingRenderer.cpp:798>)、[Max 缓存](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:201>)、[Max TAA / HDR 契约](<D:/UnityProject/MaxSRP/Documentation/TemporalAA.md:13>)。

## 9. TOD 夜空应单独评价

散射内核接受光源后可以计算太阳暮光或月光散射，但这不等于一个完整的全天空产品。星空、月盘、太阳 / 月亮轨道、夜间可见性补光和曝光策略，应与散射求解分开评估。

| 内容 | UE PC / 移动端 | MaxSRP 当前 | 判断 |
|---|---|---|---|
| 日光 / 月光散射 | SkyAtmosphere 接受大气光源输入；项目负责光源轨迹与强度 | TOD 层分别调用同一散射内核求太阳暮光与月光贡献 | 输运可复用；输入是否真实要另验 |
| 太阳消失、月亮出现 | 不是切换 PC / 移动质量档就自动得到的天体模型 | 按项目简化轨迹安排月亮，复用太阳可见弧覆盖夜间 | 游戏规则，不是天文学精确模型 |
| 夜间天空增益 | 取决于关卡 / 天空材质 / 曝光设置，不能从散射公式推断统一值 | `NightExposureEV` 默认 9；夜权重 w 随太阳高度从 0° 到 −12° 平滑增加，散射增益为 `2^(9w)` | **表现层增益，不是 PreExposure，也不是物理推导** |
| 夜间背景 | 项目天空材质负责，不能把引擎所有项目视为同一套星空 | 人工天顶 / 地平线颜色与亮度，随夜权重加入 | **美术背景**；不等同于物理夜辉 / 银河模拟 |
| 月盘与星点 | 项目内容决定 | 程序化满月、程序化星点；亮度参数独立于散射增益 | 表现层，未做真实星表与月相模拟 |

因此当前夜景“能看清”不能用来证明月光散射能量正确；夜景比 UE 某个关卡亮，也不能据此判定大气公式有 bug。比较第 3–7 节时，应先关闭人工背景、星点和夜间增益，统一月光照度，再看线性 HDR。

UE 同样有可调的能量 / 亮度倍率。比如 `MultiScatteringFactor` 构造默认是 **1**；头文件对低质量模式推荐其他值，不等于运行默认值。做能量对照时也要统一这些倍率。[UE 默认值](<D:/Program Files (x86)/EPIC/UE_5.8/Engine/Source/Runtime/Engine/Private/Components/SkyAtmosphereComponent.cpp:130>)。

历史源码位置：`Runtime/MaxTODSky.cs`、`ShaderLibrary/Realtime/AtomsphereScattering/TODSky.hlsl`；这两个新增文件已随此次撤回删除。

## 10. 如何据此决定后续修改

| 选择 | 应归类为 | 可以期待什么 | 不能直接宣称什么 |
|---|---|---|---|
| 先统一地表反照率、光源强度、太阳半径、介质参数 | 统一物理输入 / 边界 | 消除比较对象不一致 | 不能把统一输入当成移动端优化 |
| Max CS 改 HG，或反过来 | 改物理模型近似 | 与某个目标模型 / UE 更接近 | 不能默认称为修复物理错误 |
| 改多散射首项、闭合方式 | 改多散射模型近似 | 改变能量与角度分布，需要独立参考验证 | 不能只看一张中午截图判定全天正确 |
| 地平线加密的 SkyView、重新分配积分区间 | 数值方法改进 | 有机会在较小预算下保持关注区域精度 | 不能不验证便认定完全等质 |
| 缩小 LUT / 减少步数 / RGBA32F 改半精度 | 质量 / 性能预算 | 降低更新成本或存储量 | 不能叫无损，尤其应看日出日落和远景 |
| AP 透射率 RGB 改平均值 | 信息压缩 | 降低存储 / 采样开销 | 不能保持完整彩色消光 |
| 缓存正确复用、保持信息的布局调整 | 调度 / 数据组织 | 有机会保持同一模型输出并减少重复工作 | 不能只凭采样指令数承诺帧率倍数 |
| 调整夜间 EV、背景和星月亮度 | 表现层 | 改善夜景可读性 | 不能验证或修复物理输运 |

建议的后续对照顺序（本次未执行）：

1. **固定问题。** 无云、无局部雾、无额外光束 / 阴影变化；统一介质、地面边界、相机高度、太阳方向 / 照度 / 半径，关闭自动曝光、人工夜背景和星月盘等干扰。先比较天空和 AP，避免将 DDGI 场景照明混入大气误差。
2. **比较线性量。** 在相同光度单位和曝光约定下采样线性 HDR；同时检查天顶、地平线、顺光 / 逆光，以及天空积分照度。Tone Mapping 后截图用于外观验收，不单独充当能量依据。
3. **建立收敛参考。** 在相同物理输入下使用独立高精度 / 高采样输运参考；分别检验单散射与多散射。UE Cine 可作实时实现对照，但也受模型近似限制。
4. **再降预算。** 一次只改变参数化、采样数、LUT 尺寸或存储精度中的一项；至少覆盖正午、低太阳高度、太阳刚落山和月光场景。
5. **最后测性能。** 分开记录 LUT 重建帧、静态缓存帧、相机运动帧及全天 TOD 连续变化帧。源码循环次数、逻辑纹理字节和采样指令数量都不能替代 GPU 实测。

目前可以确定的是：**UE 移动端主要在第 6、7 节压缩预算；Max 与 UE 的核心模型 / 输入差异主要在第 3、4 节；夜间可读性还受第 9 节表现层控制。三类问题应分别决策。** 本文不将其中任何一项自动列为待修改项。
