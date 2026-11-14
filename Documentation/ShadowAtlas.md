# 方向光 CSM atlas

方向光四级共享一张深度 atlas，宽高均硬性限制在 4096 以内；设备纹理上限不足时进一步缩小。运行时 Lighting 仅绑定 `_MainShadowAtlas`，PCSS 原始深度读取与 PCF 比较读取共用该资源，分别使用 point 与 comparison sampler。

## 布局和有效分辨率

采用固定 2×2 槽位，当前四级 tile 都为 2048×2048，每侧预留 2 texel，实际 viewport 为 2044×2044。每级可选 512/1024/2048 的 tile；旧序列化值 4096 在运行时也限制到 2048。较小 tile 不会缩小整张 atlas，当前没有动态打包器。

| 级联 | 当前有效分辨率 | 过滤 |
|---|---:|---|
| 0 | 2044² | PCSS，16 次 blocker search + 最多 32 次比较过滤 |
| 1 | 2044² | PCSS，16 + 最多 32 |
| 2 | 2044² | 3×3 tent 加权硬件比较 PCF |
| 3 | 2044² | 3×3 tent 加权硬件比较 PCF |

原先的 4096 首级不能与其他级联一起放进 4096 atlas，因此近级 XY 精度会下降；后三个级联相对之前的 1024 提升。当前 D3D11 实际分配为 32-bit depth，atlas 逻辑存储 64 MiB，四张原图按同格式估算为 76 MiB。atlas 并不减少四次 caster 绘制，也不等同阴影缓存。

逐帧模式每个相机只完整清 atlas 一次；复用模式仅在首帧/失效时完整清，之后以 viewport 约束的深度绘制清更新 tile，保留未更新内容。局部光空间 VP 保留原约定，采样时才应用 tile scale/offset；Stable CSM 包围球、padding、snapping 和世界空间纹素尺寸均使用 tile 的有效分辨率。viewport tile 偏移与纹理地址采用相同数值坐标，不能根据 `graphicsUVStartsAtTop` 再翻转 atlas 行；局部投影 Y 已由 `ShadowClipToUV` 处理。

所有 PCSS 搜索和比较采样均限制在本级首尾 texel 中心之间，再变换到 atlas UV。接收面逐样本深度修正使用限制后的实际偏移。保护区不能替代按 tile 约束采样；当前已有保守视锥/kernel padding，正常接收区域不应依赖边缘重复采样。

## 距离和过渡

`DistributionExponent` 表示相邻区间长度的比值，采用 UE 5.3 `ComputeAccumulatedScale` 同类的等比累积方式，而非对归一化距离直接取幂。默认 2，四段权重为 1:2:4:8。每级终点为相机 near 加上累计权重乘以 `min(ShadowDistance, camera.far) - near`。

500 米总范围下，忽略 near 后，默认终点约为 33.3/100/233.3/500 米。参数 3 对应 12.5/50/162.5/500 米，更侧重近景，但必须评估跨级精度落差。旧固定 7/13/25/55 权重与单独的首级上限已被替代。

`CascadeBlendFraction` 当前为 0.1，按相邻较短区间计算重叠带，远级拟合包含该重叠区域；在边界前平滑混合两级可见度。`DistanceFadeFraction` 当前为 0.1，在最后一个区间末尾淡出。`PcssCascadeCount` 默认 2，交界同样混合 PCSS/PCF 可见度。远级固定 PCF 不再承诺物理接触硬化，不能把它误认为完整 PCSS 的等价低成本实现。

每级实际密度为 `_OrthoWidthN / _ShadowResolutionN` 米/texel。距离、分辨率、bias 与过滤宽度需一起评估；单纯加宽过渡不能恢复缺失的轮廓，也不能消除所有软硬差异。保留现有世界单位 bias，不借 atlas 改动擅自重写场景调参。

CSM 始终使用无 jitter 矩阵；沿用共享整帧 TAA，不增加阴影颜色历史。Inspector 修改会通过资产验证重建管线与时间历史。资源丢失/销毁后重建并重新绑定 atlas；隐藏相机仍经延迟销毁路径释放。实验性阴影深度复用见下节。

## 固定的投影物 XY 剔除

用户验收后，XY 剔除在所有路径固定启用，已移除 `TightCasterCulling` 配置及原 `Shadow Culling` A/B 菜单。以包含级联重叠及一像素 jitter 保护的接收视锥角点，构建紧致光空间 XY 包围盒；向外扩张 PCSS 最大核半径、normal bias，以及覆盖 PCF 方形足迹对角线的 `2 * sqrt(2)` 个有效阴影纹素。它仅改变 caster 候选列表，原有稳定 raster VP、texel snapping、深度范围、atlas 布局、过滤和逐帧更新顺序均不变。原光空间 Z 两个剔除平面、六面顺序和遮挡剔除选项保留；非有限值或退化输入保守回退。构建复用固定数组，不增加每帧托管分配。

首版采用轴对齐包围盒而非完整多边形凸包：Unity 2022.3 当前原生 Cull 路径对超过六个平面的参数触发 `params.cullingPlaneCount == kPlaneFrustumNum` 断言。保留六面可收紧稳定方盒四周冗余，但凸包斜角处的部分无用 caster 仍会保守保留；不通过关闭原遮挡剔除改变 A/B 的含义。

以下 XY A/B 数据为功能验收时的历史记录；当前代码不再保留回到原大投影盒的开关。新的 `MaxSRP > Shadow Reuse` 只比较固定 XY 剔除下的四级逐帧更新与远级短期复用，不会关闭 XY 剔除。

CPU 指标为目标相机阴影 pass 的主线程经过时间，分别记录候选体构建、原生 Cull、DrawRenderers 调度及 Submit。Submit 可能包含等待；它不是 GPU 时间、纯 CPU 指令时间、整帧时间或 Player 实测。候选体构建时间已包含于 Setup，不能重复相加。Profiler 同时保留 `MaxSRP.Shadow.*` 标记。性能采样关闭几何读回诊断，GPU 深度一致性检查单独运行。

`MaxSRP > Validation > Shadow Caster XY Geometry` 校验生产包围盒的滤波支持范围、Z 平面、擦边 AABB、异常回退及生产接收角点。GPU A/B 检查使用独立凸包采样掩码，仅要求接收域及过滤支持内的深度一致；未被查询区域的 atlas 深度差异是预期行为，不以整张 atlas 完全相同作为通过标准。

2026-11-14，在当前 `BistroTOD` 主相机初始位置、1920×1080、TOD 12:00、PC 配置、Unity 2022.3.62f2c1 / D3D11 / RTX 3080 Ti 上完成一次八块 A/B。每种模式各 320 个有效样本，均无回退；几何校验 145 个体积、368640 个支持点、2304 个生产角点及 5 个回退检查通过。GPU A/A 稳定，对四级共 2780014 个保守采样域纹素，A/B 原始深度完全相同。

| 阴影 pass 主线程指标 | A 原盒 | B 紧致 XY |
|---|---:|---:|
| 总经过时间均值 | 2.0561 ms | 1.0935 ms |
| 总经过时间中位数 | 2.0152 ms | 1.0718 ms |
| 总经过时间 P95 | 2.3909 ms | 1.2036 ms |
| Cull 均值 | 0.4400 ms | 0.3095 ms |
| Submit 均值，含可能的等待 | 1.4435 ms | 0.5952 ms |
| 新包围体计算均值，已计入总时间 | 0 | 0.0400 ms |

该视角的总经过时间均值减少约 0.9626 ms（46.82%）；这是 Editor 目标阴影 pass 的结果，不是整帧加速比例或 Player 结论。原始数据：`OfflineBaseline/ShadowCasterCulling/20261114-091839-109/report.json`、同目录 `samples.csv` / `geometry-validation.json`；GPU 报告：`OfflineBaseline/ShadowCasterCulling/20261114-091754-gpu/depth-comparison.json`。其他视角、TOD、动态物体与目标设备需要分别评估，不能把该相机结果外推为全场景保证。

## TOD 远级短期复用原型

用户纠正目标后，已撤回阴影合批代码、配置和菜单。`ShortTermReuse` 是 TOD 短期复用原型，独立于永久启用的 XY 剔除。`MaxSRP > Shadow Reuse > Controls` 提供 A 四级逐帧更新、B 远级短期复用、清空缓存及临时 TOD 预览。预览默认停止，停止、关闭、保存场景或重载时恢复原 Hour，不保存场景。

级联更新参数统一在阴影配置 `DefaultShadow` 的 `PC Profile` / `Mobile Profile` 各自的 `Cascade Updates` 中设置，两档独立保存；新建配置与当前默认资产两档均默认启用 B。默认值同当前 B：近两级实时、远两级最大 4 / 8 帧、0.5 texel 太阳误差门槛及 32 texel 接收覆盖余量。实际使用哪档仍只由管线的 PC/Mobile 总控选择。Editor A/B 是临时诊断覆盖，`Use Configuration` 恢复读取当前档的配置；正常状态默认跟随配置。

B 中近两级始终实时，远级最大间隔分别为 4 / 8 次本相机渲染，周期刷新只累计本相机的渲染帧，不叠加秒数过期门槛。接收域完整覆盖及太阳误差检查仍可提前刷新。默认 32 个阴影纹素的接收域余量参与 caster XY 剔除；复用检查的是已画完整的接收区域，而非仅检查 UV 未越界。深度、旧 VP / View、投影宽度和深度范围作为同一快照绑定；太阳只允许在误差门槛内滞后，不能用新矩阵解释旧深度。

每相机独立持有 atlas，避免 Game / Scene 相互覆盖。A 每帧完整清一次；B 首帧或失效时完整清，随后仅对更新 tile（含保护区）用 SV_Depth 全屏三角形可靠清理，保留未更新 tile。RT 丢失、输出 / 投影 / 光源 / 模式变化、超过两秒缺席、显式 `ResetTemporalHistory(camera)` 及 Editor 场景变更均失效。销毁相机或超过 120 秒未使用后回收；4096²、当前 D3D11 32-bit depth 约每活跃相机 64 MiB。

太阳误差采用 `depthRange * tan(deltaSun) / (worldTexel * max(abs(sunHeight), .1))` 的保守量级估计，默认门槛 0.5 texel；它不是任意斜面、遮挡显露或薄物体下的严格可见性保证。低于约 2.9 度太阳高度时禁用复用；TOD 加速或时间跳变可使远级回到逐帧更新。首版为整级短期复用，尚未拆分静态 / 动态深度：Player 动态投影物会随远级低频更新，最多受上述帧数上限限制。共享 TAA 不能保证修复这种滞后，也未增加第二套阴影颜色历史。

CPU A/B 仍采用 ABBA BAAB 八块、每块 20 次预热加 80 次记录，每块开始清空缓存。GPU A/A 与固定太阳、固定视角的 A/B 对照要求原始深度完全一致，且 B 捕获帧必须实际复用至少一级。报告输出到 `OfflineBaseline/ShadowReuse/`。源几何、材质和场景的 Editor 修改会失效；Player 中需按场景生命周期或自定义动画系统调用失效入口。

2026-11-14 原型验证：27 项 policy 检查通过；实际生命周期 7 项通过，包含 TOD 跳变、相机移出/移回、显式失效和 atlas 丢失重建，测试后相机/Hour/场景 clean 状态均恢复。固定太阳的 GPU A/B 捕获帧实际复用 2 级，2780014 个保守采样域纹素的原始深度完全一致。30 分钟一昼夜的 4 秒临时 TOD 预览采集 821 次目标相机渲染、复用 1199 级，停止后恢复 Hour=12。

同一初始主相机、1920×1080、TOD=12 的八块 CPU A/B 各 320 个有效样本：A 总主线程经过时间均值 1.4023 ms，B 1.1606 ms，减少约 17.24%；每帧平均重画级联 4 → 2.375。此数据是 Editor 阴影 pass（含提交等待），并非整帧 / Player 加速比例，也不能把静止太阳结果外推到快进 TOD。CPU 报告 `OfflineBaseline/ShadowReuse/20261114-100209-063/report.json`；GPU 报告 `20261114-095942-gpu/depth-comparison.json`；生命周期报告 `20261114-101500-828-lifecycle/lifecycle.json`。Player / Editor API 编译和包含 tile-clear 的 17 个模块 Shader 引用保留检查通过，尚未进行实际 Player 构建或移动 GPU 性能验收。

## 验证

- `Tools/ValidateShaders.ps1`：生产 Lighting、caster、Editor GPU 探针及不同深度/法线变体。
- `Tools/ValidateSceneShadows.ps1`：分布参数 1/2/3/4，透视、正交、非对称投影、负 near、重叠/kernel 覆盖及稳定纹素网格。
- `Tools/ValidateDirectPcss.ps1`：提取生产过滤公式，在普通/反向 Z 下验证预算、接触硬化、能量端点与接收平面校正。
- `Tools/ValidateShadowCameraIsolation.ps1`：相机隔离、单次完整清屏、各级 viewport、失败路径与释放。
- Unity MCP 执行 `Tools/UnityMcp/ValidateShadowAtlas.cs.txt`：实际 atlas/viewport、丢失/销毁恢复、距离分布，以及对立深度 tile 的 72 组 PCSS/PCF 边界采样。
- Unity MCP 执行 `Tools/UnityMcp/ValidateShadowAtlasProjection.cs.txt`：预览场景四个独立 caster，每级阴影内/外点验证真实栅格到采样坐标，防止只通过纹理边界测试却采错级联。
- `Tools/UnityMcp/CaptureShadowAtlas.cs.txt`：当前 Scene/Game 同帧阴影、级联/混合区/米每 texel 和 atlas 导出。

结果存放在 git 忽略的 `OfflineBaseline/ShadowAtlas`。当前 GPU 验证平台为 Unity 2022.3/D3D11；尚无原生移动 GPU 性能或功耗结论。

本次验证记录：生产 HLSL 编译入口及相关变体全部通过；普通/反向 Z 的 18 组 PCSS 预算与接触硬化检查通过；4608 个视锥角点、18432 次覆盖、41472 次重叠/kernel、4608 次旋转/平移网格检查通过。GPU 的 72 组跨 tile、8 个实际投影内外点与 9 组接收平面检查通过，四次真实 Inspector tile 分辨率切换均正确清理旧相机，没有新增 Console 错误。Scene/Game 已完成同帧导出与有限值检查；`hdr_preview` 只是线性 HDR 加曝光/gamma 的诊断预览，不是 Editor 最终显示截图。

## 阶段状态

2026-10-22，用户确认阴影画面暂时收尾。2026-11-14 固定启用已验收的 XY caster 剔除，并按用户要求新增 TOD 远级短期复用 A/B 原型；更完整的静动态分层、地形缓存和移动真机性能仍待设计 / 验证。UE 5.3 移动端场景与过滤差异见 `Tools/UnrealBaseline/README.md`。
