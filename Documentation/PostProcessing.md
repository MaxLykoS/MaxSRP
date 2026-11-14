# 后处理

本版提供全局曝光、标准 Bloom、基础调色、Tone Mapping 和 SDR 输出处理。算法依据是本机 UE 5.8.4（CL 58902052）的 Renderer/Shader 源码；保留 MaxSRP 的固定光度缩放、共享 TAA 和相机输出契约。未新增 Local Exposure、TAA 锐化、景深、运动模糊或镜头风格效果。

## 配置与使用

- 独立资产为 `Assets/MaxRendererPipeline/DefaultPostProcess.asset`，由管线资产的 `PostProcessConfiguration` 引用。可通过 `Create > MaxSRP > Post Process Configuration` 创建其他配置资产。
- 配置资产保留曝光 Compute、Bloom Shader 和最终输出 Shader 的序列化引用；Player 构建验证会检查这些引用，不能依靠运行时名称查找来防止 Shader 被剔除。
- PC / Mobile 各自保存曝光、Bloom、调色、Film 和 Dithering 参数，实际档位统一由当前 `MaxRendererPipelineAsset.Platform` 选择；后处理配置不再提供独立开关。总控与运行时刷新方式见 [RenderConfiguration.md](RenderConfiguration.md)。
- 默认 PC 曝光为 64 桶 Histogram，Mobile 为 Basic；两种方法均可在各自配置中选择，另外提供 Manual。基础调色默认关闭，参数默认中性；Film 默认 `UEFilm`。
- 项目当前的 `DefaultPostProcess.asset` 为 BistroLumen 对照配置：**PC / Mobile 均使用 Manual EV100 10.5、曝光补偿 0、Bloom 关闭且强度 0**，保持 UEFilm。上条描述新建配置的代码默认值，两者需区分；自动曝光与 Bloom 功能继续保留，可在 Inspector 按需配置。
- Game 相机和 Scene 视图使用新链条。Preview、Reflection、XR 和线性诊断旁路新后效，沿用原输出设置。没有配置资产时保留原有输出行为。
- 新配置是曝光的唯一来源。旧 `MaxSceneExposure` 仅在未绑定后效配置时保留原场景覆盖行为，不会使新配置中的 Histogram/Basic 失效。
- 原管线内的 `ColorOutputSettings` 兼容参数也迁入后处理配置的隐藏字段；旧输出标定与诊断旁路规则保留。

旧 `ColorOutputSettings` / `MaxSceneExposure` 仍使用 `Lmax = 1.2 × 2^EV100`，保留既有场景曝光；新配置采用 UE 默认镜头衰减对应的 `Lmax = 1 × 2^EV100`。相同手动 EV100 的新配置约比旧标定亮 1.2 倍（约 0.263 stops），不要通过改写灯光强度来补偿这两个标定的差别。

## 顺序、颜色与历史

生产顺序为：

`Lighting/雾/天空 → 共享 TAA → 曝光统计与 Bloom → Editor gizmos → 最终曝光/调色/Film/输出抖动`

- 曝光统计和 Bloom 读取已经解析的干净场景 HDR，不读取 Gizmo、UI、已加 Bloom 或已调色的结果。它们不写回场景颜色和 TAA 历史。
- 场景颜色与 Bloom 都是 `cd/m² / LightPhotometry.HDRScale`，当前 `HDRScale = 100000`。Bloom 与场景相加后，最后统一乘一次曝光倍率。曝光、Bloom、调色或 Film 变化不改变 TAA 历史的光度范围。
- 总控 PC / Mobile 切换在 Render 边界重建并重置历史；普通后处理参数编辑和非当前档参数编辑不因此重建整条管线或清空 TAA。
- 自动曝光只保存每相机的少量曝光状态，没有第二套颜色时间解析。显式切镜头使用 `MaxRenderPipeline.ResetTemporalHistory(camera)`，同时重置曝光状态；首帧和失效状态直接取当前目标曝光。
- 每相机按自己的有效视窗测光、生成独立局部 Bloom 纹理；输出时将场景 UV 映射到该视窗，避免读取分屏相邻相机。尺寸变化、资源丢失、平台切换和销毁按各 Pass 生命周期处理。
- UE 使用动态 PreExposure；本版保留 MaxSRP 的固定 HDRScale，没有引入 PreExposure。UE 移动端上一帧 Bloom Setup 测光与旧曝光 Buffer 的调度也没有直接照搬；此处按 MaxSRP 的共享 TAA 后处理顺序生成和使用曝光，不能宣称两套引擎逐 Pass 一致。
- 太阳盘已对齐 UE 的盘内软边和立体角归一化。UE 天空 pass 的 `min(disk * PreExposure, 64000)` 依赖其动态工作范围。Max 在写入 RGBA16F 前仅按自己的固定 HDR 范围清理光盘 NaN、限制非负并限至 64000，为叠加天空留出余量；它不是 UE 预曝光限幅的等效换算或物理亮度参数，常规太阳值不会触发，也不将最终曝光写进天空 / TAA。当前与 UE Bistro 同样关闭 Bloom，避免太阳高亮扩散造成可见圆盘膨胀；开启 Bloom 时仍应区分这项 PreExposure 限幅差异。

## 曝光

参数包括 Manual EV100、曝光补偿、自动曝光上下限、Speed Up/Down、Histogram 亮度范围和低/高百分位、测光 Mask、补偿曲线。速度单位为 stops/second；参考 UE 的线性与指数适应，在目标附近平滑收敛。

Mask 为线性权重纹理，白色贡献较大。Basic 使用带权对数亮度平均；Histogram 用 64 桶保存亮度分布并排除指定百分位之外的样本。测光恢复物理亮度时考虑 HDRScale，曝光结果是直接乘到当前场景 HDR 的倍率，包含曝光补偿。

Mobile Basic 参考 UE 的 HDR 加权降采样，但权重作用于本项目固定 HDRScale 的值而非 UE 动态 PreExposure 的值，因此混合明暗样本的权重不保证逐像素相同；纯色输入保持不变。两档均读取当前帧解析后的 HDR，此处不引入 UE 移动端的旧测光纹理调度。

补偿曲线 X 为测得的场景 EV100，Y 为补偿 stops，通过 64 样本 LUT 在 GPU 求值。零有效权重与黑场需要能恢复亮度；无 Compute 或缺少所需纹理格式时使用手动曝光回退。各相机状态隔离，不通过逐帧 CPU 读回控制曝光。

对应 UE 文件：`PostProcessEyeAdaptation.cpp`、`PostProcessHistogramCommon.ush`、`PostProcessMobile.usf`。

## Bloom

- PC 采用 UE Standard / Sum of Gaussians 的六尺度方案。各尺度尺寸为 `0.3, 1, 2, 10, 30, 64`，默认灰色权重为 `0.3465, 0.138, 0.1176, 0.066, 0.066, 0.061`；降采样后分别进行可分离 Gaussian 滤波和由粗到细的加法合成。Gaussian 形状使用 UE 的 `exp(-16.7 × (x/radius)²)`，相邻 taps 合并为双线性采样，最后归一化。
- Mobile 采用 UE Q1 预算：1/4 宽高的准备纹理、四次 2 倍降采样、三次上采样和最细层合成；圆周采样分别为降采样 15 taps、上采样每源 8 taps、最终近层 7 taps。
- `Intensity` 默认 0.675，`SizeScale` 默认 4，与 UE 参数同义。Mobile 保留 UE 默认圆周半径，并以 `SizeScale / 4` 调整该半径。
- `Threshold = -1` 完全跳过阈值。启用阈值时按曝光后的线性亮度执行 UE 两单位的平滑过渡，但中间 Bloom 仍保存未曝光的固定场景光度；最终输出不再额外乘第二遍曝光。
- Mobile 准备阶段的 HDR 加权平均改写为等价的 `sum(color × weight) / sum(weight)`，避免反解 `1 - Luma` 在极亮值附近相消。采样中的 NaN/负值清零，正溢出与超范围高光限制在 RGBA16F 的 65504 内；异常曝光也在相乘前处理，避免产生非有限纹理。
- 关闭或强度为零时返回黑纹理并释放该相机 Bloom 资源，不新建颜色历史。

参考 UE 文件：`PostProcessBloom.usf`、`PostProcessDownsample.usf`、`PostProcessWeightedSampleSum.cpp`、`PostProcessBloomSetup.cpp`、`PostProcessMobile.usf` / `.cpp`。无阈值、常量 HDR 的理想输出/输入比值为 PC `0.132516667 × Intensity`，Mobile `0.11277 × Intensity`；这是核归一和层权重的数值检查，不表示两档模糊形状完全相同。

## 调色、Film 与输出

基础调色包含白平衡、Temperature/Tint 以及全局 Saturation、Contrast、Gamma、Gain、Offset。向量 RGB 分别控制通道，W 为主控；关闭时直接跳过用户调色。调色参考 UE 的工作空间转换和全局 Color Correction，Film 的默认高光处理不属于用户调色开关。

`UEFilm` 使用 UE 的 AP1 工作空间与 Film 曲线，提供 Slope、Toe、Shoulder、Black Clip、White Clip；不是把旧的逐通道拟合曲线改名。枚举 `ACES` 保留原 Narkowicz 拟合曲线作为兼容模式，`None` 旁路 Tone Mapping。此版没有实现 UE 全部 LUT 编辑功能、HDR 显示设备变换或完整 ACES 输出设备管线。

在项目 Linear 色彩空间中，由 sRGB 目标完成最后显示编码，线性 RenderTexture 保留线性值，不重复应用 Gamma。Dithering 默认开启，对 8-bit 显示编码抖动，高精度导出跳过；最终输出保留源 alpha 和深度。不能将这个 SDR 输出路径当成 HDR10/PQ 支持。

浮点目标保留 UE Filmic 的高光余量：默认 WhiteClip=0.04 的中性灰极限约为 1.04。UE 的 Filmic LUTMax 使用 1.05，不能为满足 0–1 断言而在浮点输出前统一截断；8-bit 显示目标由格式/输出抖动阶段处理范围。

## 验证

验证入口：

```powershell
./Tools/ValidateShaders.ps1
./Tools/ValidateLightingShaderRetention.ps1
./Tools/RunPostProcessValidation.ps1
```

前两项分别检查生产 Shader 编译及引用保留/程序集；第三项运行 Unity batch GPU 数值验证，入口为 Editor 中的 `MaxPostProcessValidation.RunBatch`，也提供 `Run` 菜单入口。应覆盖曝光常量、百分位与适应、Bloom 常量能量/阈值/亮点扩散/高光稳定性、默认调色中性与 Film/输出的有限性。

只有实际执行的报告才能作为当前验证结果；本文件描述入口与预期，不预先声明测试通过。真实场景观感、Player 构建以及移动真机 GPU 带宽和功耗仍需在相应环境确认，数值夹具不能代替这些验收。

2026-11-13：Unity 2022.3.62f2c1 / D3D11 / RTX 3080 Ti 上运行此夹具，71 项 GPU 检查通过。两个平台档的曝光/Bloom 均参与验证，Bistro Scene 视图也已分别切换 PC/Mobile 检查，最终配置恢复 PC。结果保存在被忽略的 `OfflineBaseline/PostProcessing/validation.json`，灰阶/彩阶、Bloom 亮点和实际场景预览保存在同目录。生产 Shader 与 Player/Editor Unity API 编译检查通过；未做移动真机或 Player 构建验收。

同日太阳盘对齐：在相同 1920×1080、垂直 FOV 60°、30000 lux、EV100 10.5 条件下，生产天空 / 输出 Shader 离屏检查中，关闭 Bloom 并使用盘内软边后，白色区域横向约 8 像素，先前开启 Bloom 时约 80 像素。PC / Mobile 配置已由 Unity 读回确认；极端太阳输入放大一百万倍后输出保持有限，光盘限至 64000，再叠加天空约 0.04。生产 Shader 编译检查通过。该结果是本项目受控前后对照，不代表与 UE 完整画面逐像素相同；报告与图像位于被忽略的 `OfflineBaseline/SunDiskAligned/`。
