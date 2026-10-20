# 使用 Unity 实际导入纹理的原生 UV baseline（2026-10-19）

新场景：`OfflineBaseline/HelmetOnly/ImportedTextures/HelmetOnly_ImportedTextures_Cycles.blend`。

这个场景直接使用模型 UV、切线空间法线和实际导入纹理，不使用 Window 投射的世界法线/粗糙度图。四张纹理已打包，可以独立打开后按 F12。默认画面为太阳 + 原天空环境，无地面、无其他模型、无场景 GI，使用原 EV100/ACES 显示链路。直射数值测试另外关闭 World，排除天空与阴影。

## 对齐了什么

Unity 实际 GPU 导出时间 **2026-10-19T12:44:36Z**，Direct3D11：

| 贴图 | 实际导入格式 | 尺寸 | MIP 数 | 数据色彩空间 |
| --- | --- | --- | ---: | --- |
| Albedo | DXT1 | 2048² | 12 | sRGB |
| Normal | BC5 | 2048² | 12 | Linear |
| Roughness | BC7 | 2048² | 12 | sRGB，沿用原设置 |
| Metallic | DXT1 | 2048² | 12 | sRGB，沿用原设置 |

新增仅 Editor 使用的 compute shader，以 `Texture.Load` 从实际绑定的压缩资源逐 texel 读取每一级 MIP，导出 **48 个 32 位线性 RGBA EXR**。因此包含 Unity 的缩放、BC 解码、各级实际 MIP 与 sRGB 解码结果；不由 Pillow 或 Blender 重新估算 Unity importer 的输出，不开启资源 Read/Write。记录源文件、importer 与导出数据 SHA256，复测时检查没有混入其他版本。

Blender 将这些 EXR 标记为 Non-Color，不重复做 sRGB 解码；以模型原生 UV 双线性采样。法线在采样 XY 后重建 Z，再使用切线空间 Normal Map 节点；不直接使用原始 JPEG 的 Z，也不重复翻转已导入的绿色通道。再模拟本管线 GBuffer 的线性 albedo 8 位、世界法线 10 位、R/M 16 位量化与 0.05 roughness 下限。

## 过滤约定与边界

本轮采用明确的共同约定：**双方 MIP0、双线性采样、aniso=0**。Unity 仅在独立预览场景临时克隆四张压缩纹理、设置负 MIP bias，测试后销毁，不修改原贴图或生产 MIP 策略。

导出了所有 MIP，不代表 Blender 已复制 Unity 的隐式 LOD 选择。Cycles 有自己的 [GPU 纹理读取路径](https://github.com/blender/blender/blob/d13f752e3b9c/intern/cycles/kernel/device/gpu/image.h)。新场景是固定 MIP0 的原生 UV 参考，不可将它与仍使用自动 MIP 的 Game 画面混比后声称光照能量缺失。

相机像素过滤宽度为 0.01；为检查残余亚像素影响，还以 **5 倍奇数分辨率**渲染、只取各原像素正中心对应的子像素，使有效足迹缩至原来的 1/5。该实验没有将相邻子像素平均成抗锯齿图，也没有更改已保存 baseline 的 768² 默认分辨率。

## 实际原生 UV 对照

使用原报告相同的 **137913 个内部像素**，线性 HDR、曝光前统计。以下比值为 **Cycles/Unity**，与部分历史报告的 Unity/Cycles 方向相反：

| 条件 | Unity 均值 | Cycles 均值 | Cycles/Unity | 局部 L1，Unity 分母 |
| --- | ---: | ---: | ---: | ---: |
| 原生 UV、实际导入纹理、默认 Principled，768² 窄过滤 | 0.047084733 | 0.048739982 | 1.035155 | 4.521% |
| 原生 UV、默认 Principled，中心子像素检查 | 0.047084733 | 0.048578664 | **1.031729** | **4.220%** |
| 原生 UV、同款 GGX/Schlick/Lambert，768² 窄过滤 | 0.047084733 | 0.047728708 | 1.013677 | 2.118% |
| 原生 UV、同款 GGX/Schlick/Lambert，中心子像素检查 | 0.047084733 | 0.047568610 | **1.010277** | **1.792%** |

本轮比之前逐像素 GBuffer 共享实验更接近实际纹理路径：UV、切线空间变换与贴图采样由两边独立完成。因此残差略大，不能将此前共享输入的 0.11% 当成本轮原生 UV 已达到的精度。默认 Principled 仍保留精确介电 Fresnel、分层与 MULTI_GGX 差异，没有偷偷替换成工程 BRDF。

原生采样输入对照：albedo RGB MAE **0.000354**；有效 roughness/metallic MAE **0.000109 / 0.000165**；世界法线夹角中位数 **0.000723°**、P90 **0.07569°**。少数亚像素变化、量化边界与极窄高光仍未完全一致，不能宣布逐像素等价。

## 对生产画面的含义

原生产自动 MIP 的 Unity 直射均值仍约 **0.038951826**，新临时 MIP0 对照为 **0.047084733**：前者相对后者低 **17.273%**，局部高光分布也明显改变。这进一步说明当前比较对纹理采样非常敏感，不能将生产画面与无相同 MIP 约定的离线图直接相减，然后给光强补一个系数。

**本轮完成了实际导入数据、原生 UV 与共同 MIP0 约定的 baseline。生产画面的自动 MIP 效果没有被修成这张 MIP0 图，原生 4K/自动 MIP 混比的旧 16.8% 指标也没有被改名为“通过”。** 没有禁用生产 MIP，没有修改 IBL、GI、灯光、曝光或运行时 BRDF。后续若评估生产采样策略，应单独研究法线/粗糙度预过滤及窄高光稳定性，而非以此结果要求所有贴图常驻 MIP0。

## 验证与复现

新场景已从磁盘重新打开验证：四个材质贴图节点均为打包的 2048² EXR、Linear 插值、UV 输入；没有 Window 投射；所有图片依赖均已打包。最大反弹为 1，模型对二次 diffuse/glossy 射线不可见；默认材质输出确实为 Principled MULTI_GGX。F12 曝光与 ACES 组合对解析曲线最大误差 **1.79e-7**。另完成实际 768² F12 渲染，未重存验证时的临时分辨率设置。

Editor 端：创建 `OfflineBaseline/HelmetOnly/imported_textures_request.txt` 和 `request.txt`，脚本刷新会执行一次导出与临时预览；成功后自动删除请求。新 compute 独立编译、Unity 2022.3 Editor/Player API 绑定和实际 GPU 执行均通过。

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/HelmetOnly/HelmetOnly_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/build_imported_texture_baseline.py
python Tools/BlenderBaseline/analyze_imported_texture_baseline.py
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/HelmetOnly/ImportedTextures/HelmetOnly_ImportedTextures_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/verify_imported_texture_baseline.py
```

产物都在 `OfflineBaseline/HelmetOnly/ImportedTextures/`：新 `.blend`、`native_imported_comparison.png`、`imported_baseline_f12.png`、`imported_baseline_report.json`、`imported_baseline_verification.json`、48 个实际 MIP 和捕获元数据。旧 Blender 场景保留，未覆盖。
