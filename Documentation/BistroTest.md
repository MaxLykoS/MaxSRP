# Bistro 室外 Clustered Lighting 测试场景

场景：`Assets/Scenes/BistroTest/BistroTest.unity`。这是单独的新场景，原有 `SkyboxScene.unity` 保持不变。Game 相机复用原 FBX 视角，右键＋WASD 飞行控制沿用项目现有 `SimpleCameraController`。

## 本地资源与版本控制

来源为 Amazon Lumberyard Bistro v5.2 / NVIDIA ORCA，作者 Amazon Lumberyard，2017。资源页：<https://developer.nvidia.com/orca/amazon-lumberyard-bistro>。许可为 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)，完整许可保留在 `Documentation/Bistro-CC-BY-4.0.txt`。

本工程进行了 DDS 解码、BC5 法线 Z 重建、Unity 材质适配、透明玻璃隐藏、灯光与场景重配；不声称还原 Falcor 的原始渲染。

- `Assets/ThirdParty/Bistro/`：本地模型、原 DDS、转换 PNG、原包说明，以及生成的小型回退贴图。**资源本体不提交，只提交所有层级的 `.meta`**。
- `Assets/Scenes/BistroTest/`：本工程编写的场景、材质、Shader 和灯光配置；这些文件和 `.meta` 正常提交。
- `.gitignore` 保留目录遍历和 `.meta` 的例外；`AGENTS.md` 明确禁止用 `git add -f` 加入 Bistro 资源本体。
- 新 checkout 只有 `.meta`，还需要从相同版本的本地 ZIP 恢复资源；不要删除或重新生成已经提交的 `.meta`。

恢复命令（Python 3 + Pillow + NumPy）：

```powershell
python Tools/Bistro/prepare_bistro.py C:/Users/pc/Downloads/Bistro_v5_2.zip
```

脚本只提取室外 FBX 及其引用贴图，不导入两个室内模型或原 HDRI。保留全部既有 `.meta`；相同源文件和已转换贴图不重复写入。恢复后让 Unity 刷新即可，已提交的场景和材质不必重建。只有需要重新生成配置时才运行菜单 `MaxSRP > Bistro > Build Separate Test Scene`，该菜单会重置新测试场景的手工编辑，因此要求所有当前场景先保存。

室外 FBX SHA-256：`471991629c7e35a88338c48f3565390ad19cc5d1d7c7e067fc2a60b4f3b5f195`。

提交前可检查：

```powershell
git ls-files --cached -- Assets/ThirdParty/Bistro
git status --short --untracked-files=all -- Assets/ThirdParty/Bistro
```

两者在 Bistro 依赖目录内只能列出 `.meta`；场景、材质位于另外的受跟踪目录。忽略规则不影响本地运行和构建资源引用。

## 渲染配置

场景根对象的 `MaxSceneExposure` 使用手动曝光 EV100 10.5，仅覆盖最终 Color Output 的曝光；沿用现有主管线，原主管线的 EV100 15、GraphicsSettings 和 QualitySettings 不修改。Game 相机按所在场景选择覆盖，Scene 视图按活动场景选择；离开该场景或禁用组件即使用默认曝光。Preview / Reflection 相机旁路，不影响既有诊断基线。曝光在 TAA 之后应用，保持 HDR 历史约定。

- 一盏物理太阳，30000 lux，大气外输入，仰角约 8°；保留程序大气、动态天空 IBL、CSM 和公共 TAA。
- 保留模型导入后的根缩放；不再额外乘一次厘米转换。
- `Practical Lights (Showcase)` 默认 53 盏点光，使用发光子网格的世界包围盒中心布灯，1m 内去重；光强 3000 / 6000 lm，范围 7 / 9m。长灯串没有被错误地替换为位于整串中心的一盏灯。
- 压力组 `Stress Tests (Enable One Child Only)` 下提供 **额外 128 / 256 / 512 盏点光**，默认均关闭；一次启用一个子组。展示灯光默认仍然存在，需要纯固定数量对照时先关闭 Practical 组。
- 压力灯为固定的两层空间网格，1800 lm、范围 8m。这是人工工作负载，不代表原场景实际灯具；配置数量、视锥内可见数量和单 cluster 灯数不同。

## 材质适配与边界

源 `Specular` 的 **R=AO、G=粗糙度、B=金属度**，并非传统高光颜色。Bistro Shader 直接读取这三个通道，避免把 roughness 当成 sRGB 或误读 R。

DDS 在 Unity 中由 `IHVImageFormatImporter` 导入，不能直接使用所需的 NormalImporter 转换。恢复工具先解码 PNG；ATI2/BC5 法线只存 XY，需要重建 Z，再由 NormalImporter 翻转 DirectX 法线的 G 通道。法线为 Linear / BC5，Mask 为 Linear / BC7，BaseColor 和 Emissive 为 sRGB / BC7，Standalone 最大 2048，保留 MIP。这个配置是桌面基线，尚未针对手机压缩与预算优化。

独立 `MaxSRP/Bistro GBuffer` 提供叶片双面与 alpha clip，GBuffer、ShadowCaster、MotionVectors 三个通道使用同一裁剪规则，并复用生产运动向量函数。没有新增时间解析或历史。发光值按 nits / HDRScale 写入既有 GBuffer3，在 Lighting 中、雾之前加入。

目前缺少透明与折射路径，所以纯玻璃子网格在三种通道中均裁掉，保留窗框和其他实体；这不等同于玻璃渲染。AO 通道保留但当前 Lighting 不消费；不为这次场景接入 GI、接触阴影或反射探针。点光没有局部阴影，因此强光可能穿墙，发光面也不会通过 GI 自动照亮环境。性能或质量对照必须遵守这些相同边界。

## 已完成验证（2026-10-23，Unity 2022.3 / D3D11）

- 实际场景：1296 个 MeshRenderer、132 个完整材质绑定、2,832,120 三角形；普通场景和主管线曝光保持原值。
- 1280×720 最终渲染导出成功；法线 NormalImporter / 翻 G、Mask Linear 设置逐材质检查通过。浮点读回后显式编码 PNG，避免直接 8-bit ReadPixels 的格式读回问题造成假黑图。
- Game / SceneView 曝光范围、组件启停、活动场景切换验证通过；GraphicsSettings、每级 Quality 的管线引用均未改变。
- 默认展示视角可见 46 个点光；分别开启额外 128 / 256 / 512 压力组后，可见数为 111 / 179 / 311。320×180 GPU 输出非空、RGB 有限；测试结束恢复默认关闭三个压力组。这些是正确性检查，**不是 GPU 耗时或手机性能结论**。
- Player / Editor API 编译、生产 Shader 与 Bistro GBuffer / Shadow / Motion 入口编译通过。
- 恢复脚本再次运行不会改写相同 FBX / PNG，回退贴图也可恢复；819 个 Bistro 依赖条目只有 `.meta` 可被 Git 收录，资源本体可收录数为 0，缺失 `.meta` 数为 0。

可复测 `Tools/UnityMcp/ValidateBistroScene.cs.txt`、`ValidateBistroExposure.cs.txt`、`ValidateBistroStress.cs.txt`；报告和预览保存在被忽略的 `OfflineBaseline/Bistro/`。
