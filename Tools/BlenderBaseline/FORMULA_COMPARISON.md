# Unity / Cycles 直射公式对照（2026-10-19）

本轮直接比较相同输入下的公式。运行时 Shader、材质、光强、曝光、IBL 和 GI 均未修改。

## 版本与方法

本机 Blender **5.2.2 LTS，构建 d13f752e3b9c**。对照该构建的 [微表面 BSDF](https://github.com/blender/blender/blob/d13f752e3b9c/intern/cycles/kernel/closure/bsdf_microfacet.h)、[Principled 分层与金属混合](https://github.com/blender/blender/blob/d13f752e3b9c/intern/cycles/kernel/svm/closure.h)、[Fresnel 与分层权重](https://github.com/blender/blender/blob/d13f752e3b9c/intern/cycles/kernel/closure/bsdf_util.h)。源码副本、URL 与 SHA256 保存于 `OfflineBaseline/FormulaComparison/cycles_d13f752e3b9c/`；不使用变化中的 main 作为本机实现依据。

生产 `BRDF.hlsl` 通过已有验证器抽取为 C# 单精度运算；D 与合并可见性对照独立双精度 Cycles 方程，10000 组参数，roughness ≥ 0.05。

另外渲染一张无贴图平面：黑 World、单位照度的 0° Sun、正交相机、无阴影、无二次 diffuse/glossy 射线可见性，没有其他几何。IOR=1.5、Specular IOR Level=0.5，coat/sheen/transmission/subsurface 为零，默认白 specular tint。三组方向、两组粗糙度、两组灰度及金属度 0/0.5/1，加黑色非金属隔离镜面，共 **42 组输入，分别渲染 GGX 与 MULTI_GGX，合计 84 次**。

方向：`N=(0,0,1)`、`V=(sinθ,0,cosθ)`、`L=(-sinθ,0,cosθ)`，H=N，N·V=N·L=cosθ，θ 为 0°/60°/75°。比较线性响应 `BRDF*NdotL`，不经过曝光或 tone mapping，没有 UV/MIP/压缩差异。

## 相同部分：D、Smith 可见性与余弦约定

两边均令 `alpha=roughness²`，各向同性 GGX：

```text
D = alpha² / (pi * (1 - NH² + alpha²*NH²)²)
lambda(x) = (sqrt(1 + alpha²*(1/x² - 1)) - 1)/2
G2 = 1 / (1 + lambda(NV) + lambda(NL))
specular = D * G2 * F / (4*NV*NL)
```

Unity 的合并 Smith 表达式与此等价。10000 组对照最大相对误差：可见性 **1.96e-7**，D **2.03e-5**。纯金属平面相对 Cycles 单次 GGX 最大响应误差 **6.39e-7**。这支持当前单精度实现与上述方程一致。

Cycles eval 已包含入射方向余弦；工程在 BRDF 外乘 NdotL。统一为 `f_r*NdotL` 后没有遗漏余弦或多除一个 pi。

## 差异一：非金属 Fresnel

工程使用 Schlick；Cycles 默认非金属分支使用精确介电 Fresnel，按 IOR/Specular IOR Level 调整。IOR=1.5，角度指 V 与 H 的夹角：

| 角度 | 工程 Schlick | Cycles 精确介电 | 工程/Cycles |
| --- | ---: | ---: | ---: |
| 0° | 0.040000 | 0.040000 | 1.000000 |
| 60° | 0.070000 | 0.089187 | 0.784870 |
| 75° | 0.254730 | 0.253061 | 1.006597 |

黑色非金属平面去除 diffuse 后，60° 的实际镜面响应比 **0.784870**，与 Fresnel 比值吻合。误差随角度变化，不能概括为整个场景固定少 21.5%。默认白 tint 下，Cycles 金属 F82 修正项为零，退化为与工程一致的 Schlick；其他金属 tint/薄膜条件不在此结论内。

## 差异二：金属混合与漫反射分层

工程先混合 F0，再使用同一个 F 衰减 diffuse：

```text
F0 = lerp(0.04, baseColor, metallic)
F = Schlick(HV, F0)
diffuse = (1-metallic) * (1-F) * baseColor/pi
```

Cycles 先按 metallic 分配金属与介电 closure，再根据介电镜面层的反射 albedo 衰减下层 diffuse。混合顺序和分层权重不同，下层衰减也不是每条光线的 `1-F(HV)`。

baseColor=0.8、roughness=0.7、正面入射/观察：

| metallic | Unity 响应 | Cycles 单次 GGX | Unity/Cycles |
| --- | ---: | ---: | ---: |
| 0 | 0.257719 | 0.257425 | 1.001144 |
| 0.5 | 0.213050 | 0.261286 | **0.815391** |
| 1 | 0.265148 | 0.265148 | 1.000000 |

正面时两边 Fresnel 端点相同，半金属仍低 **18.46%**，所以只换精确 Fresnel 不足以对齐。Cycles 半金属响应等于两端点线性混合；全部平面半金属案例对此关系的最大相对误差 **3.81e-7**。工程在 diffuse 中使用混合 F，产生不同的中间金属度响应。这是材质模型差异，不是场景 GI。

## 差异三：MULTI_GGX 补偿

Cycles 默认 MULTI_GGX 补偿单次微表面模型丢失的多次散射。工程当前没有对应补偿。

正面、baseColor=0.8、roughness=0.7：纯金属 GGX **0.265148**，MULTI_GGX **0.351411**，高 **32.53%**；半金属 GGX **0.261286**，MULTI_GGX **0.306086**，高 **17.15%**。这是特定方向响应，不是全半球能量积分，不能直接套到头盔整图。此前头盔 GGX/MULTI_GGX 平均差很小，不与此粗糙平面案例矛盾。

这种散射发生在材质微表面模型内部，无需追踪其他模型部位；它与头盔各部位互相反射、地面反弹的场景 GI 是两件事。

## 模型自身反射与结论边界

完整 Cycles 场景会包含物体间/模型自身的间接反射。受控的 `render_helmet_only.py` 在 `direct_delta_unshadowed` / `direct_delta` 捕获前已关闭头盔 `visible_diffuse` 和 `visible_glossy`，World=0；后者只恢复 shadow 可见性。`diagnose_highlights.py` 同样关闭二次射线可见性。因此受控直射差值不包含模型自身的二次反射。此次单平面进一步排除几何歧义。

当前 Smith-GGX 核心与 Cycles 单次 GGX 一致。仍确认存在非金属 Fresnel、金属混合/下层 diffuse 权重、微表面多重散射补偿差异。它们在没有 GI 的平面上即可重现，应与纹理/法线采样分开讨论。

不能把平面百分比直接用于解释完整头盔当前约 16.8% 的直射均值差，也不能宣布原生图像已对齐。若继续修改 BRDF，应先确定是否采用 Principled 类的介电/金属分支与分层约定，再独立评估多重散射，并考虑 IBL 模型一致性。本轮未作运行时修改，没有增加光强或曝光补偿。

## 复测

```powershell
& 'D:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --factory-startup --python-exit-code 1 --python Tools/BlenderBaseline/measure_formula_points.py
pwsh -NoProfile -File Tools/BlenderBaseline/compare_formula_points.ps1
```

结果：`OfflineBaseline/FormulaComparison/cycles_formula_points.json`、`formula_comparison.json`。后者记录生产 BRDF SHA256、方程误差及全部平面结果。验证器同时运行已有物理光照检查，并断言 D/G、纯金属响应和 Cycles 端点混合精度；本次均通过。
