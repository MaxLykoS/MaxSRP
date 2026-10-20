# 残余天空环带：GPU sRGB 量化阈值（2026-10-20）

**验收结果（2026-10-20）：用户在当前 demo 中确认“banding 几乎不可见了”。本轮可见环带问题按当前场景实测验收关闭。**

本轮定位并修正一处会在 dithering 之后重新形成淡环带的输出偏差。受控渐变、同一份真实天空 HDR 的 GPU 回放和用户实际观感均已验证；结论不泛化为所有显示环境下绝对无 banding。

## 原因与修正

此前独立 RGB 三角噪声本身均值正确，但最后的 FLOAT → sRGB8 转换并不保证使用数学上精确的舍入阈值。[D3D11 规范第 3.2.3.8 节](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm#FLOATtoSRGB) 允许整数侧 0.6 ULP 的转换容差。实测偏差随输入颜色变化，在连续天空渐变中形成连贯的轮廓。全图平均误差较小，不能排除这种空间结构。

`ColorOutputPass.hlsl` 现在在 sRGB 显示空间加入原有三角噪声后，明确执行 `floor(encoded * 255 + 0.5) / 255`，再解码回线性值供目标执行 sRGB 写入。硬件接收到的是已选定色阶的中心值，后续 FP16 中间缓冲和 sRGB8 转换不再决定噪声样本应落在哪个色阶。没有额外 gamma 编码写入目标。

修正作用于已有的 sRGB SDR dithering 分支。高精度相机导出原本使用 mode 0，仍保持浮点值；线性 UNorm 分支保持原有逻辑。噪声幅度、种子与 RGB 独立性保持一致。

## 受控 GPU 渐变

菜单 `MaxSRP/Diagnostics/Compare Output Transfer`：1024×2048，共 2097152 个像素，RGB 从显示码值 `(55.125,75.375,125.625)` 各跨越 16 个色阶。冻结的旧输出 Shader 与正式新 Shader 使用相同的噪声、曝光 1 和关闭 tone mapping 的参数。比较 FP32、FP16 中间输出及实际硬件 sRGB8 写入。

| 指标 | 旧输出 RGB | 修正后 RGB |
| --- | --- | --- |
| 不同整数颜色区间的平均偏差峰峰值，显示色阶 | 0.11981 / 0.12410 / 0.12651 | 0.00812 / 0.00499 / 0.00591 |
| 全图平均偏差，显示色阶 | +0.01149 / +0.00875 / +0.01196 | +0.00008 / −0.00021 / −0.00020 |

修正后的区间波动接近有限噪声样本的统计起伏。显示结果与数学上的精确舍入相差的样本约为每百万 5～12 个，主要在 FP32 计算误差恰好跨过半色阶边界时发生。

这个实验同时发现：Unity 默认 Blit 拷贝会混入半精度行为，不适合直接充当 FP32 路径的严格基准。最终实验用返回 float4 的输出 Shader 作无 dithering 的拷贝，因此没有把拷贝精度误算为目标格式差异。

## 同一份实际天空 HDR 回放

菜单 `MaxSRP/Diagnostics/Compare Captured Sky Output`：读取先前真实相机捕获的 `OfflineBaseline/FinalOutput/sceneview_hdr.f32`，使用其原曝光与 ACES 参数；两条路径都经过 FP16 目标与硬件 sRGB8 写入。没有重新移动相机或改变天空，源文件 SHA256、正式 Shader SHA256 均记录在捕获元数据中。

- 原始相机捕获 UTC：`2026-10-19T14:29:05.5983382Z`。
- 本次 GPU 回放 UTC：`2026-10-19T16:11:54.7557837Z`（北京时间 10 月 20 日 00:11）。
- 分辨率 1397×934，天空统计 1304169 像素；排除太阳盘及高光裁切。
- 低频误差用 sigma=12 像素的高斯滤波提取，边界和太阳盘影响区排除后统计 1297403 像素。下表单位为显示色阶，数值是误差而非天空信号本身。

| 指标 | 旧输出 RGB | 修正后 RGB |
| --- | --- | --- |
| 低频绝对误差 P95 | 0.06949 / 0.06922 / 0.07123 | 0.02361 / 0.02291 / 0.02371 |
| 低频误差 RMS | 0.03504 / 0.03638 / 0.03941 | 0.01213 / 0.01184 / 0.01209 |
| 最终单像素量化误差标准差 | 0.50221 / 0.50357 / 0.50264 | 0.50019 / 0.49992 / 0.49970 |

旧输出低频误差图呈现太阳周围的连续环状结构，新输出主要剩下随机起伏。数值改善不是通过增加最终噪声方差获得的。图右侧使用 ±0.1 色阶的放大误差色标，不能把它的强烈颜色理解为正常显示时存在同等强度的色带。

## 验证与产物

- Unity C# 编译和两个诊断菜单实际执行成功；48 项离线 HLSL 入口/分支编译检查通过。
- Unity 控制台无编译错误；重新编译天空诊断 compute 时，`TransmittanceToAtmosphere` 有一条潜在未初始化变量警告。本轮没有改变其积分函数。
- Python 校验源码/输入 SHA256、渐变平均偏差、颜色区间偏差及同 HDR 低频误差改善，均通过。
- `OfflineBaseline/OutputTransfer/analysis.json`、`transfer_bias.png`：受控渐变数据及误差曲线。
- `OfflineBaseline/OutputTransfer/SkyReplay/analysis.json`、`sky_output_replay.png`：实际天空回放统计及前后对照。
- `BeforeProductionFix/` 保留正式接入前的受控测试产物。

```powershell
python Tools/AtmosphereReference/analyze_output_transfer.py
python Tools/AtmosphereReference/analyze_sky_output_replay.py
```

这次回放没有经过当前可见 Scene 窗口的再次合成、屏幕缩放或显示器。此前 1:1 Scene 的 LDR/屏幕一致性证据仍只适用于其当时的捕获。旋转时的主观残余、不同缩放比例与显示设备仍需按实际效果确认。大气输运、GI 和光照能量不由本轮输出修正改变。
