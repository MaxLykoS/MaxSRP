# Bistro 静态人工光 Lightmap

本地产物目录、重新烘焙与提交规则见 [BakeData.md](BakeData.md)。Lightmap 位于 `Lightmaps/`，天空可见度及生成材质位于 `SkyVisibility/`，太阳 GI 位于 `SolarGI/`，均在 `Assets/LocalGIBakes/Bistro/` 下并忽略。

完整 Bistro 使用 Unity 2022.3 Progressive CPU Lightmapper，Directional Lightmap、Mixed Baked Indirect。人工光 Lightmap、SkyVisibility、生成的材质绑定和太阳 TOD Probe 数据集中保存在本地 `Assets/LocalGIBakes/Bistro/`。场景和人工光烘焙配置保存在 `Assets/Scenes/BistroTest/`。太阳间接光由独立的 DDGI TOD Baker 烘焙。

2026-10-28 的验证记录见 `Documentation/GIBakeValidation.md`。其中 TinyBistro 和白盒步骤是历史对照，当前工程使用完整 Bistro。

## 能量归属

| 项目 | 数据及运行时路径 |
| --- | --- |
| 太阳直射 | 实时太阳及 CSM |
| 人工灯直射 | 原有 Clustered Lighting；Mixed 保留直射，纯 Baked 不参与实时点光列表 |
| 人工灯反弹 | Directional Lightmap，接收材质只乘一次 |
| 天空漫反射 | 程序天空 IBL 的 E/pi 乘独立 SkyVisibility |
| 天空镜面反射 | 现有程序天空 IBL，不直接套用标量天空可见度 |
| 灯具自发光 | 可见 emission；代表点光提供照明，Meta 发光为黑，避免重复 |
| 太阳反弹 | 当前未启用；拟评估由 SurfelGI 单独计算，不烘入 Lightmap |
| 移动接收体的人工反弹 | 当前没有烘焙 Lightmap 接收路径，尚需独立方案 |

烘焙点光使用 `LightPhotometry.PointCandela`、线性色彩及色温，强度除以 1000 导出给 Unity。当前 Unity 2022.3 Progressive 原生图保存辐照度 **E**；方向图重建仍作用于 E。最终接收面的出射漫反射为 `E × albedo × kD × (0.01 / pi)`，对应 `MaxLightmapping.DiffuseLightmapToPipeline`。`LightmapToPipeline=0.01` 仍用于输入强度核验和模式 9..11 的原始 E 诊断。只在最终接收边界除一次 pi，不能对原生内部每次反弹再除，也不能把 GBuffer3 的 emission 一并除。天空 IBL 已提供 E/pi，保持其原约定。曝光只在最终 Color Output 执行一次。

该单位约定已通过固定距离入射角、距离控制与独立单次反弹面积积分核验；不是根据房间均值约等于 pi 拟合出的亮度系数。校准时 NoRange 灯仍须使用合理 range：本机 range=1000 的原生衰减数值在近距离出现明显误差，range=10 的 1/2 米及 0/45/60° 控制在解析值约 0.2% 内。内部是否使用某一尺寸 LUT 未由公开源码证明。原生 InverseSquared 的有限范围平滑与实时 `DistanceAtten`、Cycles 的无限反平方也不能混为同一模型；不通过手工放大 GI 倍率掩盖差异。证据见 `OfflineBaseline/GICalibration/Followup/RESULTS.md`。

## 离线流程

`MaxSRP/Bistro/Lightmap` 菜单依次提供准备、Preflight、启动及状态查询。准备不会启动烘焙。`status.json` 在准备时写入 `preparing-uv`（不是 native bake），结束后写入 `prepared` 和本次 preflight；异常写入 `prepare-failed`。模型重新导入可能耗时数分钟，此过程 `isRunning=false` 不代表已经开始或完成 Lightmap。原场景、修改的材质、UV recipe 和导入设置先备份到忽略的 `OfflineBaseline/BistroLightmap/PreparationBackup`。

UV2 使用离线 xatlas 缓存。`Lighting/BistroUvBudget.json` 的 scene recipe 记录源几何和实例缩放/世界面积范围；它不再用 chart 数量或 Unity Pack Margin 估算最终边长。仅对 `BistroExterior.fbx` 关闭默认二级 UV 生成，专用 Postprocessor 严格匹配源数据后应用缓存。最终 `packedSide` 来自外部 manifest。`Assets/ThirdParty/Bistro/LightmapUV/` 下的 `.bytes` 和 manifest 是本机生成的依赖，和 Bistro 源模型一样忽略；其 `.meta` 必须保留并提交。

源数据在 `OnPostprocessModel` 中、UV 拆点及最终 mesh 优化之前捕获，保存原始索引拓扑、几何及顶点属性，并校验 source binary 和属性的 SHA256。两次独立捕获的 1254 个 Mesh 已逐项得到一致哈希；每次使用缓存仍检查当前来源，不能把这次一致性当作永久有效。xatlas 按原始索引连通组件分别 `AddMesh`，避免共位置但不连通的叶片被合并。目标为名义 4 texel/m、2 像素 chart padding，并校验双线性过滤足迹；同 chart 重叠、丢失三角或超尺寸时，仅将有问题的组件拆成逐三角 fallback。若保留密度和保护区仍不能放入单 tile，应报错并拆模型，不静默降低密度或 guard。

完整顺序为：

1. 捕获 before-UV 源数据，核对 source/attributes 哈希及 scene recipe。
2. 显式运行离线 `Tools/Bistro/generate_lightmap_uv.py`，生成并验证 xatlas 缓存及 manifest。
3. 调用 Prepare：`MaxBistroLightmapUV.Ensure` 校验来源并导入缓存，重新取得 Mesh 后设置各接收体的 `GetScale`。
4. 导出实际导入的 UV2 和三角形，离线复核连续重叠、丢失及过滤足迹；通过后才显式启动 native bake。
5. native 完成后检查实际图集绑定、ST、密度和 overlap；通过门槛后自动烘焙 SkyVisibility，再做最终验证和分项捕获。

每个实例使用 `ScaleInLightmap = packedSide / sqrt(instanceWorldArea × 4²)`，将外部缓存的像素布局映射到 native 矩形预算。`ReadRecipe` 负责 scene geometry/instance ranges，实际缓存及导入结果由 Ensure/Validate 检查；Preflight 同时检查 UV、逐接收体 Scale In Lightmap 一致性，以及单 tile 连同 Renderer padding 不超过 2048。Prepare 不自动运行外部生成器，也不启动 bake。模型、实例缩放、密度或缓存变化后重新执行对应捕获/生成/Prepare/验证步骤。固定模型设置 ContributeGI、ReceiveGI.Lightmaps，默认 53 盏实用点光为 Mixed，压力灯组和太阳保持 Realtime。叶片的 native alpha 提取、双面 GI、玻璃裁剪必须与实时材质一致。

64² 三角形内部点采样仅作连续 UV 粗筛，不能证明原生图集中双线性采样足迹不重叠。LightingSettings.lightmapPadding 只隔开不同 Renderer 的 UV tile，不修复一个 Mesh 内部的 chart 间隔。原生烘焙结束后实际调用 Unity HasUVOverlaps(Renderer)，保存 `native_validation.json` 的受影响数量和名称。超过 `max(8, ceil(接收体数量 × 5%))` 时保留 native 产物并停止自动天空烘焙及完成判定；少量薄片/细小 chart 报警保留为警告，仍需人工核对影响范围。该数量门槛是本测试场景的验收策略，不是面积误差或视觉无瑕疵的保证。

初始参数为名义每米 4 texel、最大 2048 图集、512 间接样本、最多 4 次反弹，关闭额外 AO、albedo boost 和间接强度放大。Unity 的烘焙环境显式设黑，避免使用默认天空。报告分别列出 `estimatedSurfaceTexels`（世界表面积 × 4²）、`estimatedNativeRectTexels`（各实例 manifest packedSide² 的总和）和 `estimatedAtlasPackingTexels`（再计入取整与 Renderer padding）；表面采样量不能代表完整图集开销。`estimatedNativeAtlasLowerBound` 仅是面积下界，实际 native packing 可能需要更多图集。准备完成后先离线验证实际导入 UV，不因 recipe、面积利用率或 Preflight 通过就自动启动。参数真实性与图集数量以本次报告为准，不预先承诺耗时或显存。

native packing 后用每个 Renderer 的真实 `unity_LightmapST` 和对应 atlas 宽高复核实际矩形 `ST.x × width` / `ST.y × height`，不能继续把预测边长当真值。审计实际矩形与 manifest packedSide 的比例、chart 间隔/过滤足迹，并按世界面积统计三角形局部密度的 P1/P10 和整体密度；平均达到 4 texel/m 不能证明所有局部表面达到该值。

烘前 53 灯局部可见性审计发现 28 个街灯点光与 2 个店招点光被封闭模型包住。只调整这 30 个演示代表光的位置：街灯水平移出 0.25 米，店招移出 0.10/0.15 米，光强、范围和颜色保持原值，几何仍然参与遮挡。原位置、目标位置及射线统计保存在 `Lighting/PracticalLightPlacement.json`。Builder 和 Prepare 按绝对坐标幂等重放，用户手动调整过的灯不会被覆盖。复测 53 灯没有完全被封闭的点光；这是局部、不含 alpha 几何的烘前检查，不是实时点光阴影实现。

原生 Lightmap 完成后检查颜色/方向贴图及 LightingDataAsset，并通过上述 native overlap 门槛，再自动烘焙天空可见度。它沿用 UV2、图集归一化布局和索引；Texture2DArray 的所有切片使用最大 native atlas 的尺寸，因此末张 native atlas 可以更小。不能要求所有 native atlas 同尺寸，也不能把天空切片降至 native 尺寸以下，缩小 chart 过滤间隔。采用静态场景 BVH 可见性查询，按天空方向余弦权重归一化，无遮挡为 1；不使用有限距离 AO 代替天空遮挡。光栅化、BVH 和累计缓冲仅在 Editor 烘焙时使用，分批执行并提供状态及取消接口。

## 运行时

GBuffer 使用 Unity 逐物体 Lightmap 数据及 `unity_LightmapST`，编译 `LIGHTMAP_ON` / `DIRLIGHTMAP_COMBINED` 变体。方向图中方向长度也承载方向性，不归一化。烘焙漫反射与天空 IBL 共用间接漫反射 Fresnel 权重。

- GBuffer2.r：SkyVisibility，未绑定数据时为 1。
- GBuffer2.g：Lightmap 有效性。
- GBuffer3.rgb：自发光 + 已乘接收材质的人工光间接出射亮度。
- GBuffer3.a：保留材质 AO。
- GBuffer1.a：保留 TAA 运动契约标记。

天空可见度为 Texture2DArray，每个 native Lightmap 索引对应一层。按原材质与图集索引复用材质变体，避免逐 Renderer MaterialPropertyBlock 禁用 SRP Batcher。每相机按所属场景绑定，SceneView 使用活动场景；Preview/Reflection 不使用该场景缓存。

遵守 `Documentation/TemporalAA.md`：光照在线性 HDR 中先合成，再大气雾、天空、共享 TAA、曝光。重烘焙或替换烘焙资源后重置历史。

## 范围与验证

首轮直接烘焙完整 Bistro，后续按用户要求改用 TinyBistro 和独立白盒进行受控核验。烘前检查工程/场景、导入 UV、材质 Meta/alpha/双面、灯单位及归属、真实 LightingSettings、Shader 编译和 SRP Batcher 兼容性。原生及最终报告沿用本次 preflight 的统计和光源单位验证结果。烘后检查所有接收体图集绑定、颜色/方向纹理、有限值和非零反弹，以及 native 报告有效性、图集数量、天空分辨率和资源时间戳；最终 `post_bake_validation.txt` 写入 UTC、native 验证 UTC 和残留 overlap 数量，避免旧报告/旧天空纹理被误认为本次完成。分别输出实时直射、人工反弹、天空漫反射、SkyVisibility 和合成画面。

`_MaxLightingDiagnosticMode` 的 6/7/8 分别显示人工灯烘焙漫反射、自发光、天空可见度。设置须发生在 GBuffer 绘制前，诊断结束恢复 0；数值捕获旁路或清空 TAA 历史。原 1–5 模式保持既有行为。

SkyVisibility 标量会损失天空方向信息。Lightmap 不解决实时局部直射穿墙，也不重建室内镜面反射。更改烘焙人工灯或静态几何后需要重新烘焙。

生成的 LightingSettings、Lightmap、天空可见度、scene recipe 及场景绑定归 `Assets/Scenes/BistroTest/Lighting/`，Tiny 和白盒/诊断副本位于各自子目录；这些项目自产资产随阶段提交保存。第三方源模型/纹理和 `Assets/ThirdParty/Bistro/LightmapUV/` 缓存 payload 保持忽略，只保留其 `.meta`；本流程不读取或扫描 `Library`。阶段提交不代表所有离线烘焙质量问题已经验收通过。
