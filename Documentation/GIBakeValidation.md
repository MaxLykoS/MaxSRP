# GI 烘焙的分层核验

本工具链先在 `Assets/Scenes/BistroTest/GIWhitebox/` 的封闭／开窗两间灰色房间验证，再复测 TinyBistro。不会自动烘焙完整 Bistro，也不修改第三方资源。只烘人工 Mixed 点光的间接反弹；实时直射、太阳和天空 RGB 反弹不在原生 Lightmap 内。

## 场景与输出

- `GIWhiteboxClosed.unity`：两个窗洞均由 0.2m 厚实体封闭，B 房间无人工光通道。
- `GIWhiteboxOpen.unity`：打开 A 的外窗和 A/B 隔墙窗。两份独立场景、原生图集、天空图和报告，避免场景相互遮挡。
- 两间室内净尺寸各 4×3×4m；灰色线性反射率 0.5，非金属、粗糙度 1；A 房间固定 1000lm 白色点光。每个盒子各面为独立 metric UV2，保留厚度、正确绕序和单面材质。
- Native：Progressive CPU、Directional、16tex/m、最大 1024、256 直接／1024 间接采样、4 最大反弹、无压缩、无 AO、黑色烘焙环境。
- 报告：`OfflineBaseline/GIWhitebox/{Closed,Open}/`。有效完成必须核对本轮 runId/UTC、实际 Lightmap 绑定/ST/密度和 native overlap；仅有文件不算通过。

Editor 入口 `MaxSRP.EditorTools.MaxGIWhiteboxDiagnostics`：`Create()` → `Open(false/true)` → `Prepare()` → `StartNative()`；原生完成不会自动启动 Sky。不要在任一烘焙运行时编辑 Assets 源码或刷新程序集。

## 人工间接光分层

使用 `Tools/UnityMcp/CaptureGIBakeStages.cs.txt`，仅允许 Tiny 和两套 Whitebox。通过实际 Camera.Render 和相机自有 GPU 缓冲抓取，暂时旁路离线 TAA 并在 finally 恢复；不读 packed normal 或 depth 为颜色。

当前模式 6/7/9..12 直接读现有 FP32 GBuffer3，导出 `.rgba32f` 和 Float32 EXR；模式0/8保留实际 SceneColor FP16及`.rgba16f`。CSV记录readbackSource/nativeFormat。旧报告的人工光分项曾经先经过SceneColor FP16再提亮，在很暗的人工反弹中会放大通道归零和断色，不能把这类预览彩块当作原生烘焙错误。旧文件保留为历史，不覆盖。生产渲染目标、能量、TAA与原生图集未因诊断读回而改变。

| 模式 | 内容 |
|---|---|
| 9 | 原生 Lightmap 辐照度 E，乘 0.01 HDR 比例，无方向／材质 |
| 10 | 加入方向图响应，使用未加法线贴图的 mesh normal |
| 11 | 加入方向图响应，使用最终 mapped normal，无接收材质 |
| 12 | 未加法线贴图的完整接收材质响应 |
| 6 | 正式的 mapped-normal 人工间接光 |
| 7 | 自发光 |
| 8 | 天空可见度 |
| 0 | 正式最终合成 |

9→10→11→6 用于判断异常第一次出现在哪一层。10/12 的 mesh normal 不是天空烘焙的三角形平面法线。方向图 xyz-0.5 不能归一化；w 保留 Unity 的重平衡约定。分项 PNG 各有独立显示增益，亮度比较须使用原始 RGBA16F/EXR 和 CSV，不能按预览图肉眼比较。

`NativeDecodedEOverPi` 是模式 9 的历史文件名，实际保存 **E × 0.01**；不要按文件名再误判单位。模式 9..11 保留 E，模式 6/12 才执行最终接收面的 Lambert 1/pi 与材质响应。现有 `unitsValid` 检查本身主要证明 LightDataGI 提交强度的比例；单位和输运另由下文独立控制核验。

## 天空可见度 v2

旧版每 texel 只在 UV 中心获取一个世界位置、没有 chart 归属的最近邻补洞、零分母写 1，可能产生白点和跨表面污染。

v2 在 CPU 上裁剪三角形与 texel 的实际交集，产生面积加权空间起点，覆盖亚像素三角形；每条射线从有效起点集合中选择一个。按同 renderer、世界位置和 UV 连通边区分 chart。扩边只接受同 chart 的有效样本；真实冲突拒绝发布，不用平均值掩盖包装错误。GPU 只调度稀疏表面 texel，而非空白图集。

天空图仍是 R8 **标量几何可见度近似**。v2 用接收几何法线的余弦半球采样，方向域与整球 diffuse IBL 一致；无遮挡任意法线为 1，封闭房间为 0，不再以朝下表面的零上半球权重回退全白。该乘子只对均匀环境照明严格匹配；真实蓝天方向分布、地表反弹、镜面遮挡需要方向性 transport 或进一步的数据表示。竖墙＋地面需单独检查，不能把白盒遮挡通过解释成动态天空能量精确。

调试文件每图集一组，原始数组行序 y=0 在底部：

- `sky_coverage_N.u8`：覆盖／扩边／冲突状态。
- `sky_chart_N.i32`：chart 归属，0 无归属、正数 chart、-1 冲突。
- `sky_visibility_N.u8` / `.png`：线性可见度 0..255。
- `sky_surface_stats_N.json`：实际覆盖、chart、空间起点和冲突计数。

## 验收边界

先验证新 GPU kernel 与独立 CPU 逐三角参考、亚像素覆盖、alpha 和正反面，再看封闭／开窗的 Lightmap 与天空图。封闭 B 房间的原生间接应接近 0；开窗后才应收到 A 房间反弹。实时点光尚无阴影，所以最终画面中的实时穿墙不能归因于原生烘焙。

当前未实现 VolumeGI、太阳／天空 RGB 多次反弹、点光阴影或移动端烘焙数据预算。Tiny 高质量旧版已保存在 `OfflineBaseline/TinyBistro/QualityComparison/High_BeforeSkyFix_16tex_2048samples_8bounce_1024sky/`，供同视角复查。
## 2026-10-27 本轮实测

结果与证据见 `OfflineBaseline/GIWhitebox/RESULTS.md`、`validation_summary.json`。Closed/Open 均完成原生 Directional 与 Sky 烘焙，native overlap 均为 0。封闭 B 人工间接均值为 0，开窗后为 0.000680226（烘焙数值域）。32 个独立 float64 逐三角参考点，每点 32768 射线，封闭最大误差 0；开窗最大绝对误差 0.002583。

TinyBistro 原生三份文件及已有 Transform 未改变；天空图 v2 覆盖 655291 texel、0 chart 冲突、0 零有效样本，完成三视角分层捕获。当前仍有很细的白盒墙角纹素亮线，Tiny 部分斑驳在原始模式 9 已存在；这轮没有把它们认定为完全解决。标量全方向可见度仍是均匀环境近似，不据此认定真实动态蓝天能量准确。新旧天空图的平均值变化同时含方向域修订，不能全算作白块修复收益。

## 天空可见度 v3：借鉴 Cycles 的采样和射线处理

- 不透明遮挡默认双面，独立于光栅 `_Cull`、接收面法线和原生材质的 `doubleSidedGI`；alpha cutoff 仍逐命中检查，玻璃排除规则保留。本轮不更改原生人工间接光烘焙的材质设置。
- 每条路径重新选择 texel 与三角形的裁剪交集，并在其中按面积抽取空间起点；不再反复使用固定四个起点。旧缓存路径保留作受控对照；V3 只保留一个兼容起点用于覆盖数据校验。
- 排除 source triangle，沿几何法线偏移 8 ULP，近零坐标使用 `2^-20 m`，替代固定 2 mm；保留同一物体其他三角形的遮挡。共享绝对顶点与一致的投影边测试减少接缝，平面方程计算距离避免巨大薄片的消减误差。这不是对所有世界尺度或所有 GPU 的完备稳健性证明。
- 不引入空间去噪、第二套 TAA 或动态天空 RGB 反弹。R8、UV2/ST、chart 扩边和运行时采样契约保持不变。

`MaxSkyVisibilityV3Diagnostics.Run()` / `Tools/UnityMcp/ValidateSkyV3.cs.txt`：生产 kernel 已验证任意几何法线的无遮挡/封闭、背面和翻转几何、alpha、1 mm 挡板与相邻同材质薄片、逐路径空间采样。1 mm 挡板控制中旧 2 mm 偏移完全漏过，新起点遮挡为零可见度；空间半平面八个种子、每组 8192 路径的 RMSE 为 0.004056。测试只证明这些夹具，不能据此宣称实际场景没有所有漏光。

真实白盒本轮 V3：Closed 2026-10-27T11:31:01Z、Open 11:33:06Z 完成。两者各 32 个检查点、每点 32768 条独立 float64 参考射线；Closed 最大误差 0，Open 最大绝对误差 0.002182（均值 0.000440）。闭室实际画面墙角仍有极少数滤波足迹亮线，需与几何内外混合 texel 问题分别处理，不算完全解决。

TinyBistro 的 TwoSideOnly 于 11:37:18Z、V3 于 11:59:57Z 完成；两者仍为 4096² R8、1024 路径、655291 个覆盖 texel、4270 个 chart、零冲突和零无效 alpha 样本。V3 瞬时 GPU 逻辑存储估算约 201 MiB，不含正常管线、CPU 副本和驱动。三视角比较中，原生模式 9 在接收面内逐像素不变，42 个 Transform、26 个 Lightmap ST 绑定和三个原生文件哈希不变；全 Bistro 四个受保护文件也未变。新增 V3 的运行时间 79s 仅是本机该次记录，不作为通用性能结论。Editor 已恢复 Tiny、TAA 开启、诊断 0、相机 target 为 null；最终控制台无 error。

对照图及原始捕获索引在 `OfflineBaseline/GIWhitebox/SkyV3Comparison/RESULTS.md`；可用 `Tools/CompareSkyVisibilityV3.py` 和 `Tools/ValidateSkyVisibilityV3Artifacts.py` 重做离线核验。白盒残留的 166 个角落像素定位到六个同 chart 的真实覆盖纹素，其世界足迹跨越房间边界与外侧凹口。未见跨 chart/padding 混入；尚未独立计算六个完整面积积分，不把这一定位当作完整边界正确性证明。

## Cycles 人工间接光参考及未决问题

本节记录 SkyV3 阶段的历史发现；其未决的 E/E/pi 和角度问题已经由后续控制分解，当前结论见下一节，原始参考文件仍保留。

使用独立后台 Blender 5.2.2 LTS，导出实际白盒世界几何、法线、UV2/ST、Meta 等效反射率与原生 GPU 色图/方向图。黑环境、纯漫反射、仅 INDIRECT、关闭接收颜色、无降噪和 margin，固定四次反弹；独立直接光灰卡校准点光数值功率，不把流明当瓦特。1024 与 2048 samples 的房间均值变化不超过 0.11%。原数据、输入和哈希均保留。

Closed/Open 的 A 房间原生色图均值约为 Cycles 的 3.1575/3.1566 倍，开窗 B 为 3.0345 倍，封闭 B 两者均为零。这揭示了人工反弹光的能量约定问题，不能凭比例接近 π 就直接修正：独立原生直接光白卡在正入射与 45° 下不保持相同物理余弦比例；CombinedDirectional 和默认 bump 对照没有消除此差异。**本轮保留生产光强、解码系数和原生 Lightmap，不做拟合补偿，也不宣称能量验收通过。**

结果见 `OfflineBaseline/GIWhitebox/CYCLES_REFERENCE_RESULTS.md` 与 `OfflineBaseline/GICalibration/RESULTS.md`。Cycles 的 texel jitter、primitive 自交、背面与材质透明处理来源及与 V3 的实现差异，见 `OfflineBaseline/GIWhitebox/CyclesSamplingNotes.md`，引用 Blender 官方 v5.2.2 固定版本源码。

## 原生与 Cycles 单位、角度的后续核验

固定距离后，原生 0/45/60° 满足余弦响应；此前 `(0,1,0)`→`(1,1,0)` 同时改变了距离，不能单独推断余弦缺失。只将 oracle 的 range1000 改为10、保持 NoRange 类型，旧45°结果从0.491333回到0.352844（解析0.353553）。Standard 材质控制相同，原生 TrainingData 的位置、法线正确，排除了当前自定义 Meta/平法线路径。range10 的 r1 正入射、r1 的45/60°、r2正入射的 raw/E 分别为0.99832/0.99818/0.99806/0.99805。

独立 one-bounce：2m灰色反射板、2cm朝下虚拟 sensor、I=1点光，黑环境，不投射 sensor 自身阴影。256阶面积积分的 sensor 内区 E=0.0593600022650；原生 raw=0.0596098899841，误差+0.42097%；其直接光分项在 sensor ROI 内严格0。Cycles 2048 color-off 得0.0188950584288，匹配解析 E/pi 的误差+0.0009678%，max_bounces1/2/4结果哈希相同。故原生与Cycles分别保存E、E/pi；最终接收面的1/pi须只应用一次，不能干扰前面反弹面的原生输运。

`MaxBakedLighting.hlsl` 已在 mode6/12/正式漫反射边界补理论1/pi；mode9..11的原始E、灯输入、Meta与native文件不变。Tiny三视角GPU验证：rawE、SkyVisibility、Emission逐像素不变，人工出射反弹为旧值/pi（在原生half舍入范围内），TAA/诊断/相机target均恢复。全Bistro、Tiny原生文件、42个Transform和26个ST继续通过哈希/逐值检查。

房间旧对照归一后A房误差约+0.5%，但B房仍约-3.4%；其Native带range平滑、Cycles不带，不能冒称模型已完全一致。独立Open NoRange副本（range仍12）保留旧源资产/图集后复测：A/B均值差为+0.7644%/+1.1912%，对应L1/Cycles为0.9741%/4.0629%。Native AutoFilter与Cycles无降噪仍不同，不宣称逐像素相同。完整索引见 `OfflineBaseline/GICalibration/Followup/RESULTS.md`。边界纹素细线没有在这轮被改几何、改可见度或空间模糊掩盖。

## 2026-10-28 阶段冻结：门上黑斑与待解决项

用户决定暂缓离线 GI 调查，保留当前基线推进后续工作。SkyVisibility V3 保持原样；人工反弹尚未完整验收。正式 TinyBistro 的 108 个文件在本轮对照前后哈希一致，诊断没有替换正式材质、几何、Lightmap 或天空可见度。

- 门上方斜黑斑已存在于原生 MIP0 辐照度，31 点按正确 UV2/ST 双线性重建与 FP32 GBuffer 读回最大相对 L1 误差约 0.041%。它与另一个昏暗雨篷上的 FP16 彩色量化条带是不同问题。
- Auto、关闭去噪/过滤的 None、再关闭墙面接缝拼接的 NoStitch 均保留斜黑斑。只给外立面网格 `Bistro_Research_Exterior_paris_building_05_5151` 的克隆材质开启双面 GI，指定黑斑明显改善。四组使用相同物理灯光、几何、UV、4096 图集、名义 16 texel/m、256 直接/2048 间接样本及最多 8 次反弹。
- 双面 GI 对照并未闭合有效性问题：局部 1840 个覆盖纹素中仍有 1814 个训练 validity 标记为红色。正常独立积分控制为绿色，但 Unity 2022.3 公开训练数据 API 没有定义原始 R/G 通道含义，不能把红色比例直接等同能量错误比例。
- 独立 first-hit 探测发现约 98.6% 的单面背面命中来自上述网格；这支持审查其 GI 背面/闭合语义，不等于已经复现原生多次反弹和无效纹素填补算法。生产 Prepare 当前把 `doubleSidedGI` 绑定到光栅 `_Cull`，会覆盖单独配置；后续应分离两者并重新验收，不能全场强开双面或降低有效性门槛。
- WallTolerance0 原生烘焙完成但产生两张图集，诊断已按实际图集恢复原始数据；其运行时捕获报 `Empty baked receivers or normal output`，不作为通过的视觉对照，也未采用该参数作为修复。
- Auto 与 DoorTwoSided 的完整原生 atlas 和可见 FP32 间接光均无 NaN/Inf、无负 RGB；尚未发现新的数值非法值问题。展示图采用相同固定增益，不作为真实曝光效果或视觉有效性已通过的证据。

可追溯副本保存在 `Assets/Scenes/BistroTest/GIDiagnostics/DoorWall/20261027T163221780897Z/`。详细原始报告与截图位于本机忽略的 `OfflineBaseline/TinyBistro/IndirectQuality/Filtering/20261027T163221780897Z/RESULTS.md` 及 `Gallery/`；工具 `AnalyzeTinyIndirectFilters.py`、`ExportTinyIndirectGallery.py` 用于重新展示已有数据，不触发新烘焙。
