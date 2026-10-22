# MaxSRP 问题检查与修复记录

更新日期：2026-10-23（接入 Clustered Deferred 点光与独立 Bistro 室外测试场景，保留既有太阳、物理强度与共享 TAA）。

范围：实时 SRP、PBR、IBL、大气和级联阴影。待处理章节沿用原编号，便于逐项处理；已完成的项目集中放在文末按日期排列的“已修复”区域。

当前 TAA 契约与接入边界见 [Documentation/TemporalAA.md](Documentation/TemporalAA.md)，AGENTS.md 已要求相关模块遵守该契约。

待处理：**27、28、32**，共 3 项。33 在接入独立相机历史时一并修复，见文末。19、29 已分别修复；30 的屏幕阴影过滤路径已按用户选择移除，31 已补齐四级 PCSS、投影重叠及过渡，记录见文末。shadow cache 暂不实现。27/28 为独立大气对照发现的条件性精度事项；clustered 点光已接入，聚光灯／面光源、局部光阴影与场景 GI 继续延期。

P1 表示优先修复的正确性问题；P2 表示条件性错误、精度问题、功能未接通或性能问题。各项具体证据与验证范围分别记录，不能将某一项的 GPU 验证推广到全部管线。

本轮新增已修复：天空输出的 RGB dithering 相关性及硬件 sRGB8 量化阈值偏差。受控 GPU 渐变和同 HDR 回放验证通过，用户于 10 月 20 日确认当前 demo 的 banding 几乎不可见，按当前场景验收关闭。详见 [输出量化对照](Tools/AtmosphereReference/OUTPUT_TRANSFER.md)。

## 32. [P2] 灯光阴影强度与 Hard/Soft 选择没有接入

状态：**本轮新增，设置消费路径确认；不是光照能量公式问题**。

位置：[SunLightData.cs](Assets/MaxRendererPipeline/Runtime/SunLightData.cs)、[Lighting.hlsl](Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl)、[Shadow.hlsl](Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl)。

只把 `Light.shadows != None` 转为布尔开关。`Light.shadowStrength` 没有上传或混合，Lighting 直接乘以原始可见度；因此把阴影强度设为 0 仍不会使阴影全亮。Hard 与 Soft 也进入相同路径；10 月 21 日更新后均走四级 PCSS，尚未接通灯光模式选择。当前 demo 的强度若为 1，不影响已验收的照明，但 Inspector 控制语义不完整。

处理建议：接入阴影强度（例如 `lerp(1,visibility,strength)`），并明确 Hard/Soft 是否为支持的选择，或在自定义 Inspector 清楚说明仅使用管线阴影设置。参考 [Unity 2022.3 Light.shadowStrength](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Light-shadowStrength.html)。

## 本轮验证范围与功能边界（2026-10-20）

- `Tools/ReviewShadowContracts.ps1` 提取真实 PCSS 半径函数及两段资源清理函数执行数值/生命周期反例，；旧版曾读取真实模糊权重，10 月 21 日移除屏幕阴影后改为检查路径已删除。它是问题诊断，不是 GPU 测试，也不是“通过代表所有阴影正确”的回归脚本。
- 既有 `ValidateSceneShadows`、`ValidateShadowCameraIsolation`、`ValidateCameraViewport`、`ValidateColorOutput` 均通过。这些证明已有覆盖的投影、包围盒、源相机隔离、视窗和输出逻辑没有在此复查中失败，不覆盖新增反例。
- 本轮 Unity MCP 返回没有注册的编辑器实例；进程存在且标题为 MaxSRP/Untitled。故未采集当前场景 GPU 图像、未实测阴影投射开关、未做 Player 构建。不把共享 Editor.log 中其他 Unity 工程的内容当成 MaxSRP 的运行证据。
- **待核实而非已确认错误**：通过普通 `DrawRenderers` 绘制 `ShadowCaster` 时，`ShadowCastingMode.Off / ShadowsOnly / TwoSided` 在当前调用中的实际行为，需独立几何场景 GPU A/B；本轮不直接判错。
- **功能未实现，不混作新回归**：透明物体没有前向透明 Pass，当前只绘制 opaque 队列；Alpha Clip 没有 `_Cutoff/clip` 路径；GBuffer 自发光、AO、运动向量通道尚未接入；没有完整的抗锯齿路径。按需扩展即可，不在本轮擅自增加。
- **逐物体接受阴影属于扩展项**：GBuffer 未保存该标志，但不能仅据 `Renderer.receiveShadows` 无效判定渲染错误；[Unity 2022.3 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer-receiveShadows.html) 也说明内置 Deferred 路径不使用该标志。若项目需要，应明确设计材质/对象标志与 GBuffer 编码。
- 按既定范围继续保留单方向光和现有点光路径，cluster lighting、场景 GI 不作为本轮回归问题。

上述复查只更新诊断脚本和文档；后续按用户指定分别修复 19、29，10 月 21 日改为直采样 PCSS 并关闭 30/31，见文末记录，32 尚未修复；33 后续在 TAA 基础设施接入中修复。

## 补充：性能与潜在问题

### 27. [P2] 近太阳单次散射固定 32 步积分偏低

状态：独立参考与实际 GPU 已复现，待处理。位置：`RayMarching.hlsl` 的 `GetAtmosphereSegment`。

当前 2 m、太阳高度 69.811°，沿太阳方向 GPU 单次散射比独立 float64 高精度参考低约 **5.72%**，完整天空仍低约 **4.80%**。独立介质/太阳透射下，仅采用 32 段常系数积分便得到 **0.942606** 的响应比，几乎复现 GPU 的 **0.942782**；64/128/256 段分别为 0.984933/0.996186/0.999044。这支持固定步数对低层 Mie 密度变化的积分误差，不是太阳强度缩放错误。可后续评估分段与步数预算，本轮未修。

### 28. [P2] 黄昏部分方向的多次散射明显偏亮

状态：独立路径追踪与实际 GPU 已复现，待进一步拆分原因。位置：`IntegralMultiScattering`、`GetMultiScattering` 与 32×32 多次散射 LUT。

太阳高度 −3°、2 m 观察者、背阳仰角 1°，两边单次散射均为零；完整亮度 Unity/独立参考约 **1.73955**（比值约 95% 统计半宽 0.0263）。当前项目 RGB 约定下约 **38.32 / 22.03 cd/m²**，差异明显超过噪声。同一黄昏其他方向也有约 7%～16% 偏差。

已定位到本实现的多次散射链路，尚未将 isotropic/local closure 近似、LUT 的高度/太阳角插值、64 方向积分分别量化，不能据此认定 UE 原论文错误。当前白天整体天空照度已与独立参考相符，不宜用整体强度系数追平单个黄昏方向。本轮未修。

27/28 的方法、数据与复测：[独立大气报告](D:/UnityProject/MaxSRP/Tools/AtmosphereReference/README.md)、[数值结果](D:/UnityProject/MaxSRP/OfflineBaseline/AtmosphereReference/atmosphere_comparison.json)、[对照图](D:/UnityProject/MaxSRP/OfflineBaseline/AtmosphereReference/atmosphere_reference_comparison.png)。

- **实机光度与色彩对照**：太阳/天空/IBL 已统一入射能量和 lux 参考，见文末修复记录。当前使用 RGB 大气近似和固定 HDR 缩放，尚未做光谱到 XYZ 的完整标定、目标硬件照度对照或相机 EV100/白平衡系统；不能将 CPU 数值检查等同于真实场景的测量标定。
- **独立大气数值对照已补齐**：当前白天水平天空照度 GPU **6209.23 lux**，独立参考 **6209.16 ± 7.08 lux**（近似 95% 统计区间，沿用项目 RGB 光度约定）；RGB 分别相差约 −1.38%/−0.17%/+1.68%。整体能量相符不代表各方向/各通道相同，不能把一次均值差当作 0.0012% 的验证精度。当前参数以外和完整光谱/实测标定仍不在本结论内。
- **PRT/SH 遗留路径未启用**：MaxLightProbeSkybox 未执行 SH 投影，c0..c8 实际上传零；GlobalIllumination.hlsl 的 PRTApprox 绿通道最后一项用 c8.z 而不是 c8.y。当前 PBR 漫反射走 cubemap，不受该 SH 错误直接影响；若恢复 PRT，应单独审查归一化、卷积和系数约定。

## 后续修复顺序

1. 29/30/31 已改为 Lighting 直采样四级 PCSS；实际 GPU 观感、移动稳定性及真机成本待验收。
2. 32：接入灯光阴影强度和 Hard/Soft 控制语义。
3. 33：避免多视图的 RT 往返重建，然后测量实际分配量与耗时。
4. 19 已补齐构建引用；发布前仍需最小 Player 构建及运行验收。
5. 27/28 为独立记录的大气条件性精度事项，按用户后续选择单独处理；cluster lighting 和场景 GI 继续按既定计划延期。

## 复查阶段的验证范围（2026-10-15，修复前）

重新读取当前工作区的相机/输出、GBuffer/PBR/IBL、四级阴影与大气积分及 LUT 代码，并检查资产引用；没有把历史“已修复”记录当作本轮结论。原 8、10、18 仍存在，已更新代码定位并补充无方向光状态。

六个现有脚本在 PowerShell 7.6.5 下通过：ValidateCameraViewport、ValidateColorOutput、ValidateSceneShadows、ValidateShadowCameraIsolation、ValidateAtmosphereGround、ValidateAerialPerspective。先用 Windows PowerShell 5.1 调用时，五项因旧版 Add-Type 编译器/程序集支持失败；换到 PowerShell 7 后通过。这些脚本应使用 pwsh 执行，5.1 的失败不视为 Unity 编译错误。

本轮另做独立数值例核对，确认 Mie 基准差异、正交观察方向角差、PCSS 零 blocker 深度产生无穷半径，以及 GL 采样深度直接反投影的偏差；核对本机 Unity 2022.3 的法线解码实现。该审查阶段未新增或修改测试脚本，未启动 Unity 构建，未做实时 GPU 截帧，不能以现有回归通过证明显示方向、Shader 剥离或上述触发场景已经通过实机验证。全程未访问 Library。

## 已修复（2026-10-13）

本区域记录 2026-10-13 完成的修复，已从上面的待处理清单移除；先前已删除的修复记录不重复加入。

| 原编号 | 修复结果 | 代码位置 |
| --- | --- | --- |
| 4 | 四级连续覆盖相机 near 到 min(camera far, ShadowDistance)，CPU 与 Shader 共同使用累积分界 0.07 / 0.20 / 0.45 / 1.00。采样使用重建位置的相机视空间深度；范围外和空深度返回全亮。正交宽度覆盖光空间 AABB，并预留 texel 对齐余量；远两级 HardShadow 补齐世界位置的 w=1。ShadowDistance 每次更新同步到生成与采样。 | [MaxShadowCasterPass.cs:150](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs:150)、[ScreenSpaceShadowMapPass.hlsl:56](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl:56)、[Shadow.hlsl:150](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:150) |
| 5 | 增加每个采样点到太阳的行星遮挡判断，接入天空单次散射及多重散射 LUT 的直射源项。高空仍按自身地平线保留受光；通用的大气顶端透射率查询继续用于其他路径。 | [RayMarching.hlsl:24](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:24) |
| 12 | Rayleigh、Mie、Ozone 倍率接入对应系数；Mie 倍率同时缩放散射与吸收，保持原有比例。负倍率按 0 处理，1 保持原系数，0 关闭对应介质贡献。 | [Scattering.hlsl:28](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:28) |
| PCSS 补充项 | blocker 计数从整数 0 开始，无 blocker 时在除法前返回有效的 (0, 0)，调用方继续返回全亮；有 blocker 时保留原平均值计算。 | [Shadow.hlsl:105](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:105) |

问题 5 的处理与 Hillaire 作者公开实现的逐点太阳可见性判定一致，参考 [RenderSkyRayMarching.hlsl](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl#L164-L180)。目前采用几何遮挡边界，未加入有限太阳圆盘的半影平滑。

验证：Player/Editor C# 使用本机 Unity 2022.3 托管程序集进行绑定编译（CoreUtils 与 CommandBufferPool 使用接口替身）；22 个 HLSL SM5 入口独立编译通过，覆盖阴影、PBR、IBL 及五个大气 Pass。数值核验通过：1,200 个级联区间内部深度样本、10,000 组 texel 对齐后包围盒覆盖、10,025 个太阳可见性案例与独立射线球求交对照，以及倍率 -1/0/1/2、无/单个/多个 blocker 的边界情况。这些是编译与数值检查，尚未进行 Unity 运行或 GPU 画面对照；全程未访问 Library。

画面复验建议：跨四个分界放置长地面与遮挡物并移动相机；调整 ShadowDistance；降低太阳高度观察地面与高空；分别将大气倍率设为 0、1、2；检查完全无遮挡区域的 PCSS 输出。

## 已修复（2026-10-14）

### 11. 大气步内积分的数值精度改进

天空单次散射、多重散射，以及多重散射 LUT 的 G_2 / f_ms 统一使用进入本步前的透射率和步内积分权重；散射贡献累加完成后再推进 opticalDepth。保留原有 32 步采样及步中介质/光源采样位置。

对本步近似恒定的消光 sigma_t 和光源项 source，贡献为 `T_before * source * (1-exp(-sigma_t*ds))/sigma_t`。实现把小值判断放在无量纲光学厚度 `tau=sigma_t*ds` 上；tau < 0.01 时用泰勒展开计算权重，避免 `1-exp(-tau)` 的消减误差，零消光时权重恰好为 ds。RGB 分量独立处理，兼容某个分量为零的情况。

代码：[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)。处理方式参考 [Hillaire 作者公开实现](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl#L205-L212)。这是离散积分质量改进，步内密度和光源仍采用常量近似。

验证：22 个 HLSL SM5 入口独立编译通过。将实际标量积分函数抽取为 CPU 单精度计算，与稳定双精度参考比较 10,033 个零/极小/较大光学厚度及分支边界案例，最大相对误差约 2.61e-6；零步长、零消光、零光源、混合 RGB 消光和分段常量介质/光源通过。均匀介质总光学厚度 4、单位消光和光源、32 步的结果从 0.92160699 改为 0.98168439，解析参考为 0.98168436；1 / 2 / 64 / 256 步也通过误差检查。这些数值不代表实际天空画面的误差或 GPU 实测精度。

尚未进行 Unity 运行或 GPU 画面对照；全程未访问 Library。后续复验可对照 32 与 128 步的天空和多重散射 LUT，并检查零大气倍率及地平线方向。

### 补充项：HDR 场景颜色、统一曝光与 tone mapping

状态：**输出链路已接通**。

管线现在将物体光照和天空写入同一套线性 ARGBHalf 场景颜色与独立深度缓冲，再由 ColorOutput Pass 统一应用曝光与 ACES 拟合曲线，输出到屏幕或 camera.targetTexture。输出 Pass 同时复制场景深度并保留 alpha。大气最终片元输出使用 float4。

相机按最终目标共享 HDR 颜色/深度：同一显示器的 Game 相机，以及指向同一个 RenderTexture 的相机共用一套；SceneView/Preview 等无 targetTexture 的相机分别持有。Depth / Nothing 相机继续在保留的线性结果上绘制，每次输出重新读取 HDR 原图，避免把已经 tone mapping 的颜色再次作为叠加底图。HDR 缓冲覆盖整个输出目标，GBuffer 保留相机自身尺寸；分屏相机使用 pixelRect。无深度的 RenderTexture 在输出端使用匹配 MSAA 样本数的独立深度。资源在尺寸变化、目标不再使用及管线销毁时释放。

当前管线资产的 **Color Output Settings** 默认设置为 **Tone Mapping = ACES、Exposure EV = -0.5**。曝光倍率为 `2^EV`，+1 档翻倍，-1 档减半；物体与天空共同应用。Tone Mapping = None 仅跳过曲线，曝光仍生效；用于对照或线性 HDR 输出时，应使用浮点、线性的 targetTexture，并按需将 Exposure EV 设为 0。普通 SDR 目标在 None 模式仍可能截断超范围亮值。输出 Shader 已通过资产引用，确保 Player 构建能够保留它。

ACES 采用 [Narkowicz 的拟合曲线](https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/)，是线性 RGB 的紧凑近似，不是完整 ACES 色彩管理或 UE 的全部 Film 设置。当前工程使用 Linear 色彩空间，sRGB 输出目标执行显示编码，Shader 不再重复 pow/gamma 处理；线性 RenderTexture 保留线性数值。此实现提供手动曝光，自动曝光和 HDR 显示器输出校准不在本次实现范围内。

代码：[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxColorOutputPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxColorOutputPass.cs)、[ColorOutputPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl)、[管线资产](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset>)。

验证：Player/Editor C# 托管 API 绑定编译通过（Unity 2022.3 程序集，CoreUtils/CommandBufferPool 接口替身），24 个 HLSL SM5 入口独立编译通过。新增 [Tools/ValidateColorOutput.ps1](D:/UnityProject/MaxSRP/Tools/ValidateColorOutput.ps1) 可直接运行：从当前源码抽取输出分组、尺寸、资源方法，在内存图形 API 替身上检查相机共享/隔离、分屏与小数视口、尺寸变化、MSAA 与目标深度变化、分配失败及幂等释放；从 Shader 抽取 ACES 表达式检查 10,001 个输入的单调性/有限性/范围、黑白点、超 1 的颜色、曝光和叠加顺序。另检查光照/天空接线、输出顺序、alpha/深度输出及构建所需的 Shader 引用。

尚未启动 Unity 或做 GPU 画面对照，全程未访问 Library。运行复验应覆盖 Game/SceneView、Depth/Nothing 相机叠加、分屏、窗口缩放、带/不带深度和 MSAA 的 targetTexture；在同一曝光下切换 None/ACES，检查太阳周围与地平线层次，并确认画面方向、深度和 Gizmos 正确。太阳强度标定和问题 8/10 的 IBL 链路仍单独待处理。

### HDR 输出的方向与亮度回归修正

用户在正确工程的 Unity 中反馈相机画面上下颠倒、天空与整体偏亮。之前独立编译与 CPU 数值检查没有覆盖真实 GPU 的投影和纹理方向约定。

修正：GBuffer 与 HDR 场景使用 renderIntoTexture=true 的 GPU 投影；_ProjectionParams.x 从实际投影方向计算。最终全屏输出按目标区分纹理与 backbuffer 的 Y 方向，颜色与深度使用同一 UV；SceneView/Preview 按纹理目标处理。屏幕空间阴影及大气 LUT 的 utility Blit 之后恢复相机矩阵；PostImageEffects Gizmos 切回最终目标的投影约定。顺带修复 unity_MatrixInvP 上传 viewMatrix 的笔误，现上传实际逆投影，已从待处理补充项移除。

默认曝光从 0 EV 调整到 -0.5 EV。当前 ACES 拟合曲线在 0 EV 下将线性中灰 0.18 映射为约 0.2669，而 -0.5 EV 映射为约 0.1748；此调整减轻中间亮度的抬升。曝光仍可在管线资产上修改。这是依据中灰响应的显示基准调整，不是对实际天空光照的完整标定。

验证：C# 托管绑定和 24 个 SM5 Shader 入口独立编译通过；ValidateColorOutput.ps1 加入屏幕/纹理目标、顶/底原点的投影与采样行一致性检查，并检查离屏投影与逆投影接线。真实 Unity 画面仍需刷新后复验；本轮尝试读取运行中的 HDR 数值，但尚未取得 GPU 统计，不能据此声称视觉回归已验证消失。

保留手动诊断菜单 **MaxSRP > Diagnostics > Capture HDR Buffer**。它读取已渲染的主相机 HDR 缓冲（无主相机输出时取其他活动输出），写入系统临时目录的 MaxSRP-hdr-probe.txt，包含亮度范围、非法数值比例、ACES 预测亮度及投影方向；不自动运行。若需要进一步排查偏亮，可先渲染一帧再执行该菜单。全程未访问 Library。

### 天空最终绘制改为显式 Pass

用户的 Frame Debugger 显示 Atmosphere Scattering 只有四次 LUT 绘制，随后直接进入 HDR Color Output，没有最终天空绘制。场景序列化的全局天空材质为空；旧路径临时设置 RenderSettings.skybox、调用内置 DrawSkybox 后又恢复，依赖内置天空材质选择与上下文状态。

现改为显式 **Procedural Skybox** 全屏三角形绘制，直接指定程序天空材质和 HDR 颜色/深度目标，不再修改全局 RenderSettings.skybox，也不再调用内置 context.DrawSkybox。阶段位置为：Atmosphere Scattering（四张 LUT）→ Procedural Skybox（Draw Procedural）→ HDR Color Output。

天空顶点位于远平面，ZTest LEqual、ZWrite Off，仅覆盖没有不透明物体的背景并保留场景深度。观察方向用当前离屏逆 VP 与相机位置重建，正交相机使用逆 view 的前向方向；避免依赖内置天空盒 mesh 或 object transform。太阳方向与单次散射使用同一朝向，并夹取 acos 输入到 [-1,1]。

位置：[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[SkyboxProceduralPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl)、[SkyboxProcedural.shader](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProcedural.shader)。

验证：C# 托管绑定和 24 个 HLSL SM5 入口独立编译通过；ValidateColorOutput.ps1 检查显式材质绘制接线、远平面深度规则、平移/旋转相机的天空射线回投影与正交观察方向，涵盖顶/底纹理原点及普通/reversed Z 模型。实际天空画面和新增 Frame Debugger 事件仍需 Unity 刷新后复验；没有据独立编译推断 GPU 画面已验证。全程未访问 Library。

### 接近海平面时天空出现弧形黑带

用户反馈相机接近 Y=0 时天空出现多条弧形黑带。默认 PlanetRadius=6,360,000 米、SeaLevel=0；这一量级下单精度间隔约 0.5 米，相机高出海平面 0.1 米仍会舍入到行星表面。旧球面求交连续进行大数相减，近交点可能变成零或微小负值，并错误选择行星另一侧的远交点；天空和多重散射只处理 d>0，又会忽略零距离地面命中，继续向行星内部积分。

修正球面求交：以 `(r-radius)*(r+radius)` 计算半径平方差，在开方前判断是否相交；先计算不会发生近似相等数相减的根，再由根的乘积求另一个根。支持非单位方向，返回最近的非负交点。

地面使用独立的固体边界规则：位于地表时，朝下返回零距离命中，朝上或相切不遮挡；起点在地下直接阻断。天空、多重散射及太阳盘统一使用该规则；积分接受 d>=0 截断，并跳过零长度或无效路径。采样高度夹取到非负，避免微小舍入误差产生地下密度。积分内的透射率和多重散射 LUT 查询改为显式 LOD 0，避免新增分支触发隐式纹理导数和循环展开限制。

SkyView LUT、太阳盘和空气透视 LUT 统一调用 AtmosphereCameraPosition。大气计算的相机高度下限为海平面以上 1 米，低于该高度或进入地下时使用此下限；这是近地表的数值边界约定，不会移动实际相机或改变场景几何。保留散射系数、采样步数、太阳强度及曝光设置。

代码：[Helper.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)、[Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl) 及对应天空、太阳盘和空气透视 Pass。

验证：[Tools/ValidateAtmosphereGround.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAtmosphereGround.ps1) 抽取实际 HLSL 函数进行 CPU 单精度计算，对照独立双精度求交，覆盖从地下 100 米到高空 60 千米的 140,910 组高度/方向及 LUT 行，并检查地表朝上/朝下、相切、地下起点、球内出射、未命中、平移球体、非单位和零方向。最大交点距离相对误差约 3.80e-6；贴地的 64 个向下 LUT 行，旧实现有 38 行零距离、15 行远交点，新实现均命中近处地面。共享相机原点、地面截断和零长度保护的接线检查通过；24 个 HLSL SM5 入口独立编译和 ValidateColorOutput.ps1 回归检查通过。

尚未进行实际 GPU 画面对照。Unity 刷新后应在 Y=-1、0、0.1、1、2、10 米连续移动相机，观察地平线及太阳盘；另修改 SeaLevel 检查边界随海平面移动，并检查高处和向下观察时地平线仍正常。全程未访问 Library。

### 太阳圆盘变扁：隔离阴影相机对主相机的修改

用户确认上一版修复后近海平面天空已稳定，但太阳在高于地平线、画面中央时仍呈椭圆，排除了单纯地平线裁切。检查发现阴影 Pass 使用主相机临时渲染四个级联，并将 aspect 改成 1，再恢复保存的数值。Unity 的 [Camera.aspect 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera-aspect.html)明确说明：赋值后宽高比保持固定，直到调用 ResetAspect；恢复数值不能恢复自动跟随视口的状态。这会在窗口或视口比例变化后拉伸物体及太阳盘。

阴影现在使用独立、禁用且不保存的 Camera，复用四次级联绘制，并在 Pass 销毁时释放其 GameObject。级联范围仍从原相机视锥计算；源相机的 aspect、正交状态、位置、旋转、裁剪面、targetTexture、自定义 projection/view 矩阵均不再被临时修改。阴影相机清除从源相机复制的自定义矩阵，使用全幅视口，显式设置 1024×1024 阴影视口。删除旧的主相机参数保存/恢复代码。

新增菜单 **MaxSRP > Diagnostics > Reset Camera Aspect To Automatic**，显式恢复主相机与 Scene View 相机的自动宽高比，用于清除旧代码在当前会话留下的覆盖。修复不会自动重置用户有意指定的 Game 相机宽高比；执行该菜单才会重置主相机的覆盖。

同时处理用户粘贴的 LUT 报错：调用栈在 Blit，Expected 为大气 LUT Shader，Actual 为 Hidden/Internal-Loading。大气数据 LUT 不应使用异步编译期间的占位 Shader 生成数据。Editor 中仅在四张 LUT 烘焙命令范围内调用 ShaderUtil.SetAsyncCompilation(cmd,false)，随后 RestoreAsyncCompilation 恢复原模式；Player 不引入 UnityEditor 依赖，也不修改全局异步编译设置。参考 [Unity 的命令缓冲异步编译控制](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderUtil.SetAsyncCompilation.html)。该处理针对占位 Shader 路径，尚未实测确认日志断言已消失。

代码：[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[MaxColorOutputDiagnostics.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Editor/MaxColorOutputDiagnostics.cs)。太阳角半径和屏幕射线公式保持原实现。

验证：[Tools/ValidateShadowCameraIsolation.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShadowCameraIsolation.ps1) 抽取实际 Execute/Dispose 方法，在图形 API 替身上检查源相机自动/手动 aspect、视口缩放、自定义矩阵、分屏视口、目标纹理、四个级联、隐藏相机复用、剔除失败、异常、空范围、无太阳以及幂等资源释放，并检查 LUT 编译模式作用域。按 Unity 文档模拟 aspect setter，旧实现从 5:4 视口切换到 16:9 后，居中角圆盘宽高比为 1.422；新路径保留自动更新，宽高比为 1。Player/Editor C# 使用 Unity 2022.3 托管程序集绑定编译通过（CoreUtils/CommandBufferPool 接口替身）；ValidateColorOutput.ps1 与 140,910 组近地表求交回归通过。

这是编译、API 模型和几何数值验证，未取得实际 GPU 的圆盘形状或最新报错统计。刷新后可执行一次恢复自动宽高比菜单，再在 Game/Scene View 改变窗口比例，保持太阳位于画面中央并高于地平线，检查圆盘及阴影。全程未访问 Library。

### 13. Lighting Pass 接入大气雾

状态：**功能已接通**；已从待处理列表移除。用户已反馈此前天空稳定性和太阳圆盘修复结果正常，本项画面仍需单独复验。

大气数据准备移至 Lighting 之前，每个相机生成自己的 LUT 并恢复离屏相机矩阵。顺序为：GBuffer → 屏幕空间阴影 → Atmosphere Scattering（透射率、多重散射、天空及空气透视 LUT）→ LightPass（物体光照与雾合成）→ Procedural Skybox → HDR Color Output。最终天空绘制不重复烘焙 LUT。

空气透视保留 32×32×32 的体素规模，按深度切片横向打包为 1024×32 的二维图集。改用一次全屏三角形 MRT 绘制，分别输出线性 ARGBFloat 的 RGB 散射光和 RGB 透射率；保留透射率的颜色差异。透射率直接来自与散射光相同路径的 exp(-opticalDepth)，删除原来的两次顶端透射率查询相除及 RGB 求平均，避免不匹配路径和极小值相除。

LUT 使用该相机实际 renderIntoTexture=true 的逆 GPU 投影与 camera-to-world 矩阵重建射线，覆盖真实 FOV、宽高比和投影翻转。输入采用专用材质矩阵，避免前面的 utility Blit 改写 Unity 内置矩阵。正交相机使用平行射线和各像素在相机平面上的起点；保持此前海平面以上 1 米的最低大气计算高度约定。

深度切片使用 `distance = (slice/31)^2 * maximumDistance`，第 0 层明确为距离 0（S=0、T=1），第 31 层为最大覆盖距离，近处采样更密。消费者按物理距离在相邻深度层间插值，每层内部双线性采样；屏幕坐标夹在该切片的 texel 中心范围内，避免横向边界串到另一层。超过最大距离使用最后一层；这是一套有限距离、有限分辨率的近似，不代表无限远积分。

Lighting 根据 GBuffer 深度重建物体位置，透视相机使用相机到物体的射线长度，正交相机使用向前深度。在线性 HDR 中合成 `surfaceRadiance * RGBTransmittance + RGBInScattering`，之后与天空共同曝光和 tone mapping。空深度背景提前走原有分支，天空不再叠加一次物体雾；Depth/Nothing 叠加相机仅对本次新绘制的物体合成，保留背景已有结果。

管线资产 **Atmosphere Scattering Setting > Enable Aerial Perspective** 默认启用；`_AerialPerspectiveDistance` 默认 32,000 米，表示 LUT 的最大覆盖距离，并非雾浓度。关闭开关或设为 0 会停用物体雾并跳过空气透视 LUT 绘制，天空仍可绘制。雾也可作用于非 Skybox 清屏相机。移除无消费者的旧 AerialPerspective 合成材质及错误的独立合成 Shader 引用；新增透射率图集与既有资源一起释放。

代码：[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[PBRLitPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl)、[AerialPerspective.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl)、[AerialPerspectiveLUTPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUTPass.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)。

验证：[Tools/ValidateAerialPerspective.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAerialPerspective.ps1) 抽取实际 HLSL 的图集坐标、深度插值和 32 步积分，对照独立图集/解析参考：10,000 组屏幕坐标与距离、全部切片边缘、零距离、零介质、RGB 消光和散射、地面截断、线性 HDR 合成、108 组透视和 36 组正交的 FOV/宽高比/纹理原点/Z 约定射线回投影及距离。图集采样最大绝对误差约 3.90e-6，均匀介质 RGB 积分通过 3e-6 的绝对误差检查。检查当前相机数据准备先于 Lighting、背景绕过合成、双 MRT/纹理接线、开关和资源释放。Player/Editor C# 托管绑定、24 个 HLSL SM5 入口独立编译，以及 HDR 输出、140,910 组近地表求交和阴影相机隔离回归通过。

这些是独立编译和 CPU 数值/API 模型验证，尚未取得 Unity GPU 画面对照。刷新后应将物体放在数十米、数百米和数千米处，在同一曝光下切换雾开关，观察远处对比度与色彩变化；改变 FOV、窗口比例、太阳高度及相机高度，检查太阳和天空保持正常，并复验正交、分屏与相机叠加。当前雾不额外查询场景遮挡来生成体积光束，也不增加透明物体渲染功能；这些不属于本次问题 13 的接线修复。全程未访问 Library。

### Scene View 阴影位置及覆盖范围修正

用户反馈 Scene 视图有阴影，但位置或范围不对，Game 视图尚未对照。当前管线已按每个相机重建级联，屏幕空间阴影资源也按相机持有；本次修正这些路径中的矩阵依赖和覆盖计算。

屏幕空间阴影及两次模糊改为显式全屏三角形绘制，指定目标尺寸、Cull Off、ZWrite Off 和 ZTest Always。接收点世界位置及级联深度使用传入相机的专用 inverse VP/view 矩阵，逆 VP 来自与 GBuffer 相同的 renderIntoTexture=true 投影；噪声坐标使用对应相机尺寸。消除 Blit 顶点变换与内置相机矩阵可能被 utility draw 改写的依赖，Scene 相机不读取其他相机的重建矩阵。

级联角点由当前相机实际 CPU 逆投影射线与目标 view-depth 平面求交，再用实际 inverse view 变换到世界空间，支持正交、非对称投影和自定义 view 矩阵。光空间包围盒、texel 对齐与阴影相机统一使用同一 Quaternion.LookRotation/TRS 基底；太阳方向接近竖直时选用不会共线的 up 向量。光空间深度半范围取 ShadowDistance 与包围盒实际半深度加对齐余量的较大值，避免宽视角或负 near 的正交 Scene 相机在 Z 方向被固定范围裁掉。前两级 PCSS 使用各自实际深度半范围进行距离换算。

阴影贴图使用真实的纹理目标 GPU 投影，Caster 顶点通过专用 _ShadowCasterVP 绘制；接收端 PCSS 和 HardShadow 使用同一个 ShadowVP，并统一将 NDC 转换为纹理 UV 的 Y 原点约定。PCSS 对贴图或深度范围外的接收点返回全亮。阴影 Pass 返回是否实际取得有效绘制路径；无太阳、无级联范围或所有级联剔除失败时，当前相机的屏幕阴影清为全亮，避免沿用其他相机结果。

ShadowDistance 仍按当前相机向前深度限制，默认 500 米，不改为以 Scene View 焦点为中心的额外范围。问题 18 的多方向光选择差异仍单独待处理。

代码：[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)、[MaxSceenSpaceShadowMapPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxSceenSpaceShadowMapPass.cs)、[ScreenSpaceShadowMapPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl)、[Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl)、[ShadowInput.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/ShadowInput.hlsl) 及管线调用和 Shader Pass 状态。

验证：[Tools/ValidateSceneShadows.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSceneShadows.ps1) 抽取实际角点、光空间包围盒和阴影相机配置函数，核验 384 个非对称透视/正交/负 near 角点、1,536 个光空间覆盖案例（包括宽视口和竖直太阳）、24 组透视/正交及纹理原点/Z 约定的深度重建，并检查专用相机输入、Caster/receiver 矩阵和纹理行对应关系。阴影相机隔离测试增加实际可用状态和剔除失败检查。Player/Editor C# 托管绑定、26 个 HLSL SM5 入口独立编译（新增显式 Caster 顶点/片元入口），以及 HDR 输出、近地表大气与空气透视回归通过。

尚未取得实际 Scene/Game GPU 截帧。刷新后在 Scene 视图中平移、旋转、缩放并切换透视/正交，再与 Game 视图交替观察同一地面和遮挡物，检查阴影是否贴合物体并覆盖当前相机范围；对照四级边界和 500 米范围外的全亮结果。全程未访问 Library。

### Game 视窗比例随实际尺寸变化：撤销固定比例适配

上一版错误地把用户“避免压缩画面”的要求解释为固定比例居中适配，新增了视窗缩小及黑边。用户未要求固定比例；这些实现已撤掉，对应文档说明和测试也已替换。

Game 相机直接使用实际 pixelRect；有 targetTexture 时使用其完整尺寸。GBuffer 按此视窗分辨率分配，HDR 输出保留完整目标画布以支持分屏/叠加；删除 FitViewport、黑边清理调用及 ViewportBars Shader Pass。1920×1080 的全幅 Game 视窗对应 1920×1080 GBuffer、480×270 屏幕空间阴影和 1920×1080 HDR 输出。

新增统一的有效投影查询：Game 的水平投影系数由实际视窗宽高比计算，保持原垂直 FOV、透视/正交类型、深度投影及 lens shift，通过变化水平视野适配窗口，避免残留 Camera.aspect 覆盖值压缩画面。返回矩阵副本，不修改源相机 aspect、rect 或 projectionMatrix。Scene/Preview/Reflection 保持原投影。

相机剔除矩阵及六个剔除平面、GBuffer/光照/天空矩阵、阴影级联角点、屏幕空间阴影和空气透视 LUT 全部使用同一有效投影，直接上传 Shader 使用的 view、GPU projection、VP 及逆矩阵，避免只修正颜色而让剔除、阴影或雾仍使用旧比例。Gizmos 与最终输出使用真实视窗。

代码：[CameraViewport.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/CameraViewport.cs)、[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[ShaderBinding.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs) 及阴影、大气和输出调用。

验证：[Tools/ValidateCameraViewport.ps1](D:/UnityProject/MaxSRP/Tools/ValidateCameraViewport.ps1) 执行实际布局/投影代码，核验 10,000 组真实视窗、圆形不变椭圆和 GBuffer 尺寸案例，窗口横竖切换、分屏偏移、targetTexture、非 Game 相机隔离及 FOV/lens/depth/源设置保持，并检查有效投影贯穿剔除、绘制、阴影和大气。Player/Editor C# 托管绑定和 26 个 HLSL SM5 入口独立编译通过；HDR、Scene 阴影、阴影相机隔离、近地表大气和空气透视回归通过。

尚未取得实际 Game GPU 画面对照。刷新后应检查 GBuffer 尺寸与实际 Game 视窗一致，窗口比例变化时圆形保持圆形、可见水平视野相应变化，画面内无管线新增黑边，并复验分屏、阴影和雾。Game View 自身的分辨率预设和编辑器面板布局仍由 Unity 控制。全程未访问 Library。

2026-10-15 收尾检查：正交相机的 unity_OrthoParams 宽高也改由实际投影系数倒数取得，不再使用残留的 camera.aspect。视窗测试从实际 ShaderBinding 源码抽取这段参数计算，在同一批 10,000 组比例案例中检查宽高与有效投影一致；Player/Editor C# 绑定及全部现有数值回归通过。当前共享 Editor.log/Editor-prev.log 不属于 MaxSRP，不能用其日志声称本工程的 GPU 刷新已验证。

### Game 画面再次上下颠倒的回归处理（2026-10-15）

用户反馈 Game 画面再次上下颠倒。最近相机与阴影调整新增了 CommandBuffer.SetViewProjectionMatrices，并将已由 GL.GetGPUProjectionMatrix 转换的 GPU 投影传给它，混入已有的显式 SRP 矩阵上传流程。[Unity 2022.3 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetViewProjectionMatrices.html)明确说明该接口不兼容自定义 SRP；这是本次撤销的直接回归点。

删除主相机与阴影 Pass 的两处调用。相机继续直接上传 GPU view/projection/VP 和逆矩阵；阴影 Caster 与 receiver 使用同一个专用 ShadowVP。保留此前 RenderTexture/屏幕目标的输出 UV 约定，颜色与深度仍使用同一采样方向；实际视窗尺寸、无固定比例/无新增黑边的逻辑保留。

代码：[ShaderBinding.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs)、[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)。

验证：Player/Editor C# 托管绑定、视窗/正交参数、HDR 输出方向、阴影相机隔离和 Scene 阴影数值检查通过。ValidateColorOutput.ps1 新增检查，禁止在相机与阴影路径中混用该 Built-in 接口，并确认相机及专用 Caster 矩阵直接上传。此前这些测试只检查矩阵数值与采样公式，没有覆盖该 Unity 状态 API 的兼容性，不能用之前通过的 CPU 检查否定用户的实际画面反馈。

尚未取得本次真实 GPU 画面对照，需刷新后复验 Game 与 Scene 的方向、阴影、深度和 Gizmos。全程未访问 Library。

## 已修复（2026-10-15，20～26 与大气 LUT 缓存）

本轮按用户指定处理 20、21、22、23、24、25、26，并优化大气 LUT 更新。以上七项已从待处理区移除；当时剩余的 8、10、18 已由下方动态天空 IBL 记录解决，19 后于 10 月 20 日单独修复。

| 原编号 | 修复结果 | 代码位置 |
| --- | --- | --- |
| 20 | Mie 吸收改为 extinction - scattering，即 (4.440 - 3.996)e-6 /m；倍率同时作用于散射与吸收，总消光恢复地球基准的 4.440e-6 /m。未用曝光补偿这个介质参数错误。 | [Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:62) |
| 21 | 统一观察方向查询：正交使用 inverse view 的 +Z（指向观察者），透视保留 cameraPosition - position；点光循环只更新光线方向，不再覆盖 V。 | [Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl:8) |
| 22 | 法线解码覆盖 RGB、AG/DXT5nm、BC5 和 ASTC；两处 Shader 的缺省法线改为 bump，与新解码组合得到切线空间 +Z。 | [Packing.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Packing.hlsl:5)、[PBRGBuffer.shader](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBuffer.shader:9) |
| 23 | blocker 距近平面为零时，使用由 Depth24 精度和最小世界距离约束的正分母；滤波 UV 半径限制在 [0,1]，搜索和滤波越界样本按无覆盖处理。保留 count=0 提前返回，两条零分母路径均有边界检查。 | [Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:143) |
| 24 | 四级 DepthNormalBias 接入 PCSS/HardShadow，定义为世界单位，按级联完整深度范围换算为归一化深度，并在掠射角适当放大。reversed-Z 与普通 Z 分别向光源方向偏移。保留独立的 ShadingPointNormalBias 世界位置偏移。 | [Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:105)、[ShadowSetting.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShadowSetting.cs:50) |
| 25 | 增加采样深度 [0,1] 与 clip depth 的双向转换，Lighting/屏幕空间阴影反投影共用。PCSS/HardShadow 的比较、距离换算、有效深度范围和 bias 符号均按 UNITY_REVERSED_Z 分支处理。 | [SpaceTransform.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/SpaceTransform.hlsl:6)、[Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:86) |
| 26 | 稳定求大气球的完整 entry/exit 区间，只积分与大气及地面裁剪后重叠的部分。maxDis 仍从原观察者计量，正确跳过真空；层外透射率查询移到入射点，无大气相交时返回单位透射率，太阳盘在太空仍可见。LUT 映射约束边界输入，避免负判别式开方。 | [Helper.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl:76)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:218)、[SkyboxProceduralPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl:26) |

Mie 基准来自 [Hillaire 作者公开的 SetupEarthAtmosphere](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Application/SkyAtmosphereCommon.cpp#L30-L35)，原文件单位 /km，此处已换为 /m。默认 Mie 消光较修复前减小，因此天空、雾及太阳衰减会相应变化；曝光设置保持原值。

### 大气 LUT 改为按依赖失效

代码：[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:160)。透射率和多重散射全局共享，SkyView 与两张空气透视贴图按相机分别保存。仅需要天空或雾时生成对应数据，命中缓存时仍重新绑定当前相机的雾纹理及布局。

| LUT | 重新生成条件 |
| --- | --- |
| Transmittance | 行星半径、大气高度、介质散射/吸收倍率与密度剖面改变，或纹理丢失。太阳、相机变化不重建。 |
| Multiscattering | Transmittance 已重建、Mie 各向异性改变，或纹理丢失。太阳方向参数化在 LUT 中，不随太阳移动重建。 |
| 每相机 SkyView | 大气版本、有效观察高度、太阳方向/强度/颜色改变，或该相机天空贴图丢失。仅改变朝向、投影或太阳盘角度不重建。 |
| 每相机 Aerial Scattering/Transmittance | 大气版本、观察高度、太阳方向/强度/颜色、实际 GPU 投影、camera-to-world、正交模式或雾最远距离改变，或纹理丢失。 |

太阳方向明确传给缓存判断，避免在 CPU 上读取尚未提交的 CommandBuffer 全局变量。此阶段未改变主光选择；后续动态天空 IBL 修复已统一为 RenderSettings.sun，详见下方记录。

纹理只在需要时分配。完整天空+雾缓存约增加 1.5 MiB/相机；相机销毁时或管线 Dispose 时释放。Editor 的 Scene/Game/Preview 可能在不同 Render 调用中传入，因此不会仅因相机缺席本次相机列表就销毁其 LUT。分配或命令录制失败会使缓存失效；纹理内容丢失后重建。Editor 项目资源变化（包含 Shader 导入）会使缓存失效，退出管线时取消事件订阅。

### 本轮验证

- Player/Editor C# 绑定编译通过：本机 Unity 2022.3 程序集，22 个源文件，CoreUtils/CommandBufferPool 为接口替身；10 处池化 CommandBuffer 分配均有 finally 释放。
- [ValidateShaders.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShaders.ps1)：26 个 SM5 入口，加普通 Z/GL clip 范围模拟、RGB 和 ASTC 的 15 个片元编译组合，共 41 项通过。GL 深度分支是在 HLSL 编译器中模拟，不是原生 GLSL 编译或 OpenGL GPU 验证。
- [ValidateLightingShadowMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateLightingShadowMath.ps1)：抽取当前 Shader 实际函数，四种组合各检查 10,000 组采样/clip 深度往返及投影还原、bias 与比较方向、有限 PCSS 半径；覆盖零距离 blocker、不同 Mie 高度/倍率、中性与倾斜法线、正交平行视线及透视回归。另核对四级 bias 调用和两处法线默认值。
- [ValidateAtmosphereCaching.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAtmosphereCaching.ps1)：完整执行生产缓存类，100 个静止帧及 20 轮双相机交替不产生 LUT 绘制；验证各参数对应的失效集合、缓存命中重绑纹理、关闭/重开雾、纯色背景、纹理丢失、Editor 资源失效、分配/录制失败、相机销毁及幂等 Dispose。
- [ValidateAerialPerspective.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAerialPerspective.ps1)：新增 70 km 层外观察者的真空段、入射点前/后、地面裁剪、无限距离、背向和相切案例；原有 10,000 组打包采样及 144 组射线检查通过。透射率独立检查覆盖层外朝太空、侧向不相交及穿过壳层。
- 原有视窗、HDR 输出、Scene 阴影、阴影相机隔离和 140,910 组近地表检查全部通过；相关接线检查已随新的缓存归属及入口签名更新。

没有启动 Unity 构建、读取 Library 或进行 GPU 截帧；以上不代表实机视觉与耗时已经验证。后续应复验 Game/Scene 方向和正交高光、无贴图材质、四级 bias 调整、太阳变化、海平面与层外天空，并在 Frame Debugger 中确认静止帧不再重建 LUT。非 reversed-Z 的真实平台画面仍需在对应 API 下验证。

## 已修复（2026-10-15，动态程序天空 IBL：8、10、18）

实时管线移除固定室内 cubemap 的启动烘焙。Lighting 前先准备大气，再从独立的环境 SkyView LUT 生成 HDR cubemap、漫反射卷积和 GGX 镜面预滤波，最后一起绑定给 Lighting。天空、物体雾和间接光由同一套大气参数与太阳驱动；所有 IBL 输入均为曝光前的线性辐射，最终仍统一经过现有曝光及 tone mapping。

| 原编号/事项 | 当前结果 |
| --- | --- |
| 8：实时 IBL 的 HDR 格式 | 环境 cubemap、漫反射、全部镜面 mip 和 BRDF LUT 均使用线性 ARGBHalf，环境 SkyView 使用 ARGBFloat。计算结果经数组 UAV 和 GPU CopyTexture 写入 cubemap，全程不经过 8 位像素存储。 |
| 10：程序天空与 IBL 不一致 | 使用同一大气模型生成独立的全局环境；太阳方向、颜色、强度及大气参数改变时更新，环境结果在 Lighting 前生效。原管线资产 m_envMap 已移除。 |
| 18：主光/阴影不一致及无主光残留 | 只使用 RenderSettings.sun；每次 Render 捕获共享太阳数据，直射、阴影、天空、雾和 IBL 使用同一来源。额外方向光被忽略；未指定、禁用、非方向光或 intensity 为零时清零直射颜色和方向，并关闭太阳散射源项及太阳阴影，避免沿用上一帧数据。大气内部保留安全占位方向以防 normalize(0)。 |
| 启动同步 IBL 烘焙 | 活跃管线不再调用旧 BakeAndSubmit，不使用 ComputeBuffer.GetData、CPU 像素数组或 SetPixels/Apply。BRDF LUT 首次渲染时在 GPU 生成，后续环境变化复用。 |

环境观察高度固定为海平面上方 **Reference Height**（默认 2 米，最小 1 米），不跟随 Game/Scene 相机。相机移动或旋转不会改变全局 IBL；各相机背景与空气透视仍按自己的高度和视图计算。这是一个全局环境近似，尚未加入局部反射探针或室内遮挡。

管线资产新增 **Sky IBL Settings**：镜面分辨率默认 128（32～256，取最近二次幂），漫反射固定 32；Diffuse Samples / Specular Samples 默认各 128（32～512）。镜面拥有从基准尺寸到 1×1 的完整 mip，mip 0 直接复制环境，其他 mip 按 GGX 粗糙度预滤波；消费者使用 roughness × (mipCount - 1)。漫反射存储 E/π，与当前 GetDiffuseIBL 直接乘 albedo 的约定匹配。BRDF LUT 为 256×256，每 texel 使用原 IntegrateBRDF 的 1024 样本。

环境中保留天空散射和太阳附近光晕，不加入解析太阳圆盘，避免该高能小光源同时进入 IBL 与方向光 BRDF；可见天空仍正常绘制圆盘。Sun Disk Angle 只改变可见太阳，不触发环境卷积。此阶段太阳颜色使用线性颜色乘 intensity，大气仍另乘原有 SunLightIntensity 与天空色调。这项遗留增益随后导致天空 IBL 过强，已在下方能量统一修复中移除。

环境 SkyView 按太阳、大气版本、参考高度或纹理丢失失效；IBL 按环境源/版本、采样数、分辨率或资源丢失更新。静止帧只重绑纹理，没有 compute dispatch 或 cubemap 拷贝。BRDF 独立缓存，纹理丢失或 Compute Shader 依赖变化时重建；Editor 资源导入会使大气缓存失效。漫反射与镜面双缓冲，整套录制完成后才发布；执行失败保留先前发布的输出并标记重试，分配失败清理已分配资源，Dispose 释放全部纹理并取消事件订阅。

GPU 路径要求 Compute Shader、2D Array 与不同纹理类型间 CopyTexture 支持；不满足时明确报错。当前仍是一次完成更新，太阳连续运动时每帧更新环境卷积，尚未采用跨帧摊销；实际耗时需要在目标硬件量测。点光保留原有最多 32 盏路径，cluster lighting 留待后续。

旧 Runtime/GI 的手动/离线 probe 工具保留，仍含同步回读和 RGBA32 输出；它们已不参与这条实时天空 IBL 路径。问题 8 的关闭范围是当前实时管线，不能解读为这些遗留工具也已改为 HDR。问题 19 的 Lighting Shader 构建引用后于 10 月 20 日单独修复。

代码：[SunLightData.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/SunLightData.cs)、[MaxUpdateGIPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxUpdateGIPass.cs)、[DynamicSkyIBL.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/DynamicSkyIBL.hlsl)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxLightConfigurator.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxLightConfigurator.cs)。

验证：

- [ValidateDynamicSkyIBL.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDynamicSkyIBL.ps1) 执行实际太阳、光配置和 IBL 运行时代码，使用命令/纹理替身检查六面和全部 mip 初始化、HDR GPU 拷贝、100 个缓存帧、整套切换、源/版本/质量变化、BRDF 独立丢失、执行与全部七处纹理分配失败恢复、重复释放；覆盖额外方向光、40 个可见点光、太阳不在可见光列表及各无太阳状态。这不等同于 GPU 像素验证。
- [ValidateSkyIBLMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSkyIBLMath.ps1) 抽取实际 HLSL 采样与卷积循环，对照独立 cube 寻址与解析半球积分：1,200 组 cube/天空 UV 往返、含上下极点的 106 个法线、全部粗糙度 mip 的恒定 HDR 归一化和有限 LOD。解析漫反射最大绝对误差约 0.002412。
- Player/Editor 使用本机 Unity 2022.3 程序集完成 23 个 C# 文件绑定编译（CoreUtils/CommandBufferPool 为接口替身），11 处池化命令均有 finally 释放。[ValidateShaders.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShaders.ps1) 的 30 个 SM5 入口及 15 个深度/法线编译组合均通过，包含四个新增 compute 入口；GL 分支仍是 HLSL 编译模拟。
- 大气缓存、空气透视、近地表、Game 视窗、HDR 输出、Scene 阴影、阴影相机隔离及光照/阴影数值回归全部通过；环境缓存检查增加固定参考高度、相机移动隔离、太阳颜色与禁用太阳的更新条件。

本轮未访问 Library、未启动 Unity 构建或取得 GPU 截帧。刷新工程后，在 Frame Debugger 首次/太阳变化帧应看到 Sky Environment、Sky Diffuse Convolution、Sky GGX Prefilter，BRDF LUT 只在需要初始化时出现；静止帧没有这些计算。用光滑金属球和粗糙漫反射球对照太阳旋转/调色/调强度，切换 Game/Scene 并移动相机，检查反射方向、粗糙度过渡与全局 IBL 稳定性；禁用太阳应清除太阳来源贡献。实测耗时后再确定是否分帧更新。

## 已修复（2026-10-15，太阳/天空/IBL 能量比例与地面太阳衰减）

用户验证动态 IBL 后反馈白色地面被厚重蓝色覆盖。上一版虽然共享太阳颜色与 intensity，大气仍额外乘旧 SunLightIntensity=31.4；该增益通过新 IBL 路径进入物体照明，而太阳直射没有同等增益。背景来源一致不代表能量比例一致。本次删除大气独立的 SunLightIntensity 和 SunLightColor 字段及资产数据，改为唯一的 SolarIrradiance 输入，直射、天空、雾、IBL 共用。

管线资产新增 **Sun Illuminance Lux**，默认 **120000 lux**，表示白色太阳在大气外、垂直光束平面上的照度；指定太阳 Light.intensity 作为乘数，Light.color.linear 作为 RGB 滤色，调色会同时影响全部太阳来源。该值不是地面水平照度；实际地面照度还包含大气透射率、N·L 和场景阴影。颜色为白且 Light.intensity=1 时，该资产值就是入射照度；着色后 RGB 的 Rec.709 亮度权重和可用于读取模型的照度/亮度，彩色滤光会改变实际亮度。

所有太阳来源统一使用固定缩放：`E_internal = E_lux_RGB / 100000`，`L_internal = L_cd/m²_RGB / 100000`。散射相函数带有 sr⁻¹，漫反射 BRDF 含 1/π，天空半球积分返回照度，现有漫反射 IBL 仍存储 E/π；不再额外增加天空倍数。固定缩放在 direct/sky/fog/IBL 和最终 HDR 颜色中一致，用于给现有浮点纹理与曝光留出数值范围。它与相机曝光分开；Exposure EV 仍是相对补偿，+1 加倍，不是物理相机的 EV100。本次保持原曝光 -0.5 和 ACES 不变，也没有对天空去色。

Lighting 的方向光新增 `SurfaceSunTransmittance`：用物体实际世界高度、SeaLevel 和 PlanetRadius 映射到与天空相同的局部行星柱，查询同一 Transmittance LUT，并经过行星遮挡判断。太阳变低时直射随大气路径变暗、偏暖；移除原来按 lightDir.y 硬切太阳的独立分支，高空物体可按自身地平线受光。局部平面世界假设保持不变，水平位置不改变所在行星柱。即使关闭物体雾或相机采用纯色背景，也会准备并绑定太阳透射率与所需介质全局参数；Dispose 解除自己持有的全局透射率贴图。

可见太阳盘也从同一能量推导：均匀圆盘亮度 `L = E / (π sin²α)`，α 为原 Sun Disk Angle（角半径，单位度）；默认角半径仍为原来的 1°，本次不改轮廓大小。圆盘再乘沿观察方向的大气透射率。天空 IBL 仍不包含解析太阳盘，避免重复计算直射。亮度会随角半径改变而保持圆盘积分能量一致；极端强度/极小角半径仍受现有 ARGBHalf 场景目标的有限范围约束。

验证：[ValidateDaylightEnergy.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDaylightEnergy.ps1) 抽取实际 HLSL 的系数、相函数、球求交、32 步透射率/多重散射/天空积分，重建 256×64 Transmittance 与 32×32 Multiscattering LUT，再用独立 48×96 半球求积计算无场景遮挡的水平面照度。白太阳、默认大气、海拔 2 米、120000 lux 入射时结果如下（CPU float 与双线性纹理模型，非 GPU 测量）：

| 太阳高度 | 地面水平直射照度 | 天空照度 | 天空占总照度 |
| --- | --- | --- | --- |
| 90° | 104778 lux | 6213 lux | 5.6% |
| 45° | 70454 lux | 5936 lux | 7.8% |
| 10° | 10362 lux | 4082 lux | 28.3% |

在相同地面直射基线上，单独把天空乘回 31.4，天顶天空占比会变成约 65.1%，解释了原增益为何会造成强蓝色覆盖；这个对照用于隔离增益，不是旧 GPU 帧的精确复现。当前天顶白色理想 Lambert 平面的曝光前 RGB 约为 (0.369, 0.351, 0.335)，不再由蓝色分量主导；这项参考不含介电 Fresnel、镜面或场景阴影。

没有把天空占比强制拟合到上一轮引用的 UE 20% 示例。该值依赖太阳高度、气溶胶、地面反射和天气；当前 RGB 模型没有地面多次反弹/完整光谱计算，与 UE 的示例配置也不同。保留阴影中的蓝天照明与金属反射中的蓝天色。

测试还检查垂直大气柱的解析 Beer–Lambert 透射率、太阳输入加倍时天空严格加倍、0.2725°/1°/2° 圆盘积分能量、夜间行星遮挡、太空无衰减、真空无衰减及关闭太阳无残留；单太阳测试增加 lux 转换与零/负入射照度，缓存测试改用共享太阳能量驱动失效。C# Player/Editor 绑定、45 项 Shader 独立编译、动态 IBL/归一化、大气缓存/空气透视、HDR 输出和光照阴影数值回归通过。

代码：[SunLightData.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/SunLightData.cs)、[MaxRendererPipelineAsset.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipelineAsset.cs)、[Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)、[Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)。

未访问 Library 或取得本轮 Unity GPU 对照。刷新后首先在阳光下看白色粗糙地面，再看阴影区；受光区应由直射主导，阴影可偏蓝。改变 Light.intensity 或 Sun Illuminance Lux 应同时改变直射/天空/雾/IBL，改变 Exposure EV 应只改变整体显示亮度。此阶段点光仍保留旧单位、输出仍缺少物理曝光基准；这两项随后造成亮度适配不完整，已在下方补齐。自动曝光、白平衡和 cluster lighting 仍未增加。

## 已修复（2026-10-15，Lighting 全源单位适配、EV100 与场景灯光）

用户反馈物理太阳接入后 Game 画面发黑，并指出其他光源与曝光也需要适配。检查确认：太阳/天空已经使用照度与统一 HDR 缩放，点光仍直接上传无单位的 VisibleLight.finalColor，输出端也只有原有 2^补偿，没有物理相机基准。上一轮白色理想平面照度通过，并不能证明真实材质与最终显示亮度已经闭环。GBuffer 的 albedo 是线性反射率，normal/roughness/metalness 也是材质数据，不能在这里乘 lux 或曝光；本次保持 GBuffer 数据约定和 BRDF 不变。

### 每盏灯只有一个强度来源

删除管线资产 Sun Illuminance Lux 与 Light.intensity 相乘的双参数设计。指定太阳直接把 **Light.intensity 解释为大气外 lux**；点光将 Light.intensity 解释为 **candela 或 lumen**。所有灯共用 Light.color.linear，启用色温时再乘 Unity 的线性色温 RGB，颜色不再由不同路径分别处理。RenderSettings.sun 仍是唯一平行光，其照度同时驱动直射、大气、雾与天空 IBL。

新增 [MaxPhysicalLight.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxPhysicalLight.cs) 和 [MaxPhysicalLightEditor.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Editor/MaxPhysicalLightEditor.cs)：在灯上添加 **MaxSRP / Physical Light**，或 Light 组件菜单的 **MaxSRP Photometric Controls**，可见带 lux/lm/cd 单位的强度输入。该面板编辑的是原生 Light.intensity，没有另外保存一个重复强度；点光切换单位时按 4π 转换数值并保留物理能量，支持 Undo、多选及 prefab 修改记录。组件仅存点光单位，默认 candela；没有该组件的灯也按方向光 lux、点光 candela 的明确约定读取，旧的 1/5/10 不会被自动当成任意亮度倍率。

点光 lumen 表示各向均匀发光的总光通量，`I_cd = Φ_lm / (4π)`；上传 `linearColor × I_cd / 100000`，Lighting 使用 `I/r² × N·L` 得到照度，再乘同一 BRDF 得到亮度。原有 `(1 - r²/range²)²` 有限范围淡出保留，所以接近 Range 时仍是有意的工程近似；一世界单位约定为一米。上传/读取的点光颜色改为 float4，避免低值先被 half 精度截掉；零范围返回零，光源中心使用有界方向计算以避免 normalize(0)。仍保留最多 32 个点光，按用户选择未扩展聚光灯、面光源或 cluster lighting。

### HDR 缩放接到物理曝光

所有太阳、点光和天空/IBL 的场景线性亮度都使用 `L_internal = L_cd/m² / 100000`。Color Output 新增 **Camera EV100**（默认 15）与独立 **Exposure EV** 补偿（默认 0）；实际乘数为：

`100000 / (1.2 × 2^CameraEV100) × 2^ExposureEV`

其中 100000 只用来抵消场景固定缩放，1.2 采用饱和亮度相机标定约定，来源为 [Filament 曝光说明](https://google.github.io/filament/main/filament.html#imagingpipeline/physicallybasedcamera/exposure)。EV100 增加 1 档减半，补偿增加 1 档加倍。该乘数在最后统一作用于光照、天空与雾；相机叠加仍先在线性 HDR 中合成，再输出。没有恢复天空 31.4 倍增益，也没有对白色材质或 GBuffer 单独补亮。

资产当前设置 **Camera EV100=15，Exposure EV=0**，是晴天外景起点；人工灯照明的室内可从 EV100 6～9 调整。现在是手动曝光，没有自动测光或眼适应，因此走入室内或夜间不会自动补偿。HDR Diagnostics 改为调用实际曝光函数，并输出亮度 cd/m²、EV100 和补偿，避免诊断工具继续使用旧显示换算。

### 当前天空场景迁移

[SkyboxScene.unity](D:/UnityProject/MaxSRP/Assets/Scenes/SkyboxScene.unity) 中四盏灯已添加单位组件，并写入显式数值：

| 灯 | Light.intensity | 单位 |
| --- | --- | --- |
| Directional Light | 120000 | lux，大气外 |
| Point Light 1 | 15000 | lumen |
| Point Light 2 | 7500 | lumen |
| Point Light 3 | 1500 | lumen |

点光的数值是本次场景布光起点（按原来 10/5/1 的相对强弱配置），不是实测灯具数据，也不保证与旧任意单位产生相同亮度。保留位置、颜色、Range 与阴影配置。其他旧的 PRT/Offline 测试场景未批量改写；使用实时管线查看它们时应按新约定重新设置灯光与曝光。Unity 中若当前场景仍保留旧内存状态，需要重新加载 SkyboxScene 才能使用磁盘中新的强度和单位组件。

### 验证与限制

[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 执行实际单位转换、光配置器、BRDF 和方向/点光 HLSL：1000 lux 方向光与 1 米处的 1000 cd 点光，在没有大气/场景遮挡的同一材质上得到相同结果；距离加倍贡献约四分之一。覆盖 lumen/candela 转换、色彩/色温只转换一次、上传绕过 finalColor、1000 组距离/范围、光源中心、零范围和负强度。相机测试覆盖完整 EV100 范围、补偿符号和 HDR 缩放抵消。

独立 18% 灰卡参考：100000 lux、EV100=15 时曝光后/tonemap 前约 0.14571；500 lux、EV100=8 时约 0.09325。旧的固定缩放加 -0.5 档补偿会把晴天灰卡压到约 0.04051；本次实际曝光乘数从约 0.7071 改为 2.5431。这体现了相机基准的补全，未改变太阳与天空的物理比例。测试同时核对四盏场景灯的强度/单位组件、fileID 唯一性以及资产 EV100。

Player/Editor 使用 Unity 2022.3 程序集完成运行时代码与新增 Inspector 的绑定编译；Shader 45 项独立编译和现有 HDR 输出、光照/阴影、大气能量、动态 IBL/缓存回归通过。这些仍是 CPU/API 模型与独立编译，未取得本轮真实 GPU 帧，也没有访问 Library。更新后应在 Game/Scene 查看同一灰色/白色漫反射物与金属物，分别调太阳 lux、点光 lm/cd 和相机 EV100；低功率点光在晴天阳光下贡献很弱是合理结果，确认人工灯照明时应选择合适的室内曝光。

## 动态 IBL 接入后的偶发黑帧自查（2026-10-15）

状态更新：**用户先后反馈全屏闪黑与固定在左下角的马赛克消失；随后 Lighting 出现沿砖缝/法线细节的黑斑。** 下方记录数值边界修补，以及对该补丁引入的 BRDF 硬切回归的修正；原闪黑根因仍未由问题帧证实。

用户反馈动态天空 IBL 接入后出现偶发黑色画面。通过 RenderDoc 检查 frame18483：Lighting 前的 GameView RT 确实有大面积黑色并保留部分天空；该纹理在捕获帧内直到 EID 4621 才首次被 Color Output 写入，编辑器在 EID 4658 读取显示。EID 4585 的 Lighting 写入另一张 SceneColorHDR。该帧最终重放正常，只能证明这次重放的最终结果，不能否定运行时的黑闪，也不能直接把初始纹理内容断定为上一帧故障。

随后自查动态 IBL 的数组 UAV、六面拷贝、mip 生成、双缓冲、缓存失效、资源丢失重建和消费链路。当前命令使用同一图形队列，没有异步 compute 队列；不能仅凭 GPU 工作是异步执行就认定缺少 CPU 等待或盲目添加同步回读。发现并修补以下风险：

1. **Lighting 依赖可被其他代码写入的全局 IBL 变量。** 遗留探针工具也会写 _IBLDiffuse、_IBLSpec 与 _BRDFLUT。现在 MaxIBLGIPass 把实际持有的活动输出、BRDF LUT 和 mip 数写入 MaterialPropertyBlock，随 Lighting 的 DrawProcedural 一起提交；保留旧全局绑定用于兼容，当前 deferred draw 的输入由自己持有。测试主动用无关纹理替换全局值，确认绘制绑定仍指向活动输出，更新切换后也跟随新一套纹理。
2. **异常 IBL 值能污染有效直射光。** 旧消费者直接把间接光加到总光照；NaN/Inf 可以让太阳贡献也失效，单纯少一份环境光则不应产生这种结果。新增共享 FiniteIBLColor，环境辐射转半浮点前拒绝非有限/负分量并限制到 65504；漫反射与 GGX 卷积在累加前处理异常样本；Lighting 消费漫反射和镜面 IBL 时再次隔离非有限结果。正常有限的 HDR 数据继续保留，曝光、太阳/点光强度和天空颜色均未修改；移除消费者多余的 half3 镜面中间变量。异常分量会失去相应间接光，不代表源数据错误已经被找出或自动恢复。
3. **Lighting 没有数据 LUT 已采用的 Editor 同步编译保护。** 在实际 Lighting 绘制前后加入局部 SetAsyncCompilation(false)/RestoreAsyncCompilation，防止首次编译或 Shader 重导入时使用加载占位 Shader 写入场景颜色/深度。只覆盖这一条命令缓冲区中的绘制，不修改全局 Editor 编译开关。用户更早提供的日志中有 Hidden/Internal-Loading 与大气 Shader 关键字空间不兼容记录，但它是历史证据，不能据此认定当前闪黑也由异步编译导致。

验证：[ValidateDynamicSkyIBL.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDynamicSkyIBL.ps1) 增加全局绑定被覆盖、按活动输出显式绑定和切换后的绑定更新检查，并核对 DrawProcedural 使用属性块及编译状态作用范围。[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 抽取实际 GetDiffuseIBL/GetSpecularIBL 与直射 BRDF，向漫反射/镜面/BRDF 数据注入 NaN、正负 Inf，确认有效直射仍保留；有限 HDR 和其他正常通道仍参与照明。[ValidateSkyIBLMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSkyIBLMath.ps1) 原有面向、归一化和解析卷积对照通过。Player/Editor C# 绑定及 45 项 Shader 独立编译通过，相关 HDR 输出和大气/IBL 回归通过。

代码：[MaxUpdateGIPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxUpdateGIPass.cs)、[MaxLightPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxLightPass.cs)、[IBLValidation.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/IBLValidation.hlsl)、[DynamicSkyIBL.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/DynamicSkyIBL.hlsl)、[GlobalIllumination.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/GlobalIllumination.hlsl)。

本次没有证明现场曾发生 IBL 全局变量覆盖、NaN/Inf 或加载占位 Shader；以上补丁是针对自查发现的缺口，不能把风险推断写成抓帧证实的根因。未访问 Library，未要求用户重新抓帧；需要继续以实际运行是否再闪黑检验补丁。

## 局部黑色马赛克的数值边界修补（2026-10-15）

用户提供新截图：全屏闪黑已消失，左下角仍有块状黑色异常。截图本身不能确定异常最早发生在 GBuffer、光照、雾还是输出；本轮没有取得该异常区域的逐 Pass GPU 数据，因此不将下列代码缺口断言为截图的已证实根因。

继续自查发现，上一版仅检查 IBL 纹理样本，未覆盖后续计算产生的非有限值。本轮作以下修正：

- **Fresnel 与 BRDF 边界**：直射和间接 Fresnel 的余弦均限制到 [0,1]，五次幂改用乘法，避免浮点误差导致负数底数进入 HLSL `pow`。GGX 使用等价的稳定分母形式，并保护零分母；光线与视线相反时避免归一化零半程向量。漫反射 IBL 在乘材质与 Fresnel 权重后再次检查结果，防止样本检查之后产生的异常污染有效直射。
- **空气透视合成**：对采样后的每个透射率/散射分量检查有效性。非有限透射率使用 1，非有限散射使用 0，使故障雾分量退化为中性效果，保留有效表面光照；其余通道正常合成。有限透射率限制到 [0,1]，散射限制为非负。这不能检测所有有限但错误的纹理内容，也不能替代对异常 LUT 来源的定位。
- **HDR 输出**：ACES 拟合曲线对大于 1 的输入采用代数等价的倒数形式，避免 HDR 乘曝光后因平方溢出发生 `Inf/Inf`。正无穷视作超范围高光映射为白色，NaN 与非正输入归零；普通有限 HDR 保持原曲线。没有通过降低太阳、点光、天空强度或修改曝光来掩盖问题。

代码：[BRDF.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl)、[GlobalIllumination.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/GlobalIllumination.hlsl)、[Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl)、[AerialPerspective.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl)、[ColorOutputPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl)。

验证：45 项 Shader 独立编译通过；光照测试新增余弦越界、零半程向量、GGX 峰值与乘权后异常值；空气透视测试注入 NaN/正负 Inf，确认有效表面光照与其他正常通道保留；输出测试使用实际单精度算法验证极大有限值、正负 Inf/NaN，以及 10001 个普通 HDR 输入与双精度曲线的误差不超过 3e-7。动态 IBL、天空卷积、大气缓存与光照/阴影数值回归通过。这些是独立编译与 CPU/API 模型验证，尚未确认新补丁在 Unity GPU 上消除了截图中的马赛克；未访问 Library。
## 修正上一版 BRDF 引入的法线贴图黑斑回归（2026-10-15）

用户反馈上一版后左下角马赛克不再出现，但 Lighting 输出在砖缝及凹凸细节上出现黑斑。检查发现，上一版为处理退化半程向量，在 `CookTorranceBRDF` 中新增了 `NdotL <= 0 || NdotV <= 0` 时整体返回零的判断；这里的 N 是法线贴图产生的着色法线，不能把它背向 V 等同于整个可见几何表面背向相机。

当几何地面仍然可见，且着色法线满足 `N·L > 0、N·V <= 0` 时，这条提前返回会额外删除原本的直射漫反射，产生沿法线细节突然变暗的边界。新增回归先在旧实现上失败，错误为 `Normal-map N.V horizon discards directly lit diffuse`；撤掉 `NdotV <= 0` 的整体清零后通过。保留 `NdotL <= 0` 判断及半程向量除零保护；Smith 几何项在 `NdotV <= 0` 时仍令镜面贡献为零，漫反射恢复原有计算。没有翻转法线或增加额外补光。

本轮产品代码仅修改 [BRDF.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl)。[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 增加 401 个法线跨越 `N·V=0` 的连续样本、背向视线时漫反射解析对照、相同照度方向光/点光一致性，以及纯金属背向镜面保持零的检查。45 项 Shader 独立编译与光照/阴影回归通过。

已确认并修正上述代码回归；本轮仍未取得截图黑斑像素的逐指令 GPU 证据，实际画面恢复程度待 Unity 运行验证。当前 RenderDoc 打开的捕获只有 LightCookies/UberPostProcess 等事件，没有本管线的 Lighting/大气事件，未把它当作本次截图的证明。未访问 Library。
## 天空 IBL 与 Blender 的实际辐照度对照（2026-10-19）

用户反馈直射亮度接近 Blender，要求比较两边天空辐照度。本轮在 Blender 只保留原 World，使用六向、反射率 1 的 Lambert 卡进行 Cycles 测量；Unity 通过新增 Editor 一次性读回工具，导出运行中管线持有的天空 LUT、环境 Cube 与活动 diffuse Cube。比较均排除太阳盘，在线性 HDR、材质权重与曝光之前完成，并将 E/π 还原为 E。

朝上方向：Blender 6207.94 lux；Unity GPU 天空 LUT 6209.23 lux；环境 Cube 6206.66 lux；diffuse Cube 6233.46 lux。四个水平方向的 diffuse 误差也均小于 1%。当前太阳、大气和环境参考高度匹配；读回数据没有 NaN/Inf，活动 diffuse 与全局绑定一致。这批样本未显示天空源或 diffuse 卷积整体丢失能量，不能据此提高天空强度。

发现两个层面应分开记录：一是朝下源天空只有约 1.8 lux，而 diffuse Cube 约 99 lux，存在 MIP/分辨率引起的地平线漏光；二是在头盔位置排除头盔自身后，Blender 朝下测量面只收天空时为 1.84 lux，加入原场景几何与灯光后为 36726 lux，主要来自受光地面的反弹。当前 Unity 的天空 IBL 缺少这样的地面/场景辐射输入，能够解释部分背光面及朝向地面的反射偏暗；尚不能断定所有暗区的根因，也尚未验证间接光消费者的材质权重与镜面链路。

[复测流程与六向结果](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/README.md)、[原始测量报告](D:/UnityProject/MaxSRP/OfflineBaseline/SkyboxScene/irradiance_comparison/irradiance_comparison.json)。本轮没有调整光照强度、曝光、大气公式或运行时 IBL 算法；新增诊断器通过 Unity 2022.3 Editor/Player API 绑定检查，并已在运行中的 Unity 成功完成一次 GPU 读回。未访问 Library。

## 已修正：直射 Smith-GGX 与头盔 baseline 复测（2026-10-19）

在独立 Unity 预览场景只保留头盔和太阳，关闭点光/雾，分别读回实际 GPU 的曝光前直射、IBL、合成及 GBuffer；Cycles 采用同一相机、贴图和源天空，禁止表面多次反弹。原 Unity 场景和材质未修改。相机矩阵误差小于 4.1e-8，50% coverage 轮廓 IoU 99.994%，法线夹角中位数 0.429°。

直射 BRDF 的几何项已从带 roughness 重映射的 Schlick 近似改为**高度相关 Smith-GGX**，合并计算 `G2/(4*NdotV*NdotL)`，移除人为加在镜面分母上的 `+0.001`。GGX NDF、Fresnel、IBL、太阳/点光强度、曝光及材质保持原值；方向光与点光共用新 BRDF。背向着色法线仍仅让镜面为零，保留有效漫反射，避免先前的整块黑斑回归。

重新执行实际 Unity GPU 六项读回及 Cycles 渲染，统一使用 0° Sun；旧的 2° 太阳结果仅保留为参考，不参与当前通过判定。只保留头盔，排除场景 GI，亮度均在曝光前公共内区统计：

| 项目 | 修复前 Unity/Cycles | 修复后 Unity/Cycles |
| --- | ---: | ---: |
| 无阴影直射 | 0.79812 | 0.93438 |
| 有自身阴影直射 | 0.80027 | 0.93867 |
| 合成（自身阴影直射 + 无遮挡 IBL） | 0.81834 | 0.94445 |
| 无遮挡天空 IBL | 1.00323 | 1.00323 |

**±10% 平均亮度筛查通过，非逐像素一致验证。** 新版直射平均值比旧版增加约 17.07%，IBL 不变。无阴影直射的像素亮度比中位数仍约 0.805，相对 L1 误差约 0.829，合成相对 L1 约 0.786；局部高光差异没有随平均值改善而消失。法线/纹理过滤、高光采样与不同 Fresnel/材质分层仍需单独研究，不能把所有剩余差异归给 GI，也没有采用调灯光或曝光的补偿。

验证：从生产 HLSL 抽取可见性/BRDF，180 组参数对照独立双精度 lambda 定义，检查光线/视线互易性、掠射/背面有限值及 401 组法线地平线样本的漫反射下界；实际 BRDF 与独立 Cycles 白金属平面参考匹配。45 项 Shader 独立编译、Unity 2022.3 Editor/Player API 绑定、物理光照、光照/阴影及动态 IBL 回归通过。物理灯光验证器移除对用户可调太阳必须等于 120000 的旧断言，继续验证有效正 lux 及单位迁移；未改动场景灯光。

GI 仍按用户范围暂缓，朝下环境过滤漏光仍是独立未修问题。[新旧/Cycles 三列对照](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/helmet_before_after.png)、[数值报告](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/comparison.json)、[完整方法与历史调查](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/README.md)。旧 GPU 结果归档在 `OfflineBaseline/HelmetOnly/BeforeSmithGGX/`。

### 局部高光调查：主要是法线/粗糙度采样，另有非金属模型差异

将实际 Unity GBuffer 逐像素投射到 Cycles，控制同一 0° Sun、无阴影/雾/场景反弹，分别统一输入和材质模型。以 Unity 亮度总和归一化的局部 L1：原生 Cycles 输入 **88.77%**；只统一法线 **32.40%**；再统一粗糙度 **5.13%**；全部共享输入、Principled 单次 GGX **3.54%**；进一步使用与工程一致的 GGX + Schlick Fresnel + Lambert 诊断材质 **0.807%**。该指标不是平均亮度损失，各项也不可相加。原 baseline 不被诊断材质覆盖。

实际 GPU 变体确认法线导入为 **DXT5**：同为 MIP0/浮点 normal GBuffer，压缩导入与未压缩源图的 XY 重建法线中位角差 **0.449°**；未压缩路径对 Cycles 原生窄过滤法线中位差仅 **0.0109°**。normal GBuffer 的 10 位量化自身中位角差约 **0.0431°**，改变局部亮度但不能解释全部误差。两边无贴图几何法线中位差 **0.00191°**。MIP 与像素过滤也改变高光，但只缩窄 Cycles 像素过滤反而增大局部 L1，不能把禁用过滤作为修复。

共享输入的纯金属对照中，工程公式与 Cycles 的 L1 约 **0.381%**，支持当前 Smith-GGX 核心一致；纯非金属用 Schlick Fresnel 时约 **12.53%**，替换精确介电 Fresnel 后降至 **2.98%**，剩余还涉及 Principled 的漫反射分层权重。这些为离线公式/诊断材质对照，不是新的运行时修改。

本轮修复了诊断工具克隆不可读纹理的错误：改用同格式 GPU 拷贝，未开启 Read/Write。临时法线精度与贴图变体仅作用于预览场景，结束后恢复材质/资源；实际 Unity 导出与 Cycles 测试均完成。生产 BRDF、贴图导入设置、光强/曝光及 GI 均未在本轮改动。下一步优先评估法线压缩和粗糙度采样策略；材质模型剩余差异保留，朝下环境漏光也仍独立待修。

[高光与放大区域对照图](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/HighlightDiagnosis/highlight_comparison.png)、[完整调查报告](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/HighlightDiagnosis/highlight_report.json)、[复现说明](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/README.md)。

### 后续已应用：BC5 法线 / BC7 粗糙度；完整 baseline 仍未闭环

本节为当前状态，上述 Smith 阶段均值“通过”是纹理调整前的结果。已对头盔法线 `04#GLB.jpg` 与共享的 `Cerberus_R.tga` 粗糙度设置 Standalone BC5/BC7、质量 100，保留 2048²、MIP、色彩空间、Read/Write=false 和材质绑定。法线压缩数据量不变；粗糙度含 MIP 约从 2.67 增至 5.33 MiB。源粗糙度仍为 sRGB、头盔仍引用枪械 M/R，作为已有材质问题另行处理；未在本次混改。

同源同分辨率 GPU 对照中，2048² 法线 MIP0 的局部亮度 L1 从 DXT5 的 **47.49%** 降到 BC5 的 **6.84%**；2048² 粗糙度正常 MIP 从 DXT1 的 **6.20%** 降到 BC7 的 **0.81%**。没有关闭 MIP，也没有把实验的全浮点 normal GBuffer 应用到生产。

调查还确认：R/M 源图是 **4096²**，Unity 实际导入 **2048²**，而原生 Blender baseline 使用 **4096²**。因此源采样差异还包含导入缩放和像素足迹，不可全写成压缩误差。矩阵已补同为 2048² 的 codec 对照；4096² BC7 候选未落到正式设置。

正式重新导入后取得实际 GPU 数据：无阴影直射相对原生 Cycles 的局部 L1 从 **0.82943 → 0.56201**，合成 **0.78568 → 0.53306**，但直射平均亮度比 **0.93438 → 0.83204**，合成 **0.94445 → 0.84815**，当前 ±10% 均值门槛不通过。IBL 均值比约 **1.00211**。这是“压缩精度改善、局部分布改善，但整体均值未对齐”，不能宣布 baseline 已通过或通过提高光强补偿。

剩余工作优先统一离线纹理尺寸与像素足迹，检查采样收敛，再单独处理介电 Fresnel/分层模型差异。GI、曝光、光强及运行时 BRDF 均未在本轮修改，朝下环境漏光仍独立待修。旧正式捕获保存在 `BeforeSamplingQuality/`，旧共享输入实验另冻结到 `HighlightDiagnosis/SourceUnityInputs/`。

[正式纹理质量调整前后对照](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/sampling_quality_before_after.png)、[当前 baseline 数值](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/comparison.json)、[同分辨率压缩报告](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/HighlightDiagnosis/sampling_report.json)。

### 直接公式对照：排除模型自身反射（2026-10-19）

改为对照本机 Blender 5.2.2 LTS 构建 `d13f752e3b9c` 的 Cycles 源码与相同输入的无贴图平面。生产 HLSL 抽取为单精度计算，10000 组参数对独立双精度 GGX 方程，D/合并 Smith 可见性最大相对误差约 **2.03e-5 / 1.96e-7**。42 组平面各渲染 GGX/MULTI_GGX，黑 World、单位 delta Sun、无阴影、无二次射线可见性；纯金属单次 GGX 响应最大相对误差 **6.39e-7**。统一 Cycles eval 内含余弦与工程外乘 NdotL 的约定后，没有遗漏余弦或 pi 因子。

仍确认三类材质差异：工程非金属 Schlick 在 60° 的反射率 **0.07**，Cycles 精确介电 Fresnel **0.089187**；工程先混合 F0 再衰减 diffuse，而 Cycles 分配金属/介电 closure 后做下层分层，正面、baseColor=0.8、roughness=0.7、metallic=0.5 时工程响应比 Cycles 单次 GGX 低 **18.46%**；Cycles MULTI_GGX 另有微表面多重散射补偿，同样粗糙度的正面纯金属响应比其单次 GGX 高 **32.53%**。这些均为特定方向的受控案例，不是头盔整图或全半球能量差。

完整光追渲染可能包含模型自身的间接反射，但之前头盔 delta 直射与共享输入诊断已关闭 secondary diffuse/glossy 可见性，本轮单平面也没有其他几何，因此上述公式差异与场景 GI 无关。微表面内部多重散射也需与模型部位之间的反射区分。当前证据不能把平面百分比直接套用为完整头盔约 16.8% 均值差的解释。

本轮只新增对照工具、结果和文档；运行时 BRDF、IBL、光强、曝光和 GI 未修改。公式、复测命令与精确版本源码链接见 [直接公式对照报告](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/FORMULA_COMPARISON.md)，完整数值见 [formula_comparison.json](D:/UnityProject/MaxSRP/OfflineBaseline/FormulaComparison/formula_comparison.json)。现有物理光照回归与新增 D/G、纯金属、Cycles 端点混合断言均通过。

### 当前 16.8% 直射差异的受控闭合：主要来自输入与采样

使用当前 BC5/BC7 的真实 GPU 捕获，独立冻结到 `MatchedInputsBC5BC7/SourceUnityInputs/`；没有混用旧 DXT 共享输入。相同相机/太阳、无 GI/阴影/雾，在原 baseline 相同的 137913 个内部像素统计。新渲染原生 Cycles 的均值与原参考比约 **1.000000011**，复现原 **Unity/Cycles=0.832042**。

把实际 GBuffer 的世界法线、有效粗糙度、金属度与线性颜色通过逐像素 Closest 投射给 Cycles，验证投射误差在约 1e-6 量级，并用窄过滤匹配 Unity 的单次像素采样。统一 N/R 后，Unity/Cycles 从 **0.832042 → 0.983579**；全部输入共享、保留 Cycles 默认 MULTI_GGX 时为 **0.975094**（仍低约 2.49%）；全部输入与 BRDF 一致、2048 samples 时为 **0.998897**（低约 0.11%）。局部 L1 以 Unity 为分母，从原生 **67.55% → 0.283%**；旧 56.20% 使用 Cycles 分母，不能直接与此数值相减。

N/R 完整 2×2 对照、对两种替换顺序取平均：法线输入影响相当于原 Cycles 均值的 **7.149 个百分点**，粗糙度 **10.110 个百分点**，其他项有正负抵消。这是本捕获的输入效应，不是普遍能量损失占比。已知路径仍包含 R/M 原图 4096² 对 Unity 导入 2048²、压缩与两引擎 MIP/过滤差异；本次不能把全部输入效应归给某一个 codec 或缩放步骤。只缩窄原生 Cycles 过滤会把差距扩大到约 18.39%，所以没有将禁用抗锯齿作为生产修复。

共享输入后 MULTI_GGX 相对单次 GGX 仅高 **0.0847%**。同款诊断 BRDF 只换精确介电镜面 Fresnel，均值增加约 **0.783%**；剩余还涉及下层 diffuse 的金属混合与 Principled 分层。512 → 2048 samples，共享输入均值变化约 **0.0084%**，说明上述收敛结果不是约 16.8% 的采样噪声。NumPy 独立像素中心 BRDF 副本对 GPU 均值差约 **0.115%**；最终约 0.11% 残差与个别极窄高光尚未逐像素穷尽，不宣称完全数学等价。

**当前结论：原始 16.8% 大头来自法线/粗糙度输入与采样不一致；没有发现实际 Lighting Pass 另有固定 16.8% 的直射缩放。** 这是诊断闭合，不是原生图像已经自动修复。本轮未修改运行时、导入器、原场景、光强、曝光或 GI。新增分析器验证捕获哈希、投射精度、原参考复现、贡献加和与采样收敛，均通过。

[详细拆解与复测](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/MATCHED_INPUTS.md)、[当前捕获的四列对照图](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7/matched_inputs_comparison.png)、[完整数值](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7/matched_inputs_report.json)。旧报告中的“完整 baseline 未闭环”仍描述原生输入对比，本节补齐了相同输入下的受控验证。

### 原生 UV baseline 已补实际导入数据与共同采样约定

已从 Unity GPU 导出 Albedo DXT1、Normal BC5、Roughness BC7、Metallic DXT1 的实际解码数据，均为 2048²、12 级 MIP，共 48 个线性 RGBA EXR，包含当前 sRGB 解码结果。未开启资源 Read/Write，未重建或修改 importer。单独保存新 Blender 场景 `ImportedTextures/HelmetOnly_ImportedTextures_Cycles.blend`，使用原生 UV/切线法线计算，采样 XY 后重建法线 Z，并模拟 GBuffer 量化；没有 Window 投射输入，纹理全部打包。

共同约定为 **MIP0、双线性、aniso=0**。Unity 只在临时预览材质上应用，生产自动 MIP 保持；导出全部 MIP 不代表已在 Cycles 复制 Unity 的隐式 LOD。原场景、灯光、曝光、运行时 BRDF、IBL 与 GI 均未修改。

在相同 137913 个内部像素上，Unity MIP0 直射均值 **0.047084733**。Cycles 原生 UV、默认 Principled 的中心子像素检查均值 **0.048578664**，Cycles/Unity **1.031729**；同款 GGX/Schlick/Lambert **0.047568610**，比值 **1.010277**，局部 L1（Unity 分母）约 **1.792%**。没有将该原生路径残差混作此前共享 GBuffer 的 0.11%。原生输入的有效 R/M MAE 约 **0.000109 / 0.000165**，法线夹角中位数 **0.000723°**。768² 窄过滤的默认 Principled 比值为 **1.035155**；中心子像素检查用于减少采样足迹，不是生产抗锯齿修改。

生产自动 MIP 直射均值仍约 **0.038951826**，比临时 MIP0 对照低 **17.273%**，进一步确认采样路径会显著改变本模型窄高光的均值。此项不构成禁用生产 MIP 的建议，也未将原生 4K 混比的旧 16.8% 报告改名为通过。

新 `.blend` 已重新从磁盘打开验证：四个材质纹理节点均为打包的 2048² 图，原生 UV、Linear 插值、默认 Principled MULTI_GGX，无二次模型可见性/场景 GI；F12 曝光/ACES 最大误差 **1.79e-7**，实际 768² F12 渲染完成。新 Editor compute 独立编译、Editor/Player C# API 绑定及实际 GPU 导出均通过。后续生产采样策略应另行研究法线/粗糙度预过滤和高光稳定性，不用调高光强补偿。

[新 Blender 场景](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/ImportedTextures/HelmetOnly_ImportedTextures_Cycles.blend)、[原生 UV 三列对照](D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/ImportedTextures/native_imported_comparison.png)、[完整范围与复现](D:/UnityProject/MaxSRP/Tools/BlenderBaseline/IMPORTED_TEXTURE_BASELINE.md)。

## 已修复（2026-10-20，19：Lighting Shader 构建引用）

原问题：Lighting 仅通过 `Shader.Find("MaxSRP/PBRLitPass")` 创建材质，缺少序列化资源依赖，存在编辑器正常而 Player 构建剥离 Shader 的风险。此前未实际复现 Player 黑屏。

`MaxRendererPipelineAsset` 已新增序列化 `LightingShader`，现有管线资产及脚本导入器的新建资产默认引用均指向 `PBRLitPass.shader`。管线将该引用传给 `MaxLightPass`，不再依赖名称查找；初始化检查 null/isSupported，材质继续按管线生命周期释放。

新增 `MaxLightingShaderBuildValidator`，构建前检查 Graphics 默认管线及全部 Quality 管线覆盖，去重后验证 Shader 为持久化的 `MaxSRP/PBRLitPass` 资产；缺失或错配时中止构建并报告资产路径。构建回调不使用 Editor GPU 的 isSupported 判断目标平台支持，设备支持仍由运行时检查。

验证：`Tools/ValidateLightingShaderRetention.ps1` 检查实际 Shader GUID、现有全部 MaxSRP 管线资产和新建资产默认引用，并将实际 Runtime 源码及新增构建回调分别按 Player/Editor 条件绑定本机 Unity 2022.3 API 编译，均通过。仅未改动的 SRP Core `CommandBufferPool/CoreUtils` 使用声明替身，未访问 Library；该检查不是完整 Unity 导入或 Player 构建。

修复 19 时 MCP 返回 0 个编辑器实例，Build Settings 场景列表为空；未执行真实 Player 构建及 GPU 运行验收，发布前仍需补这一项。问题 19 的资源引用缺口已修复并从待处理列表移出；随后按用户指定修复 29，见下一节。

## 已修复（2026-10-20，29：方向光 PCSS 角半径与世界距离）

原公式使用到阴影近平面的距离比 `(receiver-blocker)/blocker`，使方向光半影依赖任意的阴影相机原点；保持 receiver/blocker 相距 10 m，仅将距离从 `(20,10)` 改为 `(120,110)`，旧 UV 半径由 0.1 变成 0.00909091，相差 11 倍。有限面光源、以光源为中心的透视 PCSS 距离比不能直接套用当前跟随相机的正交近平面。参考 [NVIDIA PCSS 示例](https://developer.download.nvidia.com/SDK/10.5/direct3d/Source/PercentageCloserSoftShadows/doc/PercentageCloserSoftShadows.pdf)。

现在使用方向光近似 `rWorld = min(max(receiver-blocker,0) * tan(solarAngularRadius), maxPenumbraRadius)`，再除以当前级联正交宽度转为 UV。太阳角半径每次阴影渲染从天空 `_SunDiskAngle`（度，当前 1°）更新；阴影侧将输入限制在 0.001°～89°以保持 tan 有限。Depth bias 仅参与遮挡比较，不再缩减用于半影估计的接收点几何距离。

原 `PcssSearchRadius` 以 `FormerlySerializedAs` 迁移为 `MaxPenumbraRadius`，明确为世界空间搜索/半影半径上限；现有资产数值保持 0.75/0.75/1/1 m。该参数是有限采样的质量限制，超过上限会截断物理半影，不能宣称任意距离精确面积光阴影。旧 `PcssFilterRadius` 倍率及未使用的 `_LightSize` 已移除。搜索核按完整 caster 深度范围的最大可能半影推导，再应用同一上限；过滤核不会超过搜索核。搜索总次数仍为 64，其中一次固定中心射线以保留薄遮挡物的中心样本；无 blocker 保持全亮，上限为 0 时退化为单次深度比较。

各级联的投影 XY 包围盒增加最大世界过滤半径和法线偏移余量，再执行 texel snapping，避免合法接收点在过滤时越出贴图。保持原有阴影分辨率、四分之一宽高的屏幕阴影、蓝噪声、两遍高斯模糊和前两级 PCSS/后两级硬阴影选择；30、31、32 未顺带修复。

验证：提取实际函数后，1°、10 m 间距、100 m 正交宽度、0.75 m 上限，两种近平面原点下 UV 半径均为 **0.0017455065**，世界半径约 **0.17455065 m**。`ValidateLightingShadowMath` 在四种深度/法线分支各 10000 组中验证原点平移不变、级联世界尺度一致、搜索覆盖过滤、接触收紧及零角/零上限边界；`ValidateSceneShadows` 通过含过滤边界的 1536 组光空间覆盖检查；`ValidateShadowCameraIsolation` 验证太阳角编辑后实时上传；`ReviewShadowContracts` 仍独立报告未修的 30/33 反例。48 项 HLSL 编译和 Runtime Player/Editor 条件的 Unity API 绑定检查均通过。

修复 29 时 MCP 返回 0 个编辑器实例，未取得实际 GPU 图像或 Unity Editor 完整导入结果。以上是当时源码数值、API 及 HLSL 编译验证；10 月 21 日的直采样变更见下一节。


## 已修复／路径替换（2026-10-21，30、31：Lighting 直接评估四级 PCSS）

按用户选择保留 PCSS，暂不做 shadow cache。现在 Lighting 用当前 GBuffer 的世界位置与法线直接查询 CSM，删除屏幕阴影 Pass、三张 1/4 宽高的 R32 阴影 RT、两次高斯模糊、相关 Shader/HLSL 和管线资产引用。30 的跨接收面模糊污染随这条路径的移除关闭，未用新模糊掩盖。GBuffer、HDR、曝光、大气和 IBL 处理保持原有规则。

四级统一采用问题 29 的角半径 PCSS。搜索/过滤可独立选择 **16、32、64** 次，当前资产默认 **16+16**；每档拥有覆盖完整圆盘的采样分布，不截取旧表前缀。搜索使用独立 point-clamp 原始深度采样，过滤使用 bilinear comparison sampler（硬件 PCF）；点数指采样指令数，比较采样内部可涉及多 texel。中心 blocker 样本、无 blocker 提前返回全亮、零半径退化单次比较采样、静态蓝噪声旋转均保留，未加入时间累积。低采样有颗粒风险，可分别提高搜索/过滤档位；不宣称当前档位已满足所有移动 GPU。

31 的三个边界各采用相邻较小深度区间 **10%** 的过渡带，可调 0～30%。CPU 把下一级的近端投影向前扩展到过渡起点，世界过滤核和法线偏移还另有 XY/Z 余量；Shader 只在带内评估相邻两级并平滑混合。四级不再切换软硬算法。最后一级区间的末 **10%** 平滑淡出到全亮，可调 0～50%。默认 near=.3、far=500、ShadowDistance=500 时，级联边界仍约 35.279/100.24/225.165/500 m，混合宽度约 3.4979/6.4961/12.4925 m，末端淡出从约 472.5165 m 开始。带内最多两次 PCSS 评估，属于额外成本。

相机当前视图矩阵、像素尺寸及本次 ShadowCaster 是否成功绘制，通过 Lighting 的 MaterialPropertyBlock 绑定；无太阳、None、裁剪失败或无有效阴影区间时返回全亮，不读取上一相机留下的阴影。仅太阳直射项乘阴影，点光、IBL 和天空辐射量未改。问题 32 的 shadowStrength/Hard/Soft 灯光控制仍待单独处理；33 的多视图资源回收问题也未顺带修改。

验证：`ValidateDirectPcss` 提取实际 PCSS、blocker 搜索和点分布，以 point-depth/bilinear-comparison API 夹具执行普通/反向 Z 各 9 组搜索×过滤预算，验证全亮、全遮挡、半影、接触收紧、原点平移、无 blocker 提前退出及完整圆盘覆盖。`ValidateSceneShadows` 提取真实 CPU 级联更新与投影方法，覆盖 384 组视锥角点、1536 组基本投影覆盖、3456 组相邻级联混合区/核覆盖、24 组相机深度重建；标量测试验证平滑混合与距离淡出端点。既有相机隔离、视窗、HDR 输出及物理光照检查通过，40 项 HLSL 编译及 Runtime Player/Editor 条件 API 编译通过。测试脚本已移除旧屏幕 Pass 断言。

以上没有替代真实 GPU 验收：本轮 MCP 返回 0 个实例，未得到 Unity 导入、Scene/Game 画面或目标手机计时。全分辨率直接 PCSS 的总成本受阴影覆盖率、提前退出和混合区影响；删除模糊不等于一定更快。后续需检查贴地/悬空物体、细杆/斜面、级联穿越、无太阳及多视图，并在真机比较 16/32/64 档。

## 已修复（2026-10-21，大气 LUT 被 Destroy 后的 MissingReferenceException）

用户报告 `MaxProceduralSkyboxPass.Update` 原 172/175 行在调用 `RenderTexture.IsCreated()` 时抛出 MissingReferenceException。旧代码只覆盖 RenderTexture 原生对象仍存活、GPU 存储被 Release 的情况，没有先检查 Unity fake-null；原生对象已被 Destroy、托管包装仍留在字段中时会持续访问失效对象。现有证据定位了恢复缺口，尚未确定最初由哪次 Editor 清理或外部操作销毁了对象。

新增 `EnsureLut`：先用 Unity 对象空判断识别已销毁包装并新建纹理；原生对象仍在但 GPU 存储丢失时重新 Create。透射率恢复会重烘焙多次散射；多次散射恢复会推进介质版本，按依赖重烘焙天空、雾和环境源，再触发动态 IBL 更新。资源正常时继续命中缓存。所有本 Pass 拥有的 LUT 与材质增加 HideAndDontSave，明确由 Pass.Dispose 回收并防止作为未使用临时资源自动卸载，未禁用资源释放或改成逐帧烘焙。

回归夹具新增 Unity fake-null 与销毁后 IsCreated/Create 抛错语义，修改前能重现同类异常，修改后通过。覆盖透射率、多次散射和环境源的 native-destroy、Release 丢失、恢复时分配失败及重试、依赖版本更新、多相机复用、100 个静止帧与最终清理；Runtime Player/Editor 条件 API 编译通过。

本次 Unity MCP 已重新连接 MaxSRP。已直接刷新并确认新 EnsureLut 编译进入活动管线，在实际 Direct3D11 Editor 中分别 DestroyImmediate 当前透射率 LUT（旧 ID -3032）及多次散射 LUT（旧 ID -3034），由正常 Scene 重绘自动恢复为新对象（-3106/-3110）。两张基础 LUT 与环境 LUT 均 IsCreated=true，EnvironmentRevision 由 1→2→3，动态 IBL 的已使用环境版本同步为 3；复查控制台返回 0 条 error，未清空控制台。测试只销毁可再生的运行时缓存，不修改场景、材质资产或大气参数。这验证了实际资源恢复，不等于已完成移动端性能或全部 PCSS 视觉验收。

## 已修复／待观感验收（2026-10-21，旋转视角时的阴影爬动）

用户反馈整条阴影边缘在视角旋转时爬动。此前替换路径移除了低分辨率屏幕阴影及两遍高斯模糊，并将 PCSS 默认从 64+64 降为 16+16，缺少投影稳定化，构成实际的视觉回退；原有滤波提供的稳定性收益不能仅归为“掩盖旧问题”。底层紧光空间 AABB 在旋转时伸缩、中心对齐的 texel 步长也随之变化。实际 Scene 相机视锥的纯旋转诊断中，第一级宽度从 0° 的 74.8212 m 变为 1° 的 75.4845 m、5° 的 77.9112 m。此诊断未旋转用户相机或修改场景。

改为 Stable CSM：在相机空间求包含每级完整重叠视锥的包围球，半径由投影和分段决定；仅将球心转换到世界空间，再按固定尺寸在光空间对齐中心。XY 过滤核/法线偏移余量和深度余量保留，四级 PCSS、级联过渡和距离淡出保留。纯刚体旋转/平移不会改变投影宽度；FOV、横纵比、分段和阴影距离变化仍会重新计算覆盖。相较紧 AABB 会降低部分贴图利用率，不额外增加贴图分辨率。

同时把 PCSS 蓝噪声旋转从屏幕像素改为光源/世界坐标锚定，尺度采用当前相机第一级的固定世界 texel 尺寸；固定世界点不会在相机旋转时换随机方向。移除不再需要的 receiver screen-size 输入。采样档位仍为 16+16，没有恢复高斯模糊、增加时间累积或引入 shadow cache。

验证：CPU 提取真实级联/包围球/配置函数，384 组旋转和平移测试确认投影宽度相同、固定世界点只有整数 texel 位移；既有 1536 组基本覆盖、3456 组相邻级联/过滤核覆盖及正交、偏心投影、负 near 测试通过。PCSS 算术、相机隔离、40 项 HLSL 和 Editor/Player 条件 API 编译通过。

通过 MCP 在实际 Unity 临时预览场景中复制当前 Scene 相机的投影，并分别旋转 0/.1/1/5/45/90°，调用真实 Cascade.UpdateCascade/ConfigCameraToShadowSpace 与 Unity GPU 投影。四级宽度各自恒定为约 92.06728 / 248.93265 / 551.8214 / 1223.283 m；固定世界点跨姿态的最大非整数 texel 偏差约 **0.00001526 texel**，噪声世界步长恒定约 **0.08990946 m**。临时场景已关闭，源相机及场景不变，正常重绘恢复当前全局参数。MCP 刷新后控制台返回 0 条 error。该检查确认实际矩阵/网格稳定，不替代逐帧画面观感及目标手机耗时验收。

## 已调整／待观感验收（2026-10-21，PCSS 边缘指纹状纹路）

用户确认旋转时阴影不再抖动，但边缘出现指纹/等高线状 pattern。MCP 检查实际蓝噪声纹理为 512²、DXT1、Bilinear、sRGB、10 个 MIP；世界空间连续 UV 经双线性插值后成为相关的旋转角度，低采样 PCSS 可能把这些角度变化显现为结构纹路。压缩和 sRGB 也改变了随机数分布。尚未通过同视角图像对照确认各因素的独立贡献。

将该纹理改为 Point、Repeat、线性、无压缩、关闭 MIP；Shadow.hlsl 使用显式 `sampler_PointRepeat` 和 LOD0，避免替换纹理的过滤设置再次插值随机角度。世界空间锚定、噪声尺度、Stable CSM、阴影分辨率及 16+16 PCSS 预算均保持不变，便于观察本轮修正的效果。无压缩 RGB24 的该纹理约占 768 KiB，不含额外 MIP；未改其他光照或大气资源。

通过 MCP 完成重导入、刷新和 Scene 重绘，实际纹理已确认为 RGB24/Point/sRGB=false/1 MIP。40 项 HLSL 编译及普通/反向 Z 的 PCSS 算术检查通过；实际 Lighting Shader supported=true、无编译 error，但 ShaderUtil 仍报告若干 potentially-uninitialized 警告，未在本轮扩大修改范围。控制台返回 0 条 error，未清空控制台。本轮没有保存同视角前后图像，尚不能宣称图中 pattern 已完全消失；后续根据实际观感决定是否调整噪声密度或采样预算。

## 已修复／待最终画面验收（2026-10-21，PCSS 噪声绑定、类型与尺度）

用户随后反馈 pattern 仍存在但变为块状，因此上一节的纹理导入修正没有解决完整问题。本轮用 Editor 专用 `MaxPcssPatternProbe.shader` 复用真实 PCSS，在同一帧、同一 Scene 相机/GBuffer/ShadowMap 上输出六组可见度遮罩，不修改原相机或场景。发现三个因素：

- 噪声只在 ShadowCaster 构造时绑定；上次重导入后实际 `Shader.GetGlobalTexture("_NoiseTexture")` 为 null，运行时没有逐相机恢复绑定。固定圆盘的低采样结构因此直接显现。
- `_NoiseTextureResolution` 在 HLSL 中为 float，CPU 却使用 `SetGlobalInteger`；实际浮点全局值为 0、整数全局值为 512。随机方向的坐标缩放没有获得正确的浮点分辨率。
- 显式恢复纹理绑定和正确浮点参数后，以一个第一级 shadow texel 对应一个随机格子的设计仍然过粗；当前视角该设计值约 0.08990946 m。Point 采样使每个大格子使用同一旋转角，产生新的块状 pattern。上一轮口头描述的“每格约 9 厘米”是设计尺度，不能作为当时缺失绑定/错误参数状态下的实际 GPU 尺度。

现在 `CascadeSettings.Set()` 随每次有效相机级联更新重新绑定当前噪声纹理，并以匹配 HLSL float4 的 SetGlobalVector 上传新参数 `_ShadowNoiseTextureSize`（宽、高）；缺失纹理使用灰色常量纹理兜底，也支持非正方形替换纹理。构造函数不再独占这些输入的初始化。最初直接将旧同名整数槽改为 float，在热重载中留下两条 property-sheet 类型冲突日志，已改用新向量参数名避免 Editor 保留旧全局槽的冲突；没有要求重启编辑器，也未清空控制台。无消费者的旧 `_ShadowMapResolution` 声明及上传一并移除。将世界随机格子细分为原来的 1/16（当前约 **0.005619341 m**），保留光源/世界空间锚定、Stable CSM 及 **16+16** 采样预算。Point/线性/无压缩/LOD0 规则继续保留，没有恢复屏幕模糊或加入时间累积。

同帧 GPU 对照可见：正确绑定但使用粗格子的 `bound16.png` 仍有明显块状；`fine16.png` 中的大块结构被打散为细颗粒；64+64 的 fine64 更平滑，但未应用到默认预算。修正后再次 ForceUpdate 重导入噪声，经正常 Scene 重绘仍确认绑定有效。最终向量参数复测尺寸为 (512,512)，世界格子约 5.62 mm，采样数 16+16。同帧遮罩 `current16.png` 与显式指定 fine16 的图片 SHA256 完全一致（D677E20C42AF87E2F9351FB1007B21378FA939CCA9C1204FA3BAF33CF997F225），确认实际运行时输入与对照使用的修正输入相同。

输出位于 `OfflineBaseline/ShadowPatterns/`（已被 gitignore 排除）；`before_fix16.png` 保留本轮修复前结果。复现：把 `Tools/UnityMcp/CapturePcssPatterns.cs.txt` 内容作为 Unity MCP execute_code 的 code 参数，等待下一次 Scene 重绘及 Editor update 导出。探针仅评估阴影可见度，未对最终曝光、材质或天空进行重新标定。最终源码的 40 项 HLSL 编译、既有级联覆盖/旋转稳定性、Runtime Player/Editor 条件 API 编译检查通过；最终刷新后 MCP 查询返回 0 条 error，未主动清空控制台。静态遮罩验证不等于所有距离、掠射面及相机运动下都无噪点；16 次过滤仍有可见采样颗粒，等待用户最终画面与运动观感验收。

## 已实现／待画面验收（2026-10-21，PCSS 低差异采样与受约束邻域复用）

上一版仍有明显颗粒，用户要求参照 UE。已直接读取本机 UE **5.3.2** 的 `ShadowPercentageCloserFiltering.ush`、`ShadowProjectionPixelShader.usf`、`PixelQuadMessagePassing.ush` 及 `ShadowRendering.cpp`。该版本传统 Shadow Map 默认 Uniform PCF，PCSS 标为 experimental；PCSS 的搜索/过滤预算为 16+32，使用屏幕像素/帧序号 Sobol 采样，带有条件的 2×2 搜索统计/过滤共享、接收面逐样本 bias 和遮挡统计驱动的椭圆核。它并非只旋转少量固定圆盘点就输出；VSM/SMRT 另属一条实现。以下是适配当前管线的空间重建子集，没有宣称完整复刻 UE。

默认档改为 **16 次 blocker 搜索＋32 次过滤**，保留 16/32/64 可选档。运行时圆盘改为带数字移位的二维 Sobol 网格，经同心方形到圆盘映射采样；两组世界锚定的蓝噪声数值分别移位两维，避免只旋转固定点表。未引入随帧变化的种子，因此不依赖尚不存在的历史累积。旧 Vogel 点表只在 Editor 对照宏启用时编译，不进入生产默认分支。当前方向光角半径 PCSS、四级投影和过渡均保留。

增加 Lighting 内的 2×2 可见度复用，不分配阴影遮罩 RT 或增加高斯模糊 Pass。只向通过有效性、法线相似性、双向接收平面距离及相同级联/混合带状态检查的邻居借用结果；当屏幕像素在光空间的足迹接近/超过 shadow texel 时减弱/禁用复用。强度上限 0.75，保留中心像素贡献。它复用最终可见度，没有共享 blocker 搜索统计，也没有移植 UE 的椭圆核或逐样本 receiver-plane bias。没有 primitive ID，不能保证区分所有极近且共面的不同物体；零间隙共面接收面本就具有连续的阴影场。

把阴影评估从 PBR_Shading 内移到全屏 Fragment，在背景 return/discard 之前调用。PCSS 内部的有效性/级联分支先收敛，再执行所有四个 lane 的 ddx_fine/ddy_fine 邻居读取；不能在背景 lane 提前退出后再借邻居。已经重建的 visibility 作为参数传入 PBR_Shading，仅作用于太阳直射。既有诊断模式 2～5 用 draw-wide uniform 条件跳过阴影处理。

原随机格子为第一级 shadow texel 的 1/16，在当前近景横跨数个屏幕像素，使邻居高度相关。现改为 **1/128**，当前格宽约 **0.00070241763 m**，仍固定于光源/世界坐标。调整只改变随机采样密度，不改变 Stable CSM 的投影宽度或中心对齐。FOV/投影改变仍会更新尺度；无时间重建时，移动相机下的细噪声仍需用户验收。

GPU 同帧对照：在当前 Scene 相机 1397×780 输出八组遮罩，区分旧 Vogel 16/32、Sobol 16/32、粗/细随机格子，以及有无 quad 复用。输出位于 `OfflineBaseline/ShadowSamplingReview/`（忽略的生成物），复现入口仍为 `Tools/UnityMcp/CapturePcssPatterns.cs.txt`。探针把 raw/quad 拆为独立 Pass，修正了最初合并分支版本触发的 Unity ShaderCompiler IPC 错误；最终生产 Lighting 和两个诊断 Shader supported=true，ShaderUtil 无 error，控制台查询 0 条 error，未主动清空控制台。

以下指标由 `Tools/AnalyzePcssPatterns.py` 从 **8-bit 线性可见度 PNG** 计算：以 64+64/quad 遮罩的 0.1～0.9 区域选择半影，sigma=2 像素高通 RMS 仅用作高频起伏代理，不是渲染中的滤波，也不是光追真值误差。64+64 对照本身未收敛。当前同帧结果：

| 方案 | 半影高通 RMS | 相对 64+64/quad 的 RMSE |
| --- | --- | --- |
| 旧 Vogel 16+16、旧格子 | 0.019610 | 0.024605 |
| 旧 Vogel 16+32、旧格子 | 0.015629 | 0.020236 |
| Sobol 16+32 raw、旧格子 | 0.016623 | 0.021832 |
| Sobol 16+32 quad、旧格子 | 0.013644 | 0.019064 |
| Sobol 16+32 raw、细格子 | 0.020851 | 0.020747 |
| **当前 Sobol 16+32 quad、细格子** | **0.012407** | **0.012655** |
| 64+64 quad、细格子（仅对照） | 0.004726 | 0 |

当前相较旧 16+16 的高频起伏代理约下降 **36.7%**。结果说明细格子必须配合邻域复用；单独换 Sobol 或缩小格子没有消灭噪声。实际图像仍有细颗粒，不能以这个指标声称已经达到 UE 最终画面质量。

验证：`ValidateDirectPcss.ps1` 执行提取的真实采样/PCSS 函数，在普通/反向 Z 各九组预算验证接触收紧、深度原点平移、全亮/全暗、零半径和提前退出；补充 Sobol 二维分层、移位后的圆盘矩统计、接收面/法线/背景/级联拒绝。`Tools/UnityMcp/ValidatePcssReuse.cs.txt` 在实际 D3D11 GPU 上调用生产 quad 函数：同面复用得到 0.37116/0.62884；平行错开、背景、法线突变、跨级联、跨混合带和大足迹六种拒绝条件均保持 0/1；全暗/全亮保持不变，共九项、最大拒绝误差为 0。43 项 HLSL 编译、Runtime Player/Editor 条件 API 编译、CSM 覆盖/旋转稳定性、相机隔离及 HDR 输出检查通过。最终活动管线读到搜索 16、过滤 32、阴影图分辨率 1024。

本轮新增了过滤采样、Sobol 映射/噪声读取和导数计算成本，尚未取得目标手机 GPU 计时或 Player 构建验收。没有增加 GI、shadow cache、VSM、TAA 或阴影历史缓冲；当前不是无噪声完成态，剩余画面和运动质量继续单独评估。


## 已修复问题 33. [P2] 分开渲染的活动相机会反复释放彼此的 GBuffer 与输出目标

状态：**2026-10-21 接入 TAA 时修复。以下为原始问题描述，修复及复测见下节。**

位置：[MaxRendererPipeline.cs:50](Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs:50)、[MaxRendererPipeline.cs:243](Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs:243)、[MaxRendererPipeline.cs:257](Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs:257)。

`PruneCameraResources` 将不在本次 `Render(cameras)` 数组中的相机释放，`PruneOutputResources` 同样只保留本次输出键。Scene、Game、材质预览等可能来自不同 Render 调用；缺席一次调用不等于相机销毁。后一次调用再为仍存活的相机分配资源，形成往返重建。天空 LUT 的 `PruneDestroyedCameras` 已经按存活状态处理，但 GBuffer/输出缓存尚未采用相同原则。

复现：执行提取的真实清理函数，传入仍存活的 A/B 两个相机及不同输出键，本次只渲染 A，B 的相机资源与输出都被 Dispose；A 被保留。B 下次渲染会重新分配。1920×1080 下仅四张 GBuffer 彩色纹理的逻辑存储就约 **63.28 MiB/相机**，尚未计深度、HDR 和阴影目标。

处理建议：按相机销毁、输出对象销毁或带宽限的最后使用时间回收，不以一次调用的摄像机列表判定全局失效。用同屏 Scene/Game 和预览交替的真实资源实例 ID、分配量及帧时间做复测。


## 已实现／待动态画面与真机验收（2026-10-21，原生分辨率全画面 TAA）

按基础设施定位接入原生分辨率 TAA，不实现超分。采用成熟的 Salvi 方差裁剪、YCoCg 颜色体积和 Catmull–Rom 历史重建；参数与工程适配由本管线持有，不引入独立阴影时间历史。完整跨模块约定见 `Documentation/TemporalAA.md`，已加入 AGENTS.md。

- 每相机无 jitter/有 jitter 矩阵、8 点 Halton、独立历史与显式 cut API；剔除和 Stable CSM 保持无 jitter，剔除边界保守扩展一像素。大气 LUT 仍为无 jitter 方向，Lighting 查询减去 jitter，生成 LUT 后恢复相机栅格矩阵。
- 新增 MotionVectors Pass：Unity 原生上一物体矩阵和蒙皮旧顶点约定；有符号 UV 速度、上一眼空间深度及有效性独立保存，GBuffer1.a 声明材质的时间契约。Unity 省略 Camera-only 物体绘制时采用相机重投影，ForceNoMotion 拒绝历史；未声明支持的几何体不能静默冒充有效零速度。
- Lighting、雾与天空统一进入 HDR TAA，然后输出曝光、tone mapping 和 dithering。Editor gizmos 在解析后绘制；天空方向重投影忽略平移，正交天空转动时不复用旧方向。
- 正式运行路径删除 PCSS 2×2 邻域平均，旧 quad 仅由 Editor 诊断宏包含。TAA 启用时 PCSS 推进独立 R2 相位，关闭时保持世界固定相位。半影公式、太阳角、光照能量约定未修改。
- 相机 cut、投影/尺寸/视窗/目标变化、RT 丢失或销毁、长时间不渲染时重置。Scene 初始以及相机/太阳变化后主动推进收敛帧。多相机共享输出的同次 Render、Depth/Nothing 叠加、XR、反射和 Preview 旁路，不承诺多投影堆叠已支持。
- **问题 33 修复**：不再以本次 Render 数组释放其他活动相机的 GBuffer/HDR 输出；按销毁及输出迁移清理。提取真实清理函数的反例现返回 OtherLiveCameraResourcesDisposed=false、OtherLiveOutputDisposed=false；真实 Unity TAA 双相机生命周期夹具也保留另一相机历史。

验证：54 项 HLSL SM5 检查（包括普通 Z、上下纹理原点的模拟分支）及 Runtime Player/Editor 条件的 Unity API 编译通过。阴影稳定拟合、PCSS、相机隔离、视窗与 HDR 输出既有检查通过；这不是 Android Shader 编译或 Player 构建。

Unity MCP 实测生产 Shader 的 12 项 GPU 夹具通过：常量、合成噪声、硬阶跃、深度拒绝、越界、ForceNoMotion、历史重置、天空、横向/纵向速度、Camera-only 回退及不支持材质拒绝。合成 ±0.05 噪声在给定有效历史下 RMS 约 0.00294；该数字不代表实际 PCSS 降噪比例。硬阶跃在该静止夹具中的最大误差约 0.000195（FP16 量化量级），不代表运动边缘无模糊。

11 项真实 Unity 矩阵/资源生命周期检查通过，包括不修改相机投影、双相机隔离、显式 cut、Release 与 DestroyImmediate 恢复、旁路及 depthTextureMode 恢复。隔离 Game 相机实际 DrawRenderers 复测：刚体水平平移 0.3 m、前表面深度 4.5 m 的平均 UV 速度为 0.04327393，与投影参考 0.04330127 相符；Camera-only 省略物体 Pass 后仍有有效历史计数 3；ForceNoMotion 的 529 个几何像素拒绝历史，计数回到 1。初测发现的模式位方向错误已在正式源码修正后重测。

当前 Scene 同帧 HDR 输入/解析输出及运动纹理已导出，非有限值均为 0。图像和报告位于忽略目录 `OfflineBaseline/TemporalAA`。移动阴影拖影、蒙皮动画、透明反应性、Player 构建及手机 GPU 带宽/功耗尚未完整验收。新增持久 RT 约 40 字节/像素，1080p 约 79.1 MiB/启用相机；这版是正确性基线，不能当作移动性能优化已完成。
## 已修复／当前视角复测通过（2026-10-21，PCSS 接收平面比较深度）

用户反馈全画面 TAA 接入后仍有固定的斜线/阶梯 pattern。本次通过当前 Scene 相机的纯阴影可见度对照定位：原算法即使对 64 blocker + 64 PCF、32 组相位取平均，固定条纹仍存在；把 normal map 换为平面法线也不消失。它是偏移采样的比较深度错误，不能靠增加 TAA 历史消除。

旧 PCSS 把中心像素比较深度用于整个搜索/过滤核；斜面上各偏移位置对应的接收深度不同，造成错误自遮挡、blocker 统计污染和固定条纹。现在每次采样按接收几何平面计算 device-depth 偏移；搜索时先用偏移后的接收深度分类，再把 blocker 深度换回中心参考后求均值；PCF 也使用各采样位置的接收深度。算法依据见 [Microsoft CSM：Per-Texel Depth Bias](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/cascaded-shadow-maps#calculating-a-per-texel-depth-bias-with-ddx-and-ddy-for-large-pcfs)。

世界位置导数在背景返回与级联分支前计算，不使用法线贴图拟合几何。每级通过 shadow VP 的方向变换求 device-depth/UV 梯度，统一 UV Y 与普通/反向 Z；归一化导数避免近距离微小像素足迹造成阈值退化，避免直接相减大投影深度的消减误差。深度轮廓突变、退化/非有限导数回退原有 bias。原有 bias、角半径、世界半影上限、Stable CSM、采样预算和 TAA 参数未调整，也未增加可见度空间平均。

验证：`ValidatePcssReceiverPlane.cs.txt` 直接调用生产 Shader。倾斜平面在 16/32/64 次搜索下旧逻辑分别得到 7/13/26 个假 blocker，校正后均为 0；导数尺度 1e-6、1、1e4 三档的梯度均为 (0.4,0.2)，共 9 项 GPU 检查通过。普通/反向 Z 的实际 PCSS CPU 夹具、9 组采样预算、接触硬化、全亮/全暗、近原点平移与早退均通过；56 项 HLSL 检查通过；几何导数计算顺序已加入 `ValidateSceneShadows` 回归。

当前相机的同帧 A/B 位于忽略目录 `OfflineBaseline/PcssConvergence`：oldMean64 与 correctedMean64、oldMean32 与 correctedMean32；固定斜向阶梯在校正组消退。`FinalColor/resolved.png` 为正式 Lighting + TAA 输出，颜色/运动读回均无 NaN/Inf；仍有有限采样的细噪声，不把此次修复宣称为所有角度或移动阴影都已验收。

诊断修正：Unity 的旧 SetInt/SetGlobalInt 浮点上传接口不能用于当前 HLSL int 预算；本次诊断与旧 `CapturePcssPatterns` 已改为 SetGlobalInteger，并在绘制后恢复预算。**前面“PCSS 低差异采样/受约束邻域复用”记录中的 GPU 16/32/64 预算对照没有可靠覆盖整数参数，相关预算间误差与降噪百分比不能继续作为定量结论**；本节使用修正后的上传与实际采样结果复测。斜面夹具纹理/矩阵改为绘制局部绑定，避免改变运行管线全局阴影纹理。Editor 隐藏时诊断脚本显式渲染 Scene 相机，避免把过期文件或超时输出当作新结果。

## 上轮排查记录／未解决用户现象（2026-10-21，移动阴影）

用户接受静态画面后，先提交基线 **9421efa**。上轮临时把 blocker 搜索从 16 改为 64，并给共享 TAA 添加照明变化检测；合成软半影用例中的误差下降约 53%，但参考仍来自相同 shadow map，未覆盖源图栅格误差。**用户随后确认实际栅格闪烁没有缓解，因此不能把这组数字作为用户问题已修复的证据。**

本轮撤回上述两项额外开销，恢复已验收的 **16/32 PCSS** 和原共享 TAA。离线试验图像及工具仍保留于 `OfflineBaseline/MovingShadows`、`CaptureMovingShadows.cs.txt` 和 `AnalyzeMovingShadows.py`，用于区分后续采样误差；未增加阴影专用 TAA 或空间模糊。

## 已调整／待用户动态验收（2026-10-21，移动 caster 的 shadow map 栅格步进）

实际 Scene 相机 FOV=60°，1397×710，ShadowDistance=500 米。原来四级均为硬编码 1024²；稳定包围球加 padding 后，四级投影宽度约 97.91、266.30、591.29、1311.07 米，单 texel 分别约 9.56、26.01、57.74、128.03 厘米。近处物体缓慢移动时，轮廓跨过 texel 边界会离散改变栅格覆盖；Stable CSM 固定相机投影不能让移动几何覆盖连续。PCSS 高采样与 TAA 对原图的平均也不能还原源图缺少的轮廓。

新增逐级 `MapResolution`（1024/2048/4096）。当前资产及新建设置的质量配置为 **4096/2048/1024/1024**，仍保留 500 米覆盖和既有切分。实际纹理大小同步用于 viewport、CSM padding、texel snapping 和 Editor 采样尺度。支持运行时调整尺寸；原生 RT Release/Destroy 后重新分配并绑定本 Pass 资源，避免陈旧全局纹理。当前 Scene 近两级实测单 texel 约 **2.39、12.99 厘米**，远两级不变。太阳角、世界半影公式、bias 与 TAA 均保持验收基线。

独立复现：`CaptureShadowRasterMotion.cs.txt` 在临时 layer 30 创建离屏 caster 和可见地面，相机 far=1000、阴影覆盖=500；物体每帧平移 4 毫米，分别采集 1024/2048/4096 的 48 帧。固定相机、主相机 jitter 和诊断采样序列；每帧以 64/64 次、16 相位平均 PCSS，仅用于剥离随机采样噪声，未添加运行时滤波。另由 `AnalyzeShadowRasterMotion.py` 用解析 ray/box 相交与 256 个太阳圆盘方向独立计算可见度，不读取 shadow map，不包含 TAA 或 GI。

| 单级图分辨率 | 最大帧间可见度 RMS 跳变 | 相对解析参考的时间残差 RMS |
| --- | ---: | ---: |
| 1024 | 0.048927 | 0.013380 |
| 2048 | 0.025936 | 0.009338 |
| 4096 | 0.013543 | 0.006534 |

同一区域解析参考的最大帧间变化约 0.004619；4096 相比 1024 的最大跳变量降低约 **72%**。太阳圆盘精确光线可见度和 PCSS 是不同近似，绝对误差不能当作 PCSS 物理公式已完全验证，也不保证解析结果应逐像素相等。主要证据是同相机、同覆盖、同运动下，栅格跳变随源图分辨率显著缩小。输出位于忽略目录 `OfflineBaseline/ShadowRasterMotion`。

真实 Unity 复测：四级尺寸 4096/2048/1024/1024、运行时更改为 2048、Release 恢复、Destroy/fake-null 恢复及 owned global binding 均通过；入口 `ValidateShadowMapResolution.cs.txt`。源相机隔离夹具改为混合尺寸，验证实际 Execute 的每级 viewport 与纹理相符。三档分辨率的 1152 组 CSM 旋转/平移稳定性、4608 个覆盖及 10368 个过渡/核支持检查、Player/Editor 条件 Unity API 编译均通过。

成本与边界：按每 texel 4 字节估算，四张深度图从约 **16 MiB 增至 88 MiB**，光栅填充开销也增加；实际驱动占用及手机耗时未测。这是用于确认/改善画面的质量配置，分辨率仍可逐级下调。后续可结合目标阴影距离和级联切分改善分配效率；本轮不缩短覆盖距离、不加入 shadow cache/GI，也不把更高分辨率宣称为根除所有阴影走样。

## 已修正／待用户画面验收（2026-10-22，球体阴影波浪与近级覆盖）

用户在直径 1 米的 Sphere 上观察到阴影边缘起伏。实际网格为 515 顶点、768 三角形；当前 Scene 相机距离球体约 2.8 米，而第一张 4096² 图覆盖约 90.18 米、每 texel 约 2.20 厘米，球宽仅约 45 texel。临时换成 64×128 分段、约 1.6 万三角形的球后，主要起伏仍在。

`CaptureSphereShadows.cs.txt` 在同一 Scene 相机对比原始样本、64/64 taps × 32 相位平均、平面法线、临时近级收紧和高模球。测试后恢复原网格与设置；平均仅用于诊断，不进入运行时。对照发现两项：

- **normal map 扭曲 bias**：此前 `positionWS + normalWS * ShadingPointNormalBias` 中的 normalWS 来自砖面法线贴图。0.1 米偏移带有横向分量，即使几何平坦，也会让阴影查询位置随纹理凹凸变化。现在通过已存在的世界位置导数重建几何接收面法线，朝向相机；世界法线偏移和 slope bias 使用该法线。BRDF 及有无直接光照判断继续使用贴图法线。可靠导数缺失时关闭法线位移，保留深度 bias，不增加 GBuffer 通道或历史。
- **首级覆盖浪费近处密度**：新增 `FirstCascadeMaxDistance=10m`。第一分界从约 35 米提前至 10 米，仍保留后续约 100/225/500 米终点、四级分辨率与既有物理半影公式；参数为 0 可恢复原比例分配。实际当前首级投影宽约 26.89 米，texel 约 **6.57 毫米**，球宽约 **152 texel**。显存不增加；10 米以后更早使用第二级，这是近/中距离分配的取舍。

只修正 bias 的 A/B：球体地面半影区域，原贴图法线与平面法线对照的平均可见度差为 0.10950、最大差为 0.46398；修正后两组逐像素一致。修正组与旧平面法线参考最大差约 0.000246，来自有限精度导数/投影计算。继续收紧首级后，剩余栅格轮廓进一步变圆。诊断目录为忽略的 `OfflineBaseline/SphereShadowReview`，`comparison.png` 三列依次为原结果、仅几何 bias、收紧近级；`FinalColor/resolved.png` 为正式 Lighting + TAA + 输出转换的读回。

验证：`ValidateShadowBiasNormal.cs.txt` 的 9 项实际生产函数 GPU 测试通过，覆盖三档导数尺度、反向导数顺序、相机上下两侧、斜面、零/共线/NaN 回退。57 项 HLSL 编译通过。首级限制 0/10 米、三档分辨率下，2304 组稳定性、9216 个覆盖与 20736 个重叠/核支持检查通过；Runtime Player/Editor 条件 Unity API 编译通过。当前 Scene 同帧 HDR 输入/解析结果/运动数据均无 NaN/Inf。尚未据此宣称所有材质、轮廓回退及快速运动均已验收。

## 已修复（2026-10-22，Inspector 切换阴影分辨率时销毁隐藏相机报错）

Inspector 的 SerializedObject 修改触发 RenderPipelineAsset.OnValidate，旧 MaxRenderPipeline 同步 Dispose；隐藏阴影相机随后经 CoreUtils.Destroy 走编辑模式 DestroyImmediate。Unity 禁止在 OnValidate/渲染等回调中立即销毁 GameObject，因而报错并遗留无归属的隐藏相机。此前仅直接改 C# 设置字段的分辨率测试没有覆盖这个资产验证路径。

`MaxRenderResources.Destroy` 对 GameObject 单独处理：持有方引用立即置空；编辑模式下一次 EditorApplication.update 再销毁，运行时使用 Object.Destroy。待清理 ID 存入 Editor SessionState，并由 InitializeOnLoadMethod 在脚本重载后继续清理，避免普通延迟闭包随 domain reload 丢失；已被其他路径销毁的对象跳过。纹理解绑、RT Release 及其他渲染资源的既有释放方式保持不变。

通过 MCP 清理了本工程中 162 个旧错误遗留的、明确标记 HideAndDontSave 且不属于活动 Pass 的隐藏阴影相机，保留当前管线相机。复测入口 `Tools/UnityMcp/ValidateShadowDisposal.cs.txt` 使用真实 SerializedObject.ApplyModifiedPropertiesWithoutUndo 连续四次切换并恢复原分辨率，每次都确认管线重建、旧相机在 Editor 更新后消失、阴影图尺寸正确，隐藏相机数量始终为 1，监听期间无新增 Console error。未清空原控制台日志。

另外实际请求了一次脚本重新编译/domain reload；重载后隐藏相机仍为 1、SessionState 待清理数为 0。验证记录在忽略目录 `OfflineBaseline/ShadowDisposal`。Player/Editor 条件 Unity API 编译及源相机隔离/Execute/Dispose 检查通过；本轮未改变已验收的阴影采样、bias、级联覆盖或 TAA。


### 4096 上限的 CSM atlas 与远级 PCF（2026-10-22）

按用户确认，将方向光四张独立深度图合并为一张最大 4096×4096 的 atlas，运行时仅绑定一个阴影纹理资源，共享 comparison/point sampler。固定 2×2 布局，当前每级 2048 tile（含保护边），有效 viewport 2044×2044；首级相对原 4096 降低分辨率，其他三级相对原 1024 提升。当前 D3D11 深度实际为 32 bit，逻辑存储约 64 MiB。没有增加阴影缓存、GI 或第二套时间解析。

距离配置改为 UE 同类等比区间分布，`DistributionExponent=2`，500 米下约 33.3/100/233.3/500 米；移除旧固定比例与首级单独上限。保留 Stable CSM、每级有效分辨率 snapping、重叠拟合、10% 可见度混合和末级淡出。近两级 PCSS，远两级确定性 3×3 tent PCF，跨算法边界仍混合两级结果。PCF 的纹理过滤不等于屏幕空间模糊。

atlas 每相机完整清一次，逐级不再清整张深度。宽核搜索和过滤均限制在 tile 有效 texel 中心内，按限制后的偏移做平面修正。真实投影 GPU 测试覆盖 viewport 与纹理 UV 约定，防止 atlas 行重复翻转；资源销毁/丢失恢复与 Inspector 生命周期继续遵守共享 TAA 契约。实现、参数和验证入口详见 `Documentation/ShadowAtlas.md`。

验证：72 组 atlas PCSS/PCF 边界采样、8 个实际投影内外点、9 组接收平面 GPU 检查通过；真实 Inspector 四次 tile 切换保持单一隐藏相机且无新增错误。数值检查覆盖四种分布、透视/正交、自定义投影与级联重叠；生产 HLSL 及深度/法线变体通过。Scene/Game 同帧输出已检查。手机 GPU 的性能与功耗仍未实测。

### 当前阴影阶段收尾（2026-10-22）

用户确认当前阴影画面可以暂时收尾，保留现有 4096 atlas、近两级 PCSS／远两级 PCF、Stable CSM 与共享 TAA。Shadow cache 延后至地形系统引入后再设计，本阶段不实现。

当前测试场景已迁移到 UE 5.3 移动前向路径对照，生成工具与参数记录见 `Tools/UnrealBaseline/README.md`。用户观察到球体阴影轮廓相近、UE 柔边更宽；两边每级分辨率、过滤和深度比较不同，尚未统一世界 texel 尺寸，因此不把柔边宽度差直接解释为 PCSS 半影公式错误。此结论不扩大为移动真机性能或所有运动场景已验收。

## 已接入（2026-10-23，Clustered Deferred 点光）

在现有 GBuffer Lighting 中接入每相机独立的三维局部灯列表，默认 64×64 像素、32 个深度层、每格 64 个存储索引。CPU 收集不再于 32 灯截断，点光的 BRDF、物理强度和衰减不改，方向光仍单独计算。近远／格子范围外、容量溢出或不支持建表时回退完整灯列表，不以任意丢灯换性能。沿用共享 TAA，不增加阴影历史或随机采样；shadow cache、GI、聚光灯／面光和局部灯阴影仍延期。详细契约见 `Documentation/ClusteredLighting.md`。

真实 GPU 的 8 组生产 Compute 测试：24523 个视锥内点、300835 组点／光球检查，无漏灯，包含负 near 正交、非对称投影和 129 灯容量溢出。实际 GBuffer／Lighting 的 96 灯场景完成 7 组开关对照，涵盖透视／正交／非对称、共享 TAA jitter、容量 8 溢出、零灯与缩放 view 回退；HDR 读回在该夹具内逐像素一致。每相机隔离、缺席保留与原生 buffer 丢失恢复通过。生产 HLSL、Runtime Player／Editor Unity API 编译及物理光照数值检查通过。结果位于忽略目录 `OfflineBaseline/ClusteredLighting`，不宣称手机性能或 Player 构建已验证。

## 已接入（2026-10-23，Bistro 室外测试场景）

新增独立 `Assets/Scenes/BistroTest/BistroTest.unity`：1296 个网格、132 个材质、2,832,120 三角形，53 盏默认展示点光，另有默认关闭的额外 128 / 256 / 512 灯压力组。原场景保持不变。Bistro 材质适配原包 ORM 通道、DirectX 法线和叶片 alpha clip，GBuffer / Shadow / Motion 使用一致裁剪；既有 GBuffer3 自发光在雾之前接入 Lighting。`MaxSceneExposure` 只覆盖所属场景的最终曝光，不改管线资产、GraphicsSettings、QualitySettings 或 HDR 历史约定。

实际 Unity GPU 检查通过：默认视角可见 46 盏点光，三个压力档分别为 111 / 179 / 311，输出非空且有限；曝光启停及 Game / SceneView / 活动场景范围通过。模型、贴图和派生纹理本体留在本地，Git 仅收录 819 个依赖 `.meta`，另提交场景、材质、Shader、恢复工具与许可说明。完整餐厅室内尚未导入；玻璃当前隐藏，AO 未消费，局部灯阴影和 GI 仍不在本版。详见 `Documentation/BistroTest.md`，不将正确性读回视为桌面或手机 GPU 性能测量。
