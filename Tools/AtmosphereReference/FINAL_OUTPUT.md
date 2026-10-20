# 天空残余色阶：实际输出与 Editor 链路排查（2026-10-19）

**定位并修正了最终 dithering 的 RGB 相关性缺陷。当前 1:1 Scene 视图的后续合成没有额外改变天空像素；未把 Game 视图缩小过滤当成本次 Scene 截图的原因。**

仅修正 RGB 相关性后，用户仍反馈可见 banding，因此继续检查后续量化。Unity MCP 已接通并通过后台编译、重连和天空 GPU 诊断验证。

**10 月 20 日后续进展：** 已定位到 GPU sRGB8 量化阈值带来的颜色相关偏差，并在正式输出加入明确的 SDR 色阶选择。同一份真实天空 HDR 的 GPU 回放中，连续环状误差减弱，低频绝对误差 P95 从约 0.07 降到 0.023 个显示色阶。详见 [输出量化对照](OUTPUT_TRANSFER.md)。下文保留 10 月 19 日捕获的结论和边界；其旧捕获不能直接通过当前 Shader SHA256 校验。

**最终验收（10 月 20 日）：用户确认当前 demo 的“banding 几乎不可见了”，本轮可见环带问题已按实际场景验收关闭。**

## 实际抓取到的路径

Unity `2022.3.62f2c1`、Direct3D11、Linear 色彩空间、SkyboxScene。最终有效捕获为 `2026-10-19T14:29:05.5983382Z`。

```text
SceneColorHDR：1397×934，R16G16B16A16_SFloat，Point
  → EV100 15 / ACES 拟合曲线 / 显示空间 dithering
SceneView RT：1397×934，R16G16B16A16_SFloat
  → Editor 的 HDR→LDR→HDR clamp
LDRSceneTarget：1397×934，R8G8B8A8_SRGB
  → Scene GUI 绘制到窗口
屏幕截图：SDR 8 位，当前视图按 1:1 呈现
```

在生产 Color Output 绘制之后、Editor 后处理之前，只用 `CommandBuffer.CopyTexture` 快照实际 HDR 输入和实际相机目标；没有用另一个模拟场景代替。Editor 的 `LDRSceneTarget` 在释放回临时池后、尚未复用时单独拷贝。浮点分析读回经过私有 FP32 目标显式采样，以正确解码 sRGB；另保留原始目标的直接读回。

随后等待非阻塞的 0.25 秒呈现间隔，先抓屏幕，再导出大量浮点文件。初次重测曾在窗口 Present 前读到上一帧，并因导出耗时拍到 Unity 等待面板；那份截图不用于最终结论。现在的数据通过同帧像素配准校验，分析器会拒绝不匹配的屏幕/RT 对照。

排除工具栏、方向 gizmo、Navigation UI 和太阳盘边缘后，对 **537076 个天空像素**进行配准。只平移 `[y=45, x=0]` 去除窗口工具栏偏移，**没有缩放或颜色拟合**：

- 屏幕 RGB 与实际 `LDRSceneTarget`：**100% 像素完全相同**，MAE / P99 差值均为 0 个色阶。
- 因此本次当前 Scene 视图的后续 GUI 合成没有再抹掉 dithering，也没有额外制造可测量的色阶。
- FP16 Color Output → LDR 中间目标仍有预期的 8 位量化，平均绝对变化约 **0.253 个显示色阶**。这是最终量化需要 dithering 的位置，不是大气能量公式错误。

该结论针对上述天空 ROI，不能把 100% 相同推广到 gizmo、遮挡的 UI 或所有 Editor 显示模式。

## 修正的具体缺陷

之前三个 RGB 通道共用同一个三角分布噪声。它可以使单个通道的量化误差较均匀，但量化后的通道误差高度相关，**色差误差的强弱仍会随着渐变颜色周期性变化**。单通道检查通过，并不代表 RGB 联合误差也消除了这种变化。

用相同三角分布、相同 ±1 显示色阶幅度、相同输入颜色，单独消融通道相关性：

| 量化模型 | 蓝－红量化误差方差随颜色变化的范围（色阶²） |
| --- | --- |
| RGB 共用噪声 | 0 ～ 0.2500 |
| 独立 RGB 噪声 | 0.4980 ～ 0.5029 |

新方案并不是把色差噪声总量压低，而是让其强弱不再按渐变颜色成圈变化。代价是极细的彩色颗粒；每个通道仍限制在原来的 ±1 个显示色阶。

实际 GPU Color Output 读回也验证了实现：

| 项目 | 原方案 | 修正后 |
| --- | --- | --- |
| RGB 通道噪声相关系数（非对角项） | 0.99950 ～ 0.99981 | −0.000290 ～ 0.000064 |
| 各通道噪声标准差 | 约 0.408 个色阶 | 约 0.408 个色阶 |

正式 `ColorOutputPass.hlsl` 已改为基于整数哈希的独立 RGB 序列，仍使用三角分布、固定屏幕像素坐标以及精确 sRGB 编解码。没有提高噪声幅度，没有引入逐帧变化；整数哈希也避免沿用 IGN 的规则对角空间结构。相关性消融只改变通道相关性，不把空间噪声谱变化的收益混算为该实验结果。

这证明了可复现的数值缺陷及其修正，不宣称任何显示器、任何缩放倍率下所有微弱轮廓都已主观不可见。

## 缩放路径的独立结论

核对 [Unity 2022.3 SceneView 源码](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/SceneView/SceneView.cs)：SDR 模式会创建 LDRSceneTarget 往返，并再绘制到窗口；本轮实际纹理格式和像素对照验证了该路径。

[GameView 源码](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/GameView/GameView.cs) 在 `m_ZoomArea.scale.y < 1` 时启用 Bilinear，其他情况下使用 Point。[PlayModeView 源码](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/PlayModeView/PlayModeView.cs) 记录了其目标格式创建方式。

受控的 2×2 平均测试中，当前独立三角噪声标准差从约 0.408 降至 0.204 个色阶。因此缩小显示确实可能削弱 dithering，且原生像素抗色阶不保证经再次缩小、再次量化后仍同样有效。**这部分是源码和数值对照，不是实际 GameView 的抓取**；捕获时 Game 标签隐藏，没有擅自切换用户标签或修改 zoom/aspect。当前 Scene 是 1:1，不能用该缩放机制解释本轮 Scene 残余轮廓。

## 复现、产物与边界

Unity 菜单：`MaxSRP → Diagnostics → Capture Final Output And Editor`。保持 Scene 天空可见、窗口无遮挡、相机不动。也可先创建 `OfflineBaseline/FinalOutput/request.txt`，下次脚本刷新后执行。一次性 Editor 钩子在完成、失败超时或程序集重载时解除；Player 不包含该钩子。

```powershell
python Tools/AtmosphereReference/analyze_final_output.py
```

- `OfflineBaseline/FinalOutput/capture.json`：当前实际目标格式、尺寸、参数、生产输出 Shader SHA256。
- `analysis.json`：实际 GPU 噪声相关性、LDR/屏幕一致性和适用范围。
- `scene_output.png` / `scene_ldr.png` / `scene_screen.png`：对应阶段。
- `matched_shared.png` / `matched_independent.png`：基于**同一份当前实际 HDR**、相同三角噪声分布的 CPU 消融图片。
- `dither_correlation.png`：色差误差方差与实际 GPU 相关系数图。
- `Before/`：旧共用 RGB 噪声的实际捕获。

旧捕获为 1397×1088，新捕获为 1397×934，不能将两张屏幕图直接相减作为质量评分。实际统计分别与各自 HDR 输入比较；同输入消融图片另外生成。曝光、大气参数、LUT 分辨率/积分步数、IBL 和 GI 均未随本次输出修正调整。未做 Player、HDR 显示器、实际 GameView 缩小或连续旋转的完整实测。
