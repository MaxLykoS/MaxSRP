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

每个相机开始只清整张 atlas 一次，逐级绘制只改变 viewport。局部光空间 VP 保留原约定，采样时才应用 tile scale/offset；Stable CSM 包围球、padding、snapping 和世界空间纹素尺寸均使用 tile 的有效分辨率。viewport tile 偏移与纹理地址采用相同数值坐标，不能根据 `graphicsUVStartsAtTop` 再翻转 atlas 行；局部投影 Y 已由 `ShadowClipToUV` 处理。

所有 PCSS 搜索和比较采样均限制在本级首尾 texel 中心之间，再变换到 atlas UV。接收面逐样本深度修正使用限制后的实际偏移。保护区不能替代按 tile 约束采样；当前已有保守视锥/kernel padding，正常接收区域不应依赖边缘重复采样。

## 距离和过渡

`DistributionExponent` 表示相邻区间长度的比值，采用 UE 5.3 `ComputeAccumulatedScale` 同类的等比累积方式，而非对归一化距离直接取幂。默认 2，四段权重为 1:2:4:8。每级终点为相机 near 加上累计权重乘以 `min(ShadowDistance, camera.far) - near`。

500 米总范围下，忽略 near 后，默认终点约为 33.3/100/233.3/500 米。参数 3 对应 12.5/50/162.5/500 米，更侧重近景，但必须评估跨级精度落差。旧固定 7/13/25/55 权重与单独的首级上限已被替代。

`CascadeBlendFraction` 当前为 0.1，按相邻较短区间计算重叠带，远级拟合包含该重叠区域；在边界前平滑混合两级可见度。`DistanceFadeFraction` 当前为 0.1，在最后一个区间末尾淡出。`PcssCascadeCount` 默认 2，交界同样混合 PCSS/PCF 可见度。远级固定 PCF 不再承诺物理接触硬化，不能把它误认为完整 PCSS 的等价低成本实现。

每级实际密度为 `_OrthoWidthN / _ShadowResolutionN` 米/texel。距离、分辨率、bias 与过滤宽度需一起评估；单纯加宽过渡不能恢复缺失的轮廓，也不能消除所有软硬差异。保留现有世界单位 bias，不借 atlas 改动擅自重写场景调参。

CSM 始终使用无 jitter 矩阵；沿用共享整帧 TAA，不增加阴影历史。Inspector 修改会通过资产验证重建管线与时间历史。资源丢失/销毁后重建并重新绑定 atlas；隐藏相机仍经延迟销毁路径释放。

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

2026-10-22，用户确认当前阴影画面暂时收尾。Shadow cache 留到地形系统引入后再设计；本版仍逐相机更新四级 caster，不宣称缓存或移动真机性能已完成。UE 5.3 移动端场景与过滤差异见 `Tools/UnrealBaseline/README.md`。
