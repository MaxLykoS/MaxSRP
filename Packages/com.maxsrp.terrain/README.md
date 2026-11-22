# MaxTerrain：10240 米地形制作与延迟渲染

地形包为 `com.maxsrp.terrain`，源码位于 `Packages/com.maxsrp.terrain`。`package.json` 声明依赖 `com.maxsrp.render-pipelines.maxsrp`，运行时程序集 `MaxTerrain` 和编辑器程序集 `MaxTerrain.Editor` 显式引用 `MaxSRP.Runtime`。地形通过 MaxSRP 的公共 `MaxRenderBridge` / `IMaxRenderParticipant` 提交几何；MaxSRP 管理 GBuffer、阴影、光照、雾、TAA、曝光和后效。MaxSRP 包不依赖地形包，可单独使用。

包内保存代码、默认运行配置、Shader、原生插件和第三方算法。项目中可编辑的场景、材质和发布记录位于 `Assets/Samples/MaxTerrain`；固定高度、控制图、压缩材质与 VT 载荷保留在 `Assets/StreamingAssets`。地形制作与发布工作流只在这些项目目录写入资源。安装到其他项目时须同时提供对应的项目场景和 StreamingAssets 数据，并为项目配置 MaxSRP 管线资产；仅安装包不会生成 10km 示例数据。

VT 运行配置为包根目录的 `DefaultConfigs/DefaultTerrain.asset`（`MaxTerrainConfig`），两套示例场景的 `MeshComparison.terrainConfiguration` 共用它。PC / Mobile 档位跟随当前 `MaxRendererPipelineAsset.Platform`；每帧页预算 PC 默认 20、Mobile 默认 4，两档均限制为 4–20。物理缓存预算 PC 默认 360 MB、Mobile 默认 64 MB，1 MB = 1,000,000 字节，不包含页表、源材质和合成暂存。预算由 RVT 与 SVT 共用；源数据未准备好时不会等待或强行填满页预算。BC3 保持每批最多 4 页，一帧可以提交多批。修改缓存预算会重建运行期物理池，不要求为容量重新烘焙。

同一配置还控制 Delta / MaxTerrain 的几何密度：

| 参数 | PC 默认 | Mobile 默认 | 作用 |
| --- | --- | --- | --- |
| Geometry Preset | Direct | Budget | 分别沿用 Direct 参照和原 Delta 的整套 LOD 距离、morph 比例及垂直距离度量 |
| Lod Distance Scale | 1 | 1 | 范围 0.25–2；调大让高密度网格保留到更远处，调小提早降级，仍受防裂缝安全下限约束 |
| Minimum Geometry Lod | 0 | 0 | 范围 0–4；最近顶点间距依次为 1/2/4/8/16 米，跳过更细的几何 LOD |

两套预设都使用每块 32×32 quad，默认最近顶点间距为 1 米。Direct 的初始 LOD0 结束距离为 140 米，Budget 为 64 米；后续各级分别沿用原有的距离曲线，安全校验可能抬高实际距离。距离倍率控制高密度网格的覆盖范围，最低几何 LOD 控制最近顶点间距，都不增加高度图采样密度。Direct 左侧参照和制作场景保持原始 Direct 设置；右侧 Delta 跟随当前平台配置，阴影使用相同的几何设置。运行时修改当前档的几何设置会清理旧 morph 历史并使该相机 TAA、缓存阴影失效；不重建 VT、不重烘 SVT。

Delta 在相机所在 mip8 区域中心周围约 400 米全向预取粗精度 SVT 页（0.625 米 / 材质纹素），需求只在跨越 80 米的区域边界时更新，转动相机不清空。周边最多保留 128 个预取目标，与可见请求共用配置的物理池；可见需求优先生产，随后填充周边。预取与 GPU 可见反馈独立，不为隐藏区域分配精细 AVT 页表，也不改变材质混合、材质 UV 或可见细节的 mip 选择。此保底预取尚未实现隐藏区域的中精度 RVT 预合成。Game 中 VT 状态显示当前档位、实际页预算和周边预取驻留进度。

## 两个场景

当前示例素材为壹岐岛：原高度采样点按 XYZ 2.56 倍映射到 1 米网格，7168 米整岛保持 [1416,8584] 的原坐标；地图域扩展到 10240 米，只增加外围海底，不拉伸岛屿或重拟合既有控制点。材质表实际加载 50 层、容量上限 64；原控制数据离线拟合成现有两层控制点和三材质三角。材质混合、Hex、随机投影相位与过滤算法保留，几何/VT 世界边界同步为新尺寸。新控制采用 RGBA8，旧 R16 数据仍可读取。场景文件名中的 `10km` 为兼容旧链接保留。转换、近似边界和重建步骤见 [Iki import](../../Tools/ImportIki/README.md)。

- [DirectDeltaAuthoring10km.unity](../../Assets/Samples/MaxTerrain/Scenes/DirectDeltaAuthoring10km.unity)：DirectDelta 高保真制作场景。Game 相机全屏显示 Direct；道路、静态贴花可以通过独立 GameObject 的 Transform 和 Inspector 编辑。进入 Play 模式后，可在 Game View 查看制作效果。
- [DeltaPublished10km.unity](../../Assets/Samples/MaxTerrain/Scenes/DeltaPublished10km.unity)：冻结发布输入的对照场景。左侧 DirectDelta，右侧 Delta AVT＋RVT/SVT；静态道路、贴花已经合成到 VT 中。场景不保留静态作者对象，加载的是上一次成功发布的固定记录。

两个场景的地形都写入 MaxSRP 的四张 GBuffer，与普通不透明物体共用延迟光照。太阳按 MaxSRP 的 lux 单位设置，摄像机使用宿主天空。Direct 保留独立的三平面高保真材质参照；Delta 使用已接受的随机单投影。潮湿和积水仍暂缓。

## GotGrass 植被

两个场景的初始机位为参照视角 9、草地材质 39（世界 XZ 为 5248、5424 米）。在 Play 中可用原有机位快捷键切回其他地形视角。`MeshComparison.gotGrassSettings` 引用 [GotGrass 配置](Foliage/DefaultConfigs/GotGrass.asset)。示例把泥草材质 19 映射到草种 2，把草地材质 39 映射到草种 14。放置比较使用地形控制图的权重，水位以下或坡度超过配置的地面跳过。作者场景和发布场景的配置工具保留现有植被资产引用。

每个地形相机更新高度和控制数据以后，派发 8 米草块中的世界坐标候选。候选格间距为 25/256 米。草根按最终 XZ 在地形 LOD0 的 1 米三角形内插，并在进入 CDLOD 顶点形变前淡出。默认草视距上限 64 米，每个视口的绘制容量为 262,144 株。草生成读取原地形输入，与 VT 页是否已到达无关。双视口各自准备可见列表，按草种和 7/3 段叶片拓扑执行索引式 indirect draw。

草写入宿主的不透明 GBuffer 和深度，接受 PC 档 GTAO 或 Mobile 档 SSAO。地形仍向宿主提交 CSM；草只录制 GBuffer 绘制。草像素通过共享 TAA 的历史拒绝标志处理风和叶片形变。示例使用 MaxSRP 的 PBR 光照和场景 AO 开关。

两台相机共用风和世界空间 RG16F 位移图。需要交互时，把实例的球或胶囊 `Collider` 指定到 `MeshComparison.gotGrassColliders`。供球和胶囊使用的贴地高度来自 MaxTerrain 高度源，[交互配置](Foliage/DefaultConfigs/GotGrassInteraction.asset)延用 GrassFinal 封版参数。移植依据为 GrassFinal 封版里的 `MaxGrassWorldDisplay.compute` 的 `GenerateGotTile`、`MaxGrassField.World.cs` 的 `UpdateGotGrassWind` 和 `MaxGrassField.GotDisplacement.cs` 的交互与尾迹更新。

## 制作与发布

菜单 `Tools/MaxTerrain/Workflow` 提供：

1. **Configure authoring and published scenes**：核对并配置已经迁入的两个工作流场景，保留已有道路/贴花的编辑结果。
2. **Open DirectDelta authoring**：打开制作场景。
3. **Bake local terrain**：退出 Play 后，从制作场景烘焙新的 SVT 和固定输入。所有校验成功后才更新普通 `TerrainPublication.asset`；失败不会切掉上一次成功的版本。
4. **Open Delta publication validation**：打开发布对照场景，重新进入 Play 加载固定结果。

道路使用可拖动的水平长方形，不包含曲线系统。静态贴花使用独立 GameObject 的投影盒和遮罩。修改作者对象后，发布态需要重新烘焙；这里没有 AssetBundle、运行时热替换或地形专用动态贴花。

旧 Built-in 实验场景不作为 MaxTerrain 的菜单入口或创建模板。原始素材压缩、法线准备等离线工具仍可接受显式源工程路径；正常制作、编辑、发布和运行使用已经迁入的压缩材质载荷，不依赖源工程的原始图片目录。

## 运行数据与渲染

`StreamingAssets` 中沿用既有数据目录名以保留配方和内容身份，包括固定源高度、R16 控制数据、压缩材质数组和当前 SVT 版本。场景、材质、脚本与 Shader 的 GUID 已随模块保留；当前 archive 由 `Assets/Samples/MaxTerrain/Data/TerrainPublication.asset` 指定。

每个相机先准备地形及页面资源，再由宿主按阶段调用地形 GBuffer、ShadowCaster 和 MotionVectors。地形 GBuffer Pass 同时向像素 UAV 写页面请求；四 MRT 下反馈使用 `u4`。压实与异步回读跟随同一相机的 SRP 提交顺序，保持每次 4KiB。世界 VT 使用统一双 BC3 物理池，每页有效 128²、含边框 144²、单物理 mip；每个数组层打包 4×4 页，页表槽位扩为 16 位。按完整数组层向下取整后，360 MB 实际容纳 8672 页 / 359,645,184 B，64 MB 容纳 1536 页 / 63,700,992 B；另有 165,888 B 的四页压缩提交暂存。所有虚拟 mip 共用此容量，不为 SVT 单独分池。

三个页生产 Shader 的历史名称参与 v4 内容身份，迁移时保留它们的名字以复用现有归档；屏幕入口和程序集使用 MaxTerrain。原生 BC3 后端的源码与重建入口在 [Tools/MaxTerrainBc3D3D11](../../Tools/MaxTerrainBc3D3D11/Build.ps1)，运行不依赖原 Terrain 工程。包中的现有原生后端限 Windows x64 / D3D11，平台范围不因本次包迁移扩大。

反馈统计区分4KiB预算造成的正常截断延期与真正的hash/raw插入失败。全屏可有超过511个唯一请求，原有环形输出继续分帧报告；这不增加回读或页池，也不保证每张目标页一帧内就绪。

## 验证入口

- `MaxTerrain.Editor.MaxTerrainMigrationValidation.InspectBatch`：核对独立程序集、MaxSRP 配置、两场景的引用、作者/发布隔离、太阳与摄像机设置及生产 GBuffer Pass。
- `MaxTerrain.Editor.MaxTerrainMigrationValidation.RunNaturalBatch`：带图形设备运行真实 Play；按宿主地形相机渲染帧计数，检查 Direct 提交与 Delta RVT/SVT 反馈、生成、上传和错误，并回读实际 GBuffer/深度，保存材质及最终颜色图。单 Delta 相机阶段另检查共享 TAA 和有效运动向量。
- `MaxTerrain.Editor.MaxTerrainDebugValidation.DebugBatch`：独立短验证，检查正常光照→F1底色→恢复光照的真实最终输出参数与截图，确认诊断关闭曝光、tone mapping与dithering，切换只失效既有共享时间历史。输出由 `MAX_TERRAIN_DEBUG_OUTPUT` 指定，也可沿用 `MAX_TERRAIN_VALIDATION_OUTPUT`。

输出目录由 `MAX_TERRAIN_VALIDATION_OUTPUT` 指定，默认 `OfflineBaseline/MaxTerrain/Import01`。批量运行建议分别启动三个 Editor 进程，设置 `MAX_TERRAIN_VALIDATION_PHASE=0/1/2`：制作态300帧、发布双相机720帧、发布单Delta480帧。三个进程共用一个输出目录；每阶段完成真实GPU检查后直接退出，最后一阶段汇总 `natural.json`，不依赖退出Play后的编辑器转场事件。自然验证不能使用 `-nographics` 或提前 `-quit`。最新通过范围应以该次实际生成的 JSON 和截图为准，工具入口存在不等于验证已经通过，也不代表手机验证。

迁移保留了原 `Terrain` 工程的封板检查点；单一维护的技术状态表仍位于相邻工程的 [GDC2025-DeltaForce-Unity-Implementation.md](../../../Terrain/Docs/Research/GDC2025-DeltaForce-Unity-Implementation.md#shading-tracker)。该外部文档和历史归档不是包的运行依赖。

包拆分之前的 Editor D3D11 检查通过：场景引用/程序集检查、宿主135项及地形63项Shader编译、1500帧主回归与额外720帧显式切镜回归。实际GBuffer、运动和VT数据见[完整验证记录](../../OfflineBaseline/MaxTerrainMigration/README.md)。这些是既有迁移记录，不代表已重跑包拆分后的全部 GPU 回归；未重跑全量烘焙、Player、手机或帧时对比，画面仍需用户检查。

F1/F3、LOD及VT来源等诊断输出使用宿主已有中性输出路径，旁路物理曝光、tone mapping和Bloom。切回光照恢复原配置并重置同一份TAA/曝光历史；没有第二套历史。包拆分前正常→F1→正常的实际GPU截图与资源一致性检查见[Debug01](../../OfflineBaseline/MaxTerrainMigration/Debug01/debug.json)。

## Shader 阅读顺序

1. `Shaders/Geometry/MeshGeometry.cginc`：固定高度Clipmap、CDLOD和当前世界位置重建；DirectReference保留独立副本。
2. `Shaders/VirtualTexturing/World/WorldVtConsumer.cginc`：查页、缓存采样和缺页材质求值；出口统一返回表面属性。
3. `Shaders/Rendering/MaxTerrainGBuffer.cginc`：把表面属性编码为宿主四张GBuffer，区分正常材质与诊断输出。
4. `Shaders/Rendering/MaxTerrainGeometryPasses.cginc`：只重建几何的CSM与上一帧morph运动向量，不写VT反馈。
5. [宿主 PBRLitPass.hlsl](../com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/Lighting/PBRLitPass.hlsl)：统一光照、间接项AO和雾；地形不再自行执行旧简化光照。
