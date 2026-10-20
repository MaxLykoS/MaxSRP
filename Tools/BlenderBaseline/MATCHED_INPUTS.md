# 当前头盔 16.8% 直射差异的闭合调查（2026-10-19）

**主要差异来自两边法线、粗糙度的实际输入与采样路径不一致。共享输入后，Cycles 默认材质与 Unity 的直射均值差约 2.49%；连材质模型也统一后，均值差约 0.11%，局部亮度 L1 约 0.283%。** 当前证据不支持直射计算固定丢失 16.8% 能量，也不支持将主要差距归因于 GI 或微表面多重散射补偿。

这是诊断闭合，不是把原生 Cycles 图像改名为“修复后”的生产效果。本轮没有修改运行时、纹理导入器、材质、原场景或 Blender 文件，也没有调灯光、曝光或实现 GI。

## 使用当前数据，避免混入旧 DXT 结果

冻结 Unity **2026-10-19T10:10:30.2405501Z** 的真实 GPU 捕获：BC5 法线、BC7 粗糙度，2048²、原有 MIP；BRDF SHA256 `b7d4a1c13f0dec95b86370d41f107ede3f822d3aaf8a54d661c7d393b81f4118`。使用 `MatchedInputsBC5BC7/SourceUnityInputs/`，不使用早先 `HighlightDiagnosis/SourceUnityInputs/` 的 DXT5/DXT1 数据。

Blender 5.2.2 LTS / Cycles 构建 `d13f752e3b9c`，同相机、768²、0° Sun、黑 World、无阴影、无二次 diffuse/glossy 可见性，无雾。统计曝光前 HDR，使用原 baseline 相同的 **137913 个公共内部像素**。重渲染原生 Cycles 得到均值 **0.04681472870**，旧参考 **0.04681472819**，两者均值比约 1.000000011；确认没有通过更换参考场景制造收益。

把实际 Unity GBuffer 的世界法线、有效 roughness、metallic、线性 albedo 通过 Window 坐标以 Closest 投射到 Cycles。由 GPU 已完成的纹理压缩解码、MIP、缩放、切线变换与 GBuffer 量化形成的值，直接成为两边共同输入。投射输出的 albedo、编码法线 MAE 分别约 **1.14e-6 / 1.82e-6**，roughness/metallic 约 **4.75e-7 / 1.27e-6**，验证输入确实共享。

Cycles 原生过滤为 Blackman-Harris 1.5，受控点采样过滤宽度 0.01。窄过滤只用于与 Unity 单次像素采样对齐，未作为生产抗锯齿方案。对共享输入再做 64/512/2048 samples 收敛测试。

## 直射均值差如何变化

Unity 无阴影直射均值始终为 **0.03895182691**。下表比值固定为 Unity/Cycles，未改变 Unity 捕获。

| 对照条件 | Cycles 均值 | Unity/Cycles | Unity 偏低 |
| --- | ---: | ---: | ---: |
| 原生输入，默认材质与过滤 | 0.046814729 | 0.832042 | 16.796% |
| 原生输入，只缩窄过滤 | 0.047727582 | 0.816128 | 18.387% |
| 单次 GGX，仅统一法线，窄过滤 | 0.045060094 | 0.864442 | 13.556% |
| 单次 GGX，统一法线与粗糙度，窄过滤 | 0.039602152 | 0.983579 | **1.642%** |
| 全部共享输入，单次 Principled GGX，2048 samples | 0.039912934 | 0.975920 | **2.408%** |
| 全部共享输入，默认 Principled MULTI_GGX，2048 samples | 0.039946747 | 0.975094 | **2.491%** |
| 全部共享输入，工程同款 GGX + Schlick + Lambert，2048 samples | 0.038994851 | 0.998897 | **0.110%** |

统一 metallic/albedo 后均值差略有增加，说明多个输入差异原先存在抵消，不能假设每次统一都会单调改善平均值。只缩窄原生过滤还会恶化差距，所以不能把抗锯齿整体关闭当作修复。

局部 L1 统一用 `sum(abs(Y_test-Y_Unity))/sum(Y_Unity)`：原生 Cycles **67.55%**；统一 N/R **5.03%**；全部共享输入、单次 GGX **3.00%**；同款 BRDF、2048 samples **0.283%**。旧报告的 56.20% 使用 Cycles 为分母，与这里原生 67.55% 是同一对图像的不同归一化。L1 不是平均能量缺失比例。

## 法线和粗糙度分别有多大影响

增加 N/R 的完整 2×2 对照，所有组使用单次 GGX、窄过滤，albedo/metallic 均保留原生输入：

| 法线 | 粗糙度 | Cycles 平均亮度 |
| --- | --- | ---: |
| 原生 | 原生 | 0.047682008 |
| Unity | 原生 | 0.045060094 |
| 原生 | Unity | 0.043673619 |
| Unity | Unity | 0.039602152 |

按“先 N 后 R / 先 R 后 N”两种替换顺序取平均，法线造成的均值下降为 **0.003346691**，相当于原始 Cycles 均值的 **7.149 个百分点**；粗糙度为 **0.004733166**，相当于 **10.110 个百分点**。二者合计 17.259 个百分点；过滤、颜色、金属度和材质模型有正负抵消，最终原始差距是 16.796%。这些是该捕获的受控输入效应，不是适用于所有模型的误差占比。

当前原生 Cycles 对 Unity 的法线夹角中位数 **0.0702°**、P90 **0.9094°**；有效 roughness MAE **0.01725**，metallic MAE **0.03154**。即使 BC5 已改善压缩，高光仍会被较小法线误差显著移动；低粗糙度 GGX 对粗糙度变化也十分敏感。

已知输入路径缺口仍存在：R/M 源图 4096²，Unity 导入为 2048²；Unity 使用自己的缩放、压缩和 MIP/像素导数，Cycles 原生路径使用原始贴图和自己的纹理过滤。此次共享 GBuffer 绕过这些差异并验证其总体影响，**没有进一步声称这 10.110 个百分点全部来自某一个 codec、分辨率或 MIP 环节**。贴图绑定和原有 sRGB 标记保持原值。

## 剩余模型差异与采样收敛

共享输入后，Cycles MULTI_GGX 相对单次 GGX 仅高 **0.0847%**，仍不足以解释原始 16.8%。

在同款诊断 BRDF 上，只将非金属镜面 Fresnel 换为精确介电形式，均值增加约 **0.783%**；单独改变下层 diffuse 的金属混合权重也产生明显变化。精确 Fresnel 与分离 diffuse 权重一起改，并不能完整复制 Principled 的分层衰减，仍需要区分其镜面层反射 albedo 权重。完整结果记录在 JSON 的 `material_model_tests` 中；这些均为离线诊断材质，没有替换生产公式。

512 → 2048 samples：共享 Principled 的均值变化约 **0.0084%**、局部 L1 约 **0.0135%**；共享同款 BRDF 均值变化 **0.0085%**。同款 BRDF 的 64 → 2048 均值变化 **0.0061%**。这足以排除当前共享输入测试中约 16.8% 量级的未收敛偏差；不代表原生 4K 纹理路径也已完成全部采样收敛验证。

共享输入、同款 BRDF 的最终约 0.11% 均值残差仍存在，没有宣称逐像素完全相同。NumPy 双精度的独立像素中心 BRDF 副本对实际 GPU 均值比 **1.001147**，局部 L1 **0.1645%**，支持实际 GPU 没有额外的固定亮度缩放。浮点精度、微小射线/法线处理差异和少数极窄高光尚未逐像素穷尽；不能仅凭小均值差声称所有高光像素一致。

## 复现与产物

先将当前 `unity_helmet.json`、`unity_direct_unshadowed.f32`、三个 `unity_gbuffer*.f32`、原 `blender_direct_delta_unshadowed.f32`、`blender_combined_matched.f32`、`comparison.json`、`sampling_quality_applied.json` 冻结到 `MatchedInputsBC5BC7/SourceUnityInputs/`。本轮快照已保存，不应覆盖成另一轮捕获。分析器校验 SHA256、原始均值与像素掩码，并拒绝混用变化后的生产 BRDF。

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/HelmetOnly/HelmetOnly_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/diagnose_highlights.py -- --input-directory D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7/SourceUnityInputs --output-directory D:/UnityProject/MaxSRP/OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7 --convergence
python Tools/BlenderBaseline/analyze_matched_inputs.py
```

产物位于 `OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7/`：`matched_inputs_report.json`、`matched_inputs_comparison.png`、各对照 EXR/f32 和 `render_info.json`。分析器断言投射精度、原 baseline 复现、贡献加和闭合、采样收敛及 CPU/GPU 均值一致性，本轮均通过。
