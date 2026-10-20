# 独立大气输运对照（2026-10-19）

太阳光晕的四组 GPU 采样消融试验单独记录在 [HALO_COMPARISON.md](HALO_COMPARISON.md)。它用于区分 LUT 与积分影响，不属于独立输运参考。

后续的实际 Color Output / Editor 合成排查和独立 RGB dithering 修正见 [FINAL_OUTPUT.md](FINAL_OUTPUT.md)。

10 月 20 日进一步定位并修正的 GPU sRGB8 量化阈值偏差，以及同 HDR 前后 GPU 对照，见 [OUTPUT_TRANSFER.md](OUTPUT_TRANSFER.md)。

**当前白天 demo 的整体天空能量与独立参考相符。近太阳区域仍有固定 32 步积分偏差，黄昏部分方向的多次散射偏差较大。** 本轮完成独立参考与实际 GPU 对照，只增加 Editor 诊断和工具，没有修改生产大气、太阳、曝光、IBL 或场景 GI。

## 独立性与边界

参考是本轮另行编写的 **float64 球形介质辐射输运求解器**，没有移植或调用工程 HLSL，也没有读取 Unity 的透射 LUT、多次散射 LUT、天空 HDR 或 GPU 标准答案。不是之前把 Unity 天空导入 Blender 后得到的同源对照。

唯一共享的是物理输入和查询几何：半径/高度、密度剖面、RGB 散射/吸收系数、相函数参数、太阳顶端入射强度，以及观察方向。`prepare_inputs.py` 单独生成只含这些信息的 `reference_inputs.json`，参考计算与渲染程序只读取这份输入清单和自己的中间数据；GPU 结果只在最后的比较器中读取。

数学定义参照 [Bruneton 的公开大气方程](https://ebruneton.github.io/precomputed_atmospheric_scattering/atmosphere/functions.glsl.html)，多次散射以 [体积路径追踪与 null-scattering 方法](https://pbr-book.org/4ed/Light_Transport_II_Volume_Rendering/Volume_Scattering_Integrators) 独立计算。[Hillaire/UE 论文配套项目](https://github.com/sebh/UnrealEngineSkyAtmosphere) 同时提供近似与路径追踪对照；这里没有把其近似与完整输运必须逐像素相等作为判定前提。文献用于定义方程/方法，参考程序并非直接运行上述仓库的成品。

本次沿用工程的 RGB 三通道和 `100000` 光度缩放约定，不是完整光谱/CIE 标定或真实地球测量。固体行星为黑色边界，没有地表反照、场景物体、云或场景 GI。太阳作为方向点源参与散射与遮挡，不含可见太阳圆盘，也没有增加有限太阳盘的半影模型。因此本次结论不覆盖太阳盘外观、空气透视的完整距离链路或所有可调参数组合。

## 当前参数与验证范围

实际 GPU 捕获时间：**2026-10-19T13:11:44Z**，Direct3D11。

- 行星半径 6360000 m、大气厚度 60000 m；Rayleigh/Mie 密度尺度高度 8000/1200 m。
- Rayleigh 海平面系数 `[5.802, 13.558, 33.1] × 10⁻⁶ m⁻¹`。
- Mie 散射/吸收 `3.996 / 0.444 × 10⁻⁶ m⁻¹`，Cornette–Shanks 参数 g≈0.8。
- 臭氧峰值 `[0.650,1.881,0.085] × 10⁻⁶ m⁻¹`，中心 25000 m、三角剖面半宽 15000 m。
- 当前白色太阳 120467 lux（项目约定），高度角 **69.811°**；当前观察高度 **2 m**。
- 共 **48 组 GPU 查询**：当前角度和 90°/45°/10°/0°/−3°；2 m 的七个观察方向，以及当前太阳在 1 km/10 km 高度的三个方向。

GPU 诊断 kernel 直接包含生产 `RayMarching.hlsl`，使用管线持有的透射与多次散射 LUT。先采完整结果，再仅将该诊断 compute 的多次散射输入绑定为私有零纹理，采单次结果。不改变全局绑定、管线参数或实际天空渲染。记录生产源码 SHA256，比较器检查捕获与当前源码一致。

## 参考计算及可靠性检查

透射率由独立 Gauss–Legendre 光学厚度积分计算；为路径追踪加速，另建 **256×1024** 的双精度太阳透射表，使用平方根高度和相对局部地平线的对数角坐标。它的参数化、精度与数据均独立于 Unity LUT。

单次散射以 8192 段积分并对照 4096 段。多次散射使用 null-collision 自由程采样、Rayleigh/Cornette–Shanks 相函数重要性采样、太阳 next-event estimation 和吸收生存权重。各 RGB 通道独立追踪；将解析高精度单次结果替换 MC 第一阶，减少方差。每个查询/通道 **1048576 条路径**，64 个独立随机批次，最多 24 阶，第 10 次散射起使用无偏 Russian roulette。

检查结果：

| 检查 | 结果 |
| --- | --- |
| 两种相函数的全立体角积分 | 约 1，误差 < 3e-13 |
| 垂直透射：指数层/三角臭氧的解析积分对数值积分 | 最大绝对差约 3.54e-8 |
| 独立透射表对 512 点直接积分 | 测试网格最大绝对误差约 1.95e-5 |
| 128 点对 512 点光学积分 | 最大绝对差约 1.69e-5 |
| 两种相函数各 100 万采样的前两阶矩 | 在独立积分预期的 6σ 内 |
| 方向旋转的长度与余弦保持 | 最大误差约 4.44e-16 |
| 48 组 MC 单次散射对确定性单次积分 | 全部通过统计一致性检查 |
| 当前单次水平辐照积分加密：32×128→64×256，4096→8192 步 | 照度变化约 0.00225 lux |

误差条为 64 个独立批次的 **±2 标准误差，近似 95% 统计区间**；不把它当作包含所有离散误差的严格总误差界。最后两阶未检出贡献，也没有据此声称数学上的无限阶已被精确求和。细节与随机批次缓存均有保存。

## 当前白天空能量

对实际 256×128 GPU 天空 LUT 积分水平上半球，排除太阳盘、材质、曝光和 tone mapping：

| 项目 | Unity GPU | 独立参考 | Unity/参考 |
| --- | ---: | ---: | ---: |
| 水平天空照度，项目 lux 约定 | **6209.23** | **6209.16 ± 7.08** | 1.000012 |
| R 通道辐照值 | 3105.18 | 3148.62 | **0.98620** |
| G 通道辐照值 | 6321.41 | 6332.10 | **0.99831** |
| B 通道辐照值 | 14238.23 | 14003.32 | **1.01678** |

**总照度在参考统计不确定度内相符，但不能把一次得到的 0.0012% 均值差当作这么高的验证精度。** RGB 有约 −1.38%/−0.17%/+1.68% 的差异，亮度加权后有抵消。

当前 2 m 高度、除太阳方向以外的六个方向，完整天空亮度相对独立参考误差约 **−0.60%～+1.42%**；太阳方向约 **−4.80%**。因此当前白天 demo 整体能量基本合理，局部仍非完全一致。

太阳透射率的当前三通道最大绝对差约 **0.000935**（0.0935 个百分点）；全部查询最大约 **0.004646**（0.4646 个百分点，出现在海平面附近的垂直太阳）。这是无量纲透射差，不能混写成太阳能量损失的固定百分比。

## 已定位的近太阳积分偏差

当前太阳方向，GPU 单次散射/独立高精度参考为 **0.942782**。再用独立介质和独立太阳透射，仅改变分段数、对每段常系数输运精确积分：

| 分段数 | 独立分段结果/高精度参考 |
| --- | ---: |
| 32 | **0.942606** |
| 64 | 0.984933 |
| 128 | 0.996186 |
| 256 | 0.999044 |
| 1024 | 0.999941 |

独立 32 段结果复现了生产单次散射的约 5.7% 亏损，说明主要是固定步数对低层 Mie 密度变化的积分偏差，不是靠调太阳强度才能补齐。完整天空含多次散射后，此方向仍约低 4.8%。本轮没有调整生产步数；后续可以独立评估自适应分段或步数预算。

## 黄昏仍不能结案

太阳高度 −3°、观察仰角 1°、背阳方向：单次散射在两边均为零，完整天空亮度 Unity/参考 **1.73955**，即偏亮约 **73.96%**；该比值的参考统计区间约 ±0.0263，差异明显超出采样噪声。两边亮度约为 **38.32 / 22.03 cd/m²**（沿用项目 RGB 光度约定），不能把较大相对误差误读成白天空的总能量也多了 74%。

同一黄昏的天顶等方向约高 7%～16%。误差定位到本实现的多次散射链路；尚未将其进一步拆成 isotropic/local closure 近似、32×32 LUT 的太阳角/高度插值、固定 64 方向采样等各自贡献。**这不是证明 UE 原论文公式错误。** 不应通过整体削减多次散射强度来追平一个方向，因为当前白天整体照度已经合理。

## 产物与复现

目录：`OfflineBaseline/AtmosphereReference/`，保持在 Git ignore 内。

- `reference_inputs.json`：仅含共享物理输入，无 GPU 标准答案。
- `unity_probes.json` / `unity_snapshot.json`：GPU 数据与源码哈希。
- `independent_results.json`：每方向/散射阶的独立结果、统计误差和辐照积分。
- `atmosphere_comparison.json`：完整数值对照。
- `atmosphere_reference_comparison.png/.svg`：48 方向、积分步数与照度图。
- `independent_sky_comparison.png`：相同曝光下的天空对照。
- `independent_sky.exr`：完全独立计算的天空，可用于 Blender 默认环境映射，已转换方位约定；无太阳盘。此 128×64 示意 HDR 使用每像素/通道 2048 条路径，不替代高采样数值参考。

Editor 采集：在 `OfflineBaseline/SkyboxScene/irradiance_comparison/` 创建 `request.txt` 和 `atmosphere_reference_request.txt`，Unity 脚本刷新后自动执行一次并删除请求。实际采集已完成，无需修改场景。

工具依赖 Python 3.12、NumPy、Numba/llvmlite、Matplotlib 及 Pillow。当前额外科学库仅装在 `%TEMP%/maxsrp-atmosphere-python`，不改 Unity Python/工程运行时；可用如下方式准备相同的隔离目录：

```powershell
python -m pip install --target "$env:TEMP/maxsrp-atmosphere-python" numba llvmlite matplotlib
python Tools/AtmosphereReference/prepare_inputs.py
python Tools/AtmosphereReference/run_reference.py --samples 1048576
python Tools/AtmosphereReference/validate_sampling.py
python Tools/AtmosphereReference/diagnose_integration.py
python Tools/AtmosphereReference/render_reference_sky.py
python Tools/AtmosphereReference/analyze_comparison.py
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --factory-startup --python-exit-code 1 --python Tools/AtmosphereReference/export_reference_exr.py
```

实际执行使用 NumPy 2.3.5、Numba 0.68.0、llvmlite 0.50.0、Matplotlib 3.11.2；Numba 缓存已在工具目录局部 ignore。新的 Editor compute 入口、Unity 2022.3 Editor/Player API 绑定与实际 GPU 采集通过；本轮未修改生产 HLSL。
