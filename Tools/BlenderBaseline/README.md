# SkyboxScene 的 Cycles 离线对照

大气新增独立参考：[独立 float64 辐射输运与实际 GPU 对照](../AtmosphereReference/README.md)。此参考不复用 Unity 的天空/LUT；当前白天整体天空照度相符，近太阳积分偏差和黄昏多次散射偏亮单独保留。

新增可独立打开的原生 UV 场景：`OfflineBaseline/HelmetOnly/ImportedTextures/HelmetOnly_ImportedTextures_Cycles.blend`。使用实际 Unity 导入 GPU 纹理，双方约定 MIP0/双线性；默认 Cycles 直射均值高约 3.2%，同款 BRDF 高约 1.0%。[范围、结果与复测说明](IMPORTED_TEXTURE_BASELINE.md)。生产自动 MIP 保持，不能将此受控参考混作生产画面已对齐。

当前头盔亮度调查：[统一当前 BC5/BC7 GPU 输入与采样后的 16.8% 差异拆解](MATCHED_INPUTS.md)。原生差距主要来自 N/R 输入与采样；同输入同 BRDF 后均值差约 0.11%，未改生产或 GI。

最新调查：[与本机 Cycles 构建直接比较 BRDF 公式](FORMULA_COMPARISON.md)。10000 组 D/G 方程对照与 42 组无贴图平面测试，分离 Fresnel、金属分层和微表面多重散射；本轮未修改运行时或 GI。

输出目录：`OfflineBaseline/SkyboxScene/`（生成物已加入 Git ignore）。本轮使用 Blender 5.2.2 LTS、Cycles OptiX、RTX 3080 Ti，1920×1080，256 samples，固定随机种子 20261016，不降噪、不钳制高光。

## 查看

- `SkyboxScene_Cycles.blend`：可编辑的完整场景，14 个图像数据块全部打包，可脱离 Unity 贴图路径打开。
- `main_aces.png`：磁盘中 Unity Main Camera 的画面。
- `detail_aces.png`：新增近景相机，便于看地面法线、头盔和阴影；不是原 Game 相机。
- `main_linear.exr`、`detail_linear.exr`：未经曝光和 tone mapping 的 32 位线性 RGB，用于定量比较。
- `manifest.json`：源场景、TRS、灯光、材质、纹理色彩空间和源文件 SHA256。
- `import_report.json`、`light_unit_calibration.json`、`verification.json`：导入边界、单位与显示链路检查。

打开 `.blend` 后选中 `Unity Main Camera` 或 `Detail Camera - shading normal reference`。F12 渲染通过合成节点应用同一项目的 EV100 与 ACES 曲线；图像保存默认 PNG。需要原始 HDR 时关闭 Render → Post Processing → Compositing，保存 32 位 OpenEXR。原始 EXR 的数值单位为 `cd/m² / 100000`，直接用普通图片查看器打开不会自动呈现正确曝光。

## 对照约定

保留源场景的 5 个可见网格、4 盏灯、材质绑定和已保存 Main Camera。两个停用的物体不参与渲染。一 Unity 单位按一米转换，坐标映射 `(Unity x,y,z) → (Blender x,z,y)`；同时转换相机基向量、四元数、网格绕序及法线。头盔直接解码项目内序列化 Mesh；枪械通过 FBX 导入，保留逐角点 UV/法线，厘米到米仅转换一次，根变换采用 Unity 场景覆盖值。Unity Cube 采用每面 0–1 UV，其中地面顶面沿源 x/z 方向展开；未读取 Unity 缓存中的内置 Cube 顶点/UV，所以内置面 UV 的旋转方向并非逐顶点导出的保证。

贴图绑定与 GBuffer 一致：四张图共用 `_AlbedoMap_ST`，金属度/粗糙度取红通道，粗糙度最小 0.05；保留原纹理的 sRGB 标记，法线按线性数据读取。不把材质里未被实际 GBuffer 使用的旧 `_Metalness`/`_Roughness` 标量当成有效输入。Cycles 使用独立的 Principled GGX BSDF（IOR 1.5 对应介电 F0=0.04），与项目近似 BRDF/分离求和 IBL 不会逐像素相同。

当前太阳读取源场景 **120467 lux**，3 个点光读取 **7500 / 15000 / 1500 lumen**。这些是本轮导出时磁盘实际值，不是手写回 120000。太阳上方辐射输入、2 米处透射率与天空都来自项目源公式，导出器抽取既有数值验证器里的生产 HLSL 并在 CPU 上计算 1024×512 全景，不访问 GPU 缓存或 Library。全景不含太阳盘；另用角直径 2° 的 Sun 灯提供直射，避免重复照明。Sun 灯在相机中没有额外可见的解析太阳圆盘。

**这是一份表面光照的受控离线 baseline，不是大气模型的独立物理真值。** 天空与太阳大气透射率共享项目公式，Cycles 独立计算材质、可见性、反射和多次反弹；天空全景与实时 LUT 分辨率/插值方式也不同。它能帮助区分表面 BRDF、阴影和实时 IBL 近似的问题，不能用来证明 UE 大气实现无误。

没有凭空把 lux 当成 SI 辐射瓦特。Blender 灯光的原生参数使用 W/W·m⁻²；这里用统一的 RGB 数值嵌入：照度除 100000、辐亮度除 100000、光通量除 100000。通过灰卡数值实验确认同一 RGB 积分尺度：Sun 数值 1 与 1 米处 Point 功率数值 4π，均应给 18% Lambert 卡 `0.18/π`。太阳实测相对误差约 2.80e-7，点光约 1.25e-5。此约定不等价于假设固定光谱的 lm/W 转换。参见 [Blender 灯光单位](https://docs.blender.org/manual/en/4.0/render/lights/light_object.html)。

显示使用项目的 `CameraEV100=15, ExposureEV=0`，乘数为 `100000/(1.2×2^15)=2.54313151`，再应用 Narkowicz ACES 拟合与一次 sRGB 编码。没有额外使用 AgX/Filmic；也没有拿 Unity Camera 上的 ISO/光圈字段替代管线的手动 EV。`.blend` 内合成节点与独立导出公式的最近验证最大像素差小于 4.33e-6（两次低分辨率重渲染，含采样/浮点差异；以 `verification.json` 为准）。线性渲染与显示变换的区别见 [Blender 色彩管理](https://docs.blender.org/manual/en/4.0/render/color_management.html)。

有意保留的比较差异：

- Cycles 包含物体间反弹和局部遮挡；实时管线没有完整场景 GI，点光也没有同等遮挡路径。
- 点光使用物理的完整反平方衰减，没有复制 SRP 的有限 Range 工程淡出。
- 该对照未加入相机空气透视雾；太阳仍经过大气透射，天空仍是程序天空。这有利于先隔离 Lighting，不能直接评价雾合成。
- 天空以固定 2 米高度导出，未对每个反射点重新积分当地大气；太阳透射也采用该高度，适用于这个近地表测试场景。
- 原场景头盔最低处约为 y=0.512 米，地面顶面 y=0.05 米，所以它确实悬空；没有为画面好看而落地或改变模型位置。

## 搬迁时发现的源材质问题

`PBR_Helmet` 的金属度和粗糙度当前绑定的是 `Cerberus_M.tga` 与 `Cerberus_R.tga`（枪械贴图）；它们的纹理导入 sRGB 标记也为 true。这些数据在本 baseline 中忠实保留，因此头盔材质不会自动变成原 glTF 的正确金属度/粗糙度。此轮没有修 Unity 的材质或导入设置。后续修正时应同时重导出 baseline，避免两边输入不一致。

## 复现

导出器需要 Python 3、numpy、Pillow、PyYAML；Blender 脚本只依赖自带的 bpy/numpy。临时 PyYAML 安装位置可为 `%TEMP%/maxsrp-baseline-python`，也可安装到自己的 Python 环境。

在项目根目录执行（替换自己的 Python/Blender 路径）：

```powershell
python Tools/BlenderBaseline/export_scene.py
pwsh -NoProfile -File Tools/BlenderBaseline/export_sky.ps1
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --factory-startup --python-exit-code 1 --python Tools/BlenderBaseline/build_baseline.py -- --samples 256 --percentage 100
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/SkyboxScene/SkyboxScene_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/verify_baseline.py
```

每次构建使用单独的 Blender 进程，不覆盖用户当前打开的工程。源文件只从 `Assets` 与既有源码读取，不启动 Unity、不访问 Library。`Assets/Sponza` 是当前工程已有但被 Git 忽略的贴图来源；全新克隆需先具备这些原始资产，生成的 `.blend` 因已打包贴图不受此限制。

## 2026-10-19：天空辐照度与局部场景反弹实测

在 Blender 中放置六个方向的纯 Lambert 测量面（反射率 1），只保留原 World、不链接灯光或场景几何，4096 samples × 16×16 像素。线性测量面的出射亮度乘 π 得到半球照度。Unity 则使用 Editor 菜单 `MaxSRP/Diagnostics/Capture Sky Irradiance Inputs` 读回管线**实际持有的** EnvironmentSkyviewLUT、原始环境 Cube 和活动 diffuse Cube，而非只计算一遍理论公式。所有读回及报告保存至 `OfflineBaseline/SkyboxScene/irradiance_comparison/`。

下表按工程 RGB 光度约定换算为 lux；diffuse Cube 中原有的 E/π 已乘回 π。Cube 轴向采用中心四像素均值，所以对应一组接近轴向的法线，存在低分辨率误差。

| 法线（Unity 世界方向） | Blender World 实测 | Unity GPU 天空 LUT 积分 | Unity GPU 环境 Cube 积分 | Unity GPU diffuse Cube 还原 E |
| --- | ---: | ---: | ---: | ---: |
| +Y 朝上 | 6207.94 | 6209.23 | 6206.66 | 6233.46 |
| +X | 4432.45 | 4432.84 | 4431.19 | 4413.61 |
| -X | 4432.52 | 4432.84 | 4431.19 | 4403.87 |
| +Z | 5020.72 | 5021.04 | 5019.15 | 4986.53 |
| -Z | 4135.27 | 4135.62 | 4134.07 | 4117.18 |
| -Y 朝下 | 1.84 | 1.79 | 2.67 | 99.15 |

朝上和四个水平方向的 diffuse 结果与 Blender 差异均小于 1%；当前样本没有显示源天空或 diffuse 卷积整体少了能量/π 因子。天空 LUT、环境 Cube 和 diffuse Cube 未发现 NaN/Inf；全局 diffuse 绑定也与管线持有的活动输出一致。当前太阳强度、方向、大气数值参数及参考高度与 baseline 一致。朝下的 99 lux 则暴露出低分辨率/MIP 过滤的地平线漏光，绝对值相对朝上较小，但不能把其相对误差视为已经解决；本轮未修改该过滤方式。

另在头盔中心位置 Unity `(1.68,1.413,0)` 放置极小的朝下 Lambert 测量面，排除头盔自身。在相同 World 下，天空独自提供 **1.84 lux**；链接原场景其余几何与灯光后变为 **36726 lux**。朝下测量面不能直接接收上方太阳，差值主要来自被太阳照亮的地面反弹。该局部测量支持“天空 IBL 缺场景反射内容”的判断：天空能量本身对得上，Cycles 额外拥有真实地面和物体反弹。它不等价于证明所有 Unity 暗区都由这一原因造成，也未验证材质中的 kD/kS、BRDF LUT 与镜面卷积消费者。

复测（不会保存或修改原 `.blend`）：

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/SkyboxScene/SkyboxScene_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/measure_world_irradiance.py
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/SkyboxScene/SkyboxScene_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/measure_ground_bounce.py
# Unity 中执行上述导出菜单后：
python Tools/BlenderBaseline/compare_irradiance.py
```

汇总结果为 `irradiance_comparison.json`。其中 CPU 卷积近似、Blender 实测和 Unity GPU 读回分开标注；不要用 CPU 近似列代替实际 GPU 结论。

## 2026-10-19：只保留头盔的 GPU/Cycles 隔离测试

当前状态：直射已接入高度相关 Smith-GGX；随后将 Standalone 法线/粗糙度改为 BC5/BC7，压缩精度和原生 Cycles 的局部误差改善，但当前完整图像的 ±10% 均值筛查不通过。下方初次结果、Smith 阶段通过结果及消融表均为阶段记录；最新数据见文末“纹理压缩质量接入与正式复测”。

用户要求先排除地面和其他几何，测试亮度，再决定是否研究 GI。本轮新增 `MaxHelmetBaselineDiagnostics.cs`，在独立 Unity PreviewRenderUtility 场景克隆当前头盔，只保留太阳；关闭点光和空气透视，原场景及材质不保存修改。六次实际 SRP 渲染分别读回合成、直射、IBL、漫反射 IBL、镜面 IBL、无阴影直射的曝光前 HDR，同时导出三个 GBuffer。压缩法线格式需先 Blit 到线性 ARGBFloat 再 ReadPixels，直接读回会得到无效数据。诊断模式仅指定 Editor 相机，结束后恢复相机、模式及雾开关，普通渲染保持模式 0。

输出位于 `OfflineBaseline/HelmetOnly/`：`HelmetOnly_Cycles.blend` 为独立可编辑文件，`helmet_comparison.png` 为三行 Unity/Cycles 对照，`comparison.json` 为完整数值，`unity_helmet.json` 为实际 GPU 相机/灯光参数。Unity 捕获时间 2026-10-19T09:09:47Z 附近，以元数据为准。768×768、VFOV 32°、太阳 120467 lux、EV100 15；Cycles OptiX 512 samples，无降噪/钳制，最多一次表面反弹。两边复用相同源天空与材质贴图，不能作为大气公式独立验证。

相机矩阵最大差约 4.01e-8，轮廓 IoU 0.999938。轮廓使用 50% coverage：Unity 单采样，而 Cycles 抗锯齿 alpha 在边缘有过渡；最初用 99% coverage 比较轮廓会只侵蚀 Cycles，IoU 为 0.988665，不能把它误判成模型或相机错位。亮度采样仍要求 Cycles coverage >99%，再把公共区域侵蚀三像素，共 137913 个像素。GBuffer 对照的法线夹角中位数 0.429°、均值 1.913°，albedo 平均绝对 RGB 差 0.01138，粗糙度/金属度差 0.01248/0.01279；纹理过滤、切线和不同 BSDF 仍会造成局部差异。

**结论：天空 IBL 的整体亮度检查通过；完整头盔测试仍不能判为通过，直射项还有差异，不能全归因于 GI。** 预先设定平均亮度 ±10% 作为筛查门槛，并不表示逐像素正确。统一使用单一方向太阳后结果如下（均为曝光前 HDR 公共内区平均亮度）：

| 项目 | Unity | Cycles | Unity / Cycles |
| --- | ---: | ---: | ---: |
| 天空 IBL，排除局部遮挡 | 0.00442322 | 0.00440896 | 1.00323 |
| 直射，排除阴影、太阳角直径 0° | 0.03736399 | 0.04681473 | 0.79812 |
| 直射，各自自身阴影、太阳角直径 0° | 0.03609672 | 0.04510589 | 0.80027 |
| 合成，自身阴影直射 + 无遮挡 IBL | 0.04051989 | 0.04951486 | 0.81834 |

原完整 baseline 使用 2° 角直径的 Cycles Sun，而 Unity 直射 BRDF 只评估单一方向，没有对太阳盘积分。沿用该 2° 灯时合成平均比为 1.03326、无阴影直射比为 1.02609，看似通过，但局部误差很大。因此补测 0° Sun，保留两套结果，不把原平均值接近当成验证完成。报告中的 `brightness_screening_pass` 只描述旧 2° baseline 的平均值筛查；初次测试的 `overall_comparison_pass=false` 保存在 `BeforeSmithGGX/comparison.json`，当前文件已由下方复测更新。对照图展示 0° Sun，当前头盔 `.blend` 默认也改为 0° Sun（保留自身遮挡），生成脚本仍自动导出两种角直径参考。

0° 无阴影直射的像素亮度比中位数约 0.758，相对 L1 误差约 0.774；IBL 对应约 0.975 与 0.149。Unity 使用 Schlick-GGX 几何近似及自己的 Fresnel/分离求和链路，Cycles 使用 Principled BSDF，且输入法线/纹理过滤有小差异；这些是后续需分项验证的候选，尚未证明哪一项导致直射差距。本轮不改 BRDF、不调整太阳/曝光来对齐图片。IBL 的局部遮挡另有正常 Cycles 参考，Unity 比其平均高 5.43%，不能把它与无遮挡环境积分混用。

第二步暂缓实施。当前天空 Cube 不含地面和周围物体，因而缺少局部场景辐射；后续应区分漫反射反弹和镜面场景反射。漫反射反弹可由 Lightmap/Light Probe/实时 GI 提供；局部镜面反射可以先研究场景 Reflection Probe，再按需要考虑 SSR/光追，不必一开始实现完整动态 GI。自定义 SRP 仍需要自行捕获、过滤、绑定和消费探针数据，不能只添加 Unity 组件就认为接入完成。参考 [Unity Reflection Probe](https://docs.unity.com/en-us/engine/6000.5/manual/lighting-overview/reflections/reflection-probes) 与 [Light Probe](https://docs.unity.com/en-us/engine/7000.0/manual/lighting-overview/direct-and-indirect-lighting/light-probes/light-probes)。朝下地平线过滤漏光也没有在本轮修复。

复测：在 Unity 执行 `MaxSRP/Diagnostics/Capture Helmet Only Baseline`，等 `unity_helmet.json` 更新后运行：

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/SkyboxScene/SkyboxScene_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/render_helmet_only.py
python Tools/BlenderBaseline/compare_helmet_only.py
```

新增 Editor 工具与运行时绑定通过 Unity 2022.3 Editor/Player API 编译检查；45 项 Shader 独立编译及动态 IBL 回归通过。物理光照验证器的计算项通过，但旧场景断言硬编码太阳 120000，当前实际为用户调整的 120467，因此该整套脚本未完全通过；没有为迎合断言改回场景灯光。以上亮度数据来自本轮实际 Unity GPU 读回和 Cycles 渲染。

## 修复前直射差异追查：光源形状与微表面几何项

`measure_direct_cards.py` 实际读取生成的 `.blend`，确认灯光类型为 **SUN**，原角直径约 2°，不是 AREA。此前 0° Sun/无阴影对照已排除太阳盘积分和自身阴影作为全部差异的解释。本次不修改运行时 Shader，仅添加离线分析及独立 Cycles 对照。

`diagnose_direct_brdf.py` 使用实际 GPU GBuffer 的 N、albedo、roughness、metalness，按相机像素射线重建 V，复制现有 BRDF 作单项替换。它是 NumPy 公式复现，不是 GPU 执行新 Shader；太阳大气透射使用导出器的固定 2 米值。原公式复现与 GPU 的平均亮度比为 1.00473，相对 L1 为 0.00481，说明它足以用于此次消融，但不宣称逐指令等价。

| 保持实际 GBuffer 不变的离线公式 | 平均 HDR 亮度 | 相对原公式 | 相对 Cycles 0° Sun |
| --- | ---: | ---: | ---: |
| 原 Schlick-GGX，分母 +0.001 | 0.03754065 | 1 | 0.80190 |
| 只去掉 +0.001 | 0.03756197 | 1.00057 | 0.80235 |
| 精确 Smith-GGX，保留 +0.001 | 0.04392463 | 1.17006 | 0.93827 |
| 高度相关 Smith-GGX，无 +0.001 | 0.04394992 | 1.17073 | 0.93881 |

主要已确认的模型差异在 `BRDF.hlsl` 的 `GeometrySchlickGGX`：`k=(roughness+1)^2/8`，`G=G1(N·V)G1(N·L)`。这是 LearnOpenGL 中的直射 Schlick 近似/粗糙度重映射，并非抄错公式，也不是和精确 Smith-GGX 完全相同的微表面可见性。它会在本次头盔配置下显著压低镜面响应。参见 [LearnOpenGL 的几何项说明](https://learnopengl.com/PBR/Theory) 和 [Cycles 微表面实现](https://github.com/blender/blender/blob/main/intern/cycles/kernel/closure/bsdf_microfacet.h)，后者直接使用 `1/(1+lambdaI+lambdaO)` 的高度相关 Smith 可见性。

为排除贴图/法线的干扰，额外用纯白金属平面 F0=1、正交相机、黑 World、0° Sun 照度 1、无阴影/多次场景反弹进行 12 组角度/粗糙度测量，每组分别选择 Cycles GGX 与 MULTI_GGX。例：`N·V=N·L=0.5、roughness=0.3` 时，原工程公式预测出射值 **13.37930**，高度相关 Smith 预测 **19.41429**，Cycles GGX 实测 **19.41429**。12 组 Smith 预测相对 Cycles GGX 最大误差约 0.239%（低粗糙度高光对角度浮点误差敏感）。这确认了可见性近似造成的响应差异，不依赖头盔纹理，也不是光源功率缩放。

源 Principled 默认 `MULTI_GGX`。在实际头盔上仅将其改成单次散射 `GGX` 重渲染，原 MULTI_GGX 仅比其平均高约 **0.097%**，不足以解释 20% 差距。粗糙金属平面中该补偿可能很明显，不能把本头盔的结论推广到所有材质。这里的微表面多重散射是材质 BSDF 内部模型，区别于地面/物体之间的场景 GI。

**尚未证明所有像素差异都由 G 项引起。** Smith 替换仅把平均亮度差从约 20% 缩小到约 6%；逐像素 L1 仍很大，且此替换没有降低该误差。法线/纹理采样、极窄高光的抗锯齿与 Principled 的 Fresnel/分层差异仍需分开比较。不能据此直接给太阳乘 1.25，或宣布新公式已经在 GPU 修好。此次只完成原因调查，保持实时 BRDF、光强、曝光不变。

复测命令：

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/HelmetOnly/HelmetOnly_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/measure_direct_cards.py
python Tools/BlenderBaseline/diagnose_direct_brdf.py
```

结果：`OfflineBaseline/HelmetOnly/direct_card_diagnosis.json`、`direct_brdf_diagnosis.json`、`blender_direct_delta_single_ggx.f32`。这两个脚本不保存或修改 `.blend`，也不需要 Unity 再刷新。

## Smith-GGX 接入后的实际 GPU baseline

生产 `BRDF.hlsl` 已替换为高度相关 Smith-GGX，使用合并可见性形式 `G2/(4*NdotV*NdotL)`。它与精确 lambda 形式等价，消去分别求 G 再除掠射余弦的中间步骤；去掉镜面分母人为 `+0.001`，仅对合并表达式的零分母保留数值下限。N·V 或 N·L 非正时镜面为零；漫反射仍按原规则保留，避免重新引入法线地平线整体清零。GGX NDF、Fresnel、天空/IBL 和所有场景强度/曝光保持原值。点光与太阳共用这条 BRDF。

重新从实际 Unity GPU 读回六项结果并重渲染 Cycles，768×768、512 samples，沿用同一头盔隔离条件与 ±10% 均值门槛。Editor 报告新增 BRDF 源码 SHA256；比较器核验当前源码与捕获对应，拒绝使用改 Shader 前的旧捕获。旧 Unity 图像、HDR、GBuffer 和报告保存在 `BeforeSmithGGX/`，生成 `helmet_before_after.png` 三列对照（旧 Unity / 新 Smith-GGX / Cycles）。

| 项目 | 旧 Unity/Cycles | 新 Unity/Cycles | 新 Unity 平均 HDR |
| --- | ---: | ---: | ---: |
| 无阴影直射，0° Sun | 0.79812 | **0.93438** | 0.04374289 |
| 自身阴影直射，0° Sun | 0.80027 | **0.93867** | 0.04233958 |
| 合成，自身阴影直射 + 无遮挡 IBL | 0.81834 | **0.94445** | 0.04676426 |
| 无遮挡天空 IBL | 1.00323 | **1.00323** | 0.00442322 |

无阴影直射实际增加 17.072%，合成增加 15.411%，IBL 不变。`matched_delta_light_screening_pass` 与 `overall_comparison_pass` 为 true，含义是**本配置平均亮度筛查通过**，`per_pixel_equivalence_verified` 仍为 false。旧 2° Sun 的平均值筛查现在为 false，因为它与 Unity 单方向直射不匹配，不作为新结果的失败或成功依据。

局部高光差异仍很大：新无阴影直射像素比中位数 0.80546，相对 L1 0.82943（旧版 0.77391）；合成相对 L1 0.78568。这次改善了平均响应，不能声称所有像素更接近 Cycles；法线/纹理采样、极窄高光抗锯齿和不同材质模型仍需单独研究。当前头盔约 57.5% 的可见像素粗糙度小于 0.1，其中约 27.5% 卡在 0.05 下限，小角度法线差异对这种高光十分敏感；这只是后续排查依据，并非已经证实剩余误差的根因。GI 继续暂缓，朝下环境漏光未在本次修改。

验证通过：生产 HLSL 抽取的物理光照验证、180 组独立双精度 lambda 对照与互易性、Cycles 白金属平面解析参考、401 组法线地平线漫反射下界、45 项 Shader 独立编译、Editor/Player C# API 绑定、光照/阴影及动态 IBL 回归。更新了旧太阳固定 120000 的场景断言，允许用户调节的有效正 lux；未为测试修改场景灯光。历史章节中关于“运行时未改”及旧断言失败的叙述描述的是此前调查阶段，本节为当前状态。

## 局部高光差异：共享输入与材质模型的分离实验

本阶段不修改生产 BRDF、纹理导入设置或 GI。新增 `diagnose_highlights.py` 与 `analyze_highlights.py`，在头盔隔离场景固定 0° Sun、无阴影、黑 World、无场景反弹，分别改变输入和 BSDF。结果保存在 `OfflineBaseline/HelmetOnly/HighlightDiagnosis/`，含 `highlight_report.json`、`highlight_comparison.png` 及各项线性 EXR/float32。新 Unity 主捕获时间为 2026-10-19T09:50:39Z，法线变体为 09:50:44Z；以 JSON 元数据为准。

核心方法是把**实际 Unity GBuffer 的逐像素法线、线性 albedo、有效粗糙度和金属度**作为浮点图像，在 Cycles 用相机 Window 坐标以 Closest 采样投射到同一头盔上，绕过两边原生 UV/切线、纹理压缩和 MIP 的差异。Cycles 相机像素过滤宽度从默认 Blackman-Harris 1.5 缩窄到 0.01，便于匹配 Unity 的单次像素采样。投射的 albedo/编码法线平均绝对误差约 1.14e-6/1.82e-6，粗糙度与金属度约 4.74e-7/1.27e-6，确认输入确实相同。窄过滤只服务此诊断，未改正式 baseline 或生产抗锯齿。

下表统一使用 `sum(abs(Y_test-Y_Unity))/sum(Y_Unity)`，在 137913 个公共内部像素计算。它描述局部亮度分布差异，不是平均亮度丢失比例。此前报告约 82.9% 的指标以 Cycles 为分母，换成此表统一的 Unity 分母后对应 88.8%。

| Cycles 对照 | 相对 Unity 的局部亮度 L1 |
| --- | ---: |
| 原生贴图/法线与默认像素过滤 | 88.77% |
| 原生输入，仅缩窄像素过滤 | 103.89% |
| 只换成 Unity 法线，保留 Cycles 原生其他输入 | 32.40% |
| 再换成 Unity 有效粗糙度 | 5.13% |
| 全部共享 Unity GBuffer，Principled 单次 GGX | 3.54% |
| 全部共享输入，使用与工程一致的 GGX + Schlick Fresnel + Lambert | 0.807% |

每行是控制实验，不是可直接相加的误差贡献；最后一行是**诊断材质**，没有替换或“修好”原始 Cycles baseline。只统一 albedo/粗糙度/金属度、保留 Cycles 原生法线时 L1 仍为 79.0%，进一步支持法线的重要性。单独缩窄像素过滤并不能修复差异，也不能把去掉抗锯齿当作生产解决方案。

### 法线：已定位到压缩、MIP 与 GBuffer 量化

Unity 当前实际加载的法线为 **2048×2048 DXT5，12 个 MIP，Direct3D11**。Editor 诊断仅在预览相机上使用临时浮点 normal GBuffer 和临时材质/纹理，完毕恢复资源；原场景、材质、贴图 importer 和 Read/Write 不变。曾因 `Object.Instantiate` 不允许克隆不可读纹理而产生控制台错误，现已改为创建同格式纹理、释放其 CPU 像素后 `Graphics.CopyTexture` 拷贝完整 MIP 链，新版 GPU 读回成功。

- 普通 packed normal GBuffer 与浮点 GBuffer 的法线夹角中位数约 **0.0431°**。仅提升 normal GBuffer 精度，直射均值增加约 **1.71%**，局部 L1 变化约 **7.78%**，说明极窄高光对量化敏感，但这不是主要的全部差距。
- 在浮点 normal GBuffer 下，固定相同的 MIP0/双线性路径，导入的 DXT5 与未压缩源 JPEG 的 XY 重建法线夹角中位数约 **0.449°**，确认压缩路径引入可观偏差。
- Unity 未压缩源图 XY 重建与 Cycles 原生窄过滤法线的夹角中位数约 **0.0109°**，P90 约 **0.192°**；相比之下 DXT5 MIP0 对 Cycles 中位数约 **0.444°**。这是该材质/视角下的实测，不能推广成所有模型的格式误差。
- 两边去掉法线贴图后的平滑几何法线中位差仅 **0.00191°**，P90 约 **0.00466°**，没有发现整体几何法线坐标转换错误的证据。
- 强制 MIP0 会明显改变局部高光，但属于采样对照，不是建议生产禁用 MIP。其与浮点 GBuffer/原图对照同时表明 MIP、压缩和窄高光之间存在相互作用。

### BRDF：主要剩余模型差异在非金属 Fresnel/分层

共享相同输入后，把 Principled 单次 GGX 换成 GGX Glossy + 显式 Schlick 反射颜色 + 按工程 kD 加权的 Lambert，可将与 Unity 的 L1 从 3.54% 降到 0.807%。两种 Cycles 材质自身的 L1 差约 2.72%；共享输入时开启 MULTI_GGX 相对单次版本的影响仍很小。这验证了材质模型仍有差异，但不是原来大幅高光错位的主要来源。

再将同一批输入的金属度强制为端点，仅用于诊断，并对照离线工程 BRDF：

| 端点 | 工程公式相对 Cycles 的平均亮度比 | 相对 L1（Cycles 分母） |
| --- | ---: | ---: |
| 纯金属、Schlick Fresnel | 0.99697 | 0.381% |
| 纯非金属、Schlick Fresnel | 0.93367 | 12.53% |
| 纯非金属、精确介电 Fresnel，仍保留工程的 Lambert 权重 | 1.02283 | 2.98% |

后两行使用同一个几何/输入与微表面可见性，说明精确介电 Fresnel 是非金属残差的重要因素。Principled 还会根据镜面层反射率衰减下层漫反射，工程则使用 `(1-F)*(1-metalness)`；只换 Fresnel 并未完整复制这种分层，所以仍有残差。参考 [Cycles Principled 源码](https://github.com/blender/blender/blob/main/intern/cycles/kernel/svm/closure.h) 的介电 Fresnel 与 `closure_layering_weight`。这是一份模型对照，不是证明任一材质模型在所有条件下更正确。

**结论：局部差异主要来自法线/粗糙度输入与采样，另有较小的非金属材质模型差异；当前 Smith-GGX 核心未显示明显缺口。** 本轮不再修改生产 BRDF，不修改压缩/过滤质量策略，不实现 GI。后续若要缩小差距，应先单独评估法线压缩与粗糙度采样；精确介电 Fresnel 和分层模型可另立受控实验，避免为追逐一张图修改全管线。

复测：Unity 导出工具所在目录创建 `request.txt` 与 `highlight_request.txt`，脚本刷新后会执行主捕获和四项法线变体。无需开启贴图 Read/Write。然后：

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background OfflineBaseline/HelmetOnly/HelmetOnly_Cycles.blend --python-exit-code 1 --python Tools/BlenderBaseline/diagnose_highlights.py
python Tools/BlenderBaseline/analyze_highlights.py
```

本次新增 Editor 代码通过 Unity 2022.3 Editor/Player API 绑定检查，并在 Unity GPU 完成读回；Python 脚本通过语法检查和实际 Cycles 执行。生产运行时 Shader 未在本阶段再修改。

## 纹理压缩质量接入与正式复测

先以临时纹理在 GPU 做同源、同分辨率、同 MIP 链对照，再通过 TextureImporter 应用两张资源的 **Standalone** 格式：头盔 `04#GLB.jpg` 法线 DXT5 → BC5，`Cerberus_R.tga` 粗糙度 DXT1 → BC7，压缩质量 100。当前实际加载均为 2048²，MIP 与 Read/Write=false 保持；法线 sRGB=false、粗糙度原有 sRGB=true 保持。后者的数据色彩空间以及头盔使用枪械 M/R 贴图属于既有材质问题，本轮没有混入修改。粗糙度资源也被枪械共用，因此其 Standalone 压缩质量同时影响两处，不改变绑定或数值单位。其他平台没有启用新覆盖；Unity 保存 importer 时对旧 meta 做了序列化版本升级并补出未启用的平台条目。

压缩名义数据量（2048² 完整 MIP，不含对象开销）：DXT5 与 BC5 都约 **5.33 MiB**；粗糙度 DXT1 约 **2.67 MiB**，BC7 约 **5.33 MiB**，增加约 2.67 MiB。没有给所有纹理统一开启无压缩或 Read/Write，也没有提升全管线 GBuffer 格式。导入结果保存为 `sampling_quality_applied.json`，实际加载格式已确认 BC5/BC7，旧正式 GPU 数据归档至 `BeforeSamplingQuality/`。

本轮还发现初版离线对照的分辨率约定缺口：**Cerberus 的 R/M 源图均为 4096²，Unity 按 maxTextureSize 实际导入为 2048²，而 Blender 使用原始 4096²。** 因此原先“粗糙度采样差异”不能全部归为压缩；应同时包括缩放、MIP、像素足迹。4096² 对 2048² 的初步压缩对照不能用于隔离 codec。最终矩阵新增同为 2048² 的源图对照（Pillow Bilinear 生成固定参考，不假装完整复制 Unity importer 的缩放与法线 MIP 预处理），每对只改变 codec。

| 同分辨率压缩对照 | 局部亮度 L1，分母为对应无压缩参考 |
| --- | ---: |
| 2048² 法线，现有 DXT5 MIP0 | 47.49% |
| 2048² 法线，BC5 MIP0 | **6.84%** |
| 2048² 粗糙度，同源 DXT1、正常 MIP | 6.20% |
| 2048² 粗糙度，同源 BC7、正常 MIP | **0.81%** |
| 2048² 粗糙度，DXT1 / BC7 MIP0 | 5.35% / **0.93%** |

法线 BC5 对 RGBA32 的正常 MIP L1 约 7.96%，在相同 packed normal GBuffer 中约 5.64%。这些数字描述当前材质/视角的高光敏感性，不是普适 codec 指标。正常 MIP 测试证明无须关闭 MIP；高精度 normal GBuffer 的成本收益另留为全管线策略，不在本次提升带宽。4096² BC7 候选约 21.33 MiB，仅为分辨率实验，没有应用到正式资产。

随后在实际导入 BC5/BC7 的 Unity 再做六项读回（2026-10-19T10:10:30Z），保持同一相机、太阳 120467 lux、EV100 15、无 GI、0° Sun 对照。原始 Cycles 参考没有改用 Unity 投射输入，也没有改成工程诊断材质：

| 相对原生 Cycles 的正式结果 | DXT5/DXT1 | BC5/BC7 |
| --- | ---: | ---: |
| 无阴影直射平均亮度比 | 0.93438 | **0.83204** |
| 无阴影直射局部 L1（Cycles 分母） | 0.82943 | **0.56201** |
| 合成平均亮度比 | 0.94445 | **0.84815** |
| 合成局部 L1（Cycles 分母） | 0.78568 | **0.53306** |
| 天空 IBL 平均亮度比 | 1.00323 | **1.00211** |

**压缩保真度与局部高光分布有改善，完整画面对齐仍未完成。** 当前 `overall_comparison_pass=false`：直射均值低约 16.8%、合成低约 15.2%，超出旧 ±10% 门槛。此前 Smith 阶段“通过”只是当时有损纹理与单帧采样下的均值结果，不能据此证明稳定的能量标定。提高源图保真度会移动极窄高光，并改变当前相机可见像素的均值；该均值不是全半球反射能量。不能为了保留旧通过状态恢复有损偏差，或给太阳/曝光增加补偿。

下一步应先统一离线输入的实际导入尺寸，再做像素足迹/采样收敛对照，并保留介电 Fresnel/分层的独立模型差异。当前证据不支持归咎于 GI；GI 与朝下环境过滤漏光本轮均未动。相同输入/同模型的历史控制实验继续保留在 `HighlightDiagnosis/SourceUnityInputs/`，防止用新 GBuffer 混算旧 Cycles 投射结果。

`analyze_sampling_candidates.py` 生成 `HighlightDiagnosis/sampling_report.json` 和 `sampling_candidates.png`。正式前后对照为 `sampling_quality_before_after.png`，最新整体统计仍为 `comparison.json`；其中 `before_after_smith_ggx` 保留固定输入的 Smith 阶段结果，`before_after_sampling_quality` 单独记录纹理变化，不把两轮收益混为 BRDF 改进。新工具通过 Editor/Player API 绑定及 Python 语法检查，实际 GPU 格式、尺寸、读回结果均已检查；本阶段没有新的运行时 Shader 修改。
