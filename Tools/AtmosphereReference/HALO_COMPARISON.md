# 太阳光晕环带：采样对照（2026-10-19）

这轮完成四组实际 GPU 对照，没有改动正式天空的 LUT、积分、曝光、IBL 或 GI。不能把提高采样后的亮度增加，当成环带已经修复。

后续 [最终输出排查](FINAL_OUTPUT.md) 修改了 dithering 的通道相关性。下文原始数据对应修正前的输出 Shader；其输入/源码校验会阻止用新输出源码静默重算旧对照图。接通 Unity MCP 后已另做当前源码的复测，见文末；原始捕获已备份。

## 捕获条件

- `2026-10-19T14:04:33.9983102Z`，SkyboxScene，Direct3D11。
- SceneCamera 高度 17.2754 m，太阳高度约 69.811°。
- 围绕太阳方向渲染 512×512、40° 的相同视野，不是四次手动旋转相机截图。
- 四组共享当前生产透射与多次散射 LUT；第 1 组由生产 Skyview Shader 私有烘焙同高度的 256×128 LUT，第 2 组直接调用生产 32 步积分，第 3/4 组仅改变诊断积分中的分段数及分布。
- 显示图共同包含生产太阳盘函数、FP16 场景缓冲舍入、EV100 15、ACES 拟合曲线、当前三角分布 dithering，以及 8 位输出量化。
- 另存太阳盘之外的原始 FP32 天空辐亮度，以及 dithering/8 位量化前的显示编码浮点值。

## 数据结果

统计范围为距太阳 `1.5°～18°`、高于地平线约 1.15° 的 163028 个像素。下面的显示色阶误差是量化前的 RGB 绝对差，不是原始 HDR 单位。

| 对照 | 天空亮度比 A/B | 显示色阶绝对差：均值 / P95 / 最大 |
| --- | --- | --- |
| LUT / 逐像素 32 步 | 0.999840 | 0.02075 / 0.07540 / 0.22511 |
| 逐像素 32 / 128 步（均匀） | 0.967688 | 1.63435 / 3.22837 / 4.13523 |
| 128 步均匀 / 128 步平方分布 | 0.997933 | 0.10347 / 0.21238 / 0.31026 |

各组天空从 FP32 舍入至 FP16 后，映射到最终显示的最大变化均小于 **0.030 个色阶**。当前参数下，改成 FP32 场景色缓冲也缺乏收益依据。

这组结果支持：

1. 当前太阳附近，256×128 LUT 的重建误差较小；差值放大图能看到细小结构，但不能据此把肉眼环带直接归因于 LUT。绕过 LUT 的显示图与原图接近。
2. 固定 32 步存在约 3.2% 的局部光晕平均能量不足；改为 128 步后相对原结果约亮 3.34%。128 步近处加密另增加约 0.21%。这些主要表现为光晕整体轮廓的平滑亮度变化，不能证明它们就是剩余环带的来源。
3. 本轮没有证据支持为了消除当前环带而切换正式天空到逐像素 128 步。最终显示量化、Editor 合成/缩放或呈现环节仍需单独核对，尚未唯一定位用户截图的残余环带。

128 步平方分布也不是收敛真值。这是同源的采样消融试验，不替代独立大气输运参考；未测其他太阳角度、海拔、相机连续运动、实际 backbuffer、显示器或 Editor 合成路径。

## 复现与产物

Unity 菜单：`MaxSRP → Diagnostics → Compare Sky Halo Sampling`。也可先创建 `OfflineBaseline/SkyHalo/request.txt`，下次脚本刷新后自动执行；完成后删除请求文件。失败信息写入同目录 `error.txt`。

GPU 读回完成后执行：

```powershell
python Tools/AtmosphereReference/analyze_halo.py
```

`OfflineBaseline/SkyHalo/halo_comparison.png` 为四张同尺度显示图；`halo_profiles.png` 包含剖面与放大的重建差值；`analysis.json` 包含统计与适用边界，`capture.json` 记录输入及源码 SHA256。分析器检查源码与捕获时一致，拒绝静默混用新源码和旧数据。

方法依据：[Epic 天空大气文档](https://dev.epicgames.com/documentation/en-us/unreal-engine/sky-atmosphere-component-in-unreal-engine)、[作者公开的 LUT 映射](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyCommon.hlsl)、[作者公开的积分实现](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl)。本轮没有将论文配套代码视作当前 UE 全部源码。

## 接通 MCP 后复测（同日 23:24，UTC+8）

通过 MCP 执行现有诊断菜单，在 Unity 非前台时完成 GPU 读回。`capture.json` 的 UTC 时间为 `2026-10-19T15:24:43.9482354Z`，这次 SceneCamera 高度为 0 m，有效大气高度钳制为 1 m；使用当前独立 RGB dithering，源码 SHA256 校验通过。其他条件见该次捕获记录。

| 对照 | 天空亮度比 A/B | 显示色阶绝对差：均值 / P95 / 最大 |
| --- | --- | --- |
| LUT / 逐像素 32 步 | 0.999830 | 0.02115 / 0.07777 / 0.22596 |
| 逐像素 32 / 128 步（均匀） | 0.967387 | 1.65501 / 3.25681 / 4.16724 |
| 128 步均匀 / 128 步平方分布 | 0.997968 | 0.10132 / 0.21734 / 0.29027 |

本次最大 FP16 显示误差仍小于 0.030 个色阶，结果与先前消融结论接近。它验证了后台诊断链路，并未证明用户看到的 banding 已修复。原始 17.2754 m 捕获保存在 `OfflineBaseline/SkyHalo/BeforeMcp_20261019_2318/`；当前根目录产物和 `analysis.json` 已更新为本次复测。
