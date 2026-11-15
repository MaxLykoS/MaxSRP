# Surface16：共用压缩材质契约

本文件保留原 Terrain 工程的逐次技术记录；当前制作/发布入口与包结构见 [README](README.md)。包内源码位于 `Packages/com.maxsrp.terrain`，本项目可写的场景和材质位于 `Assets/Samples/MaxTerrain`，运行载荷仍在 `Assets/StreamingAssets`。下方旧单页/材质实验场景和验证证据属于相邻 Terrain 工程，未作为本包运行依赖打包。

**2026-11-06 单页 RVT 对照已接入 Delta：** 打开 `DirectDeltaSinglePageRvt10km.unity`。DirectDelta 的原材质、shader、常驻控制源保持；Delta 页面共用同一套修正后的压缩材质和评估函数，独立固定 1m 控制／高度快照，包含源法线、三平面陡壁与 Hex，输出两张未压缩 Half 缓存。`V/B/G/U` 分别开关缓存／覆盖诊断／返回页面／重建；F1–F5 显示缓存表面通道，控制 ID/LOD 诊断继续显示直接源。完整规则和实际证据见[单页记录](../../../Terrain/Docs/Validation/DirectDeltaSurface16/SinglePageRvt01/README.md)。原场景保留；下文“无 VT”是此前状态。

**2026-11-05当前材质为 `Surface16HexCorrected.asset`：** `MeshNormalPreparation` 在离线打包时对ID3 Gravel的原始RGB法线归一化，统一旋转平均方向至正Z，再独立生成浮点向量mip和高质量BC7/ASTC完整链。原始PNG保持；其源文件实际为8-bit RGB，解码器亦支持保持16-bit源精度，不执行gamma变换。AH继续引用原文件；其余15个NRAO层在两编码中逐字节相同。新NRAO位于 `DirectDeltaSurface16Ordered/normal-rough-ao-upright-best.*.bin`，旧源/旧资产保留。运行时仍只加载一对1024²×16/11mips数组，GPU载荷及Hex采样数不增加，无VT。[实际压缩GPU、机位对照及自然Play证据](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/NormalUpright04/README.md)。后文覆层排序时“整层搬运、不重压”的说明属于排序步骤；本次ID3法线准备会重压该层，Roughness/AO随BC7联合压缩的量化差异已对原始通道核对，质量未退化。ASTC未手机GPU验收。

**2026-11-05新增粗几何层：** CDLOD根改为5×5个2048m节点，保留32m叶块/最近1m，新增1024m和2048m两级，最粗间距64m。高度Clipmap仍五层、每侧15.993MiB；LOD5/6复用16m高度源，源法线不跟几何变粗。原世界总览400→65实例、819200→133120提交三角；超远测试25实例/51200三角。[几何、接缝与画面对照](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/CoarseGeometry02/README.md)。Hex材质实现保留，未接VT；下文512m根/最粗16m等数字为历史。

**2026-11-05直接Hex Tiling已接入：** 两侧默认开启，Game按H同步开关，Scene跟随组件Hex Tiling选项；参考Mikkelsen作者GitHub固定提交，保留MIT许可。三点随机UV副本、显式梯度和法线斜率旋转均已作GPU对照；继续使用原BC7/ASTC源与有序Height blend，不接VT。[shader diff、前后图及验证](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/HexTiling02/README.md)。平地最多18/三面最多54次材质源采样，手机性能与本轮画质尚未验收。

**2026-11-05当前网格恢复1m：** 按用户要求，将两侧共享patch从128×128降回32×32 quad；32m叶块最近间距1m，512m根块最粗16m，每块1156顶点/2048三角，为此前三角量的1/16。400块总览为819200提交三角/视图。源高度场法线、硬件双线性高度Clipmap、陡壁与材质契约保持，不接VT，不扩展粗根层级。[本轮检查](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/Grid1m01/README.md)。此前0.25m/16900顶点等数字为历史。

**2026-11-05源高度场法线：** DirectDelta与Delta默认在片元位置从最细可覆盖高度层重建中央差分法线，供光照、陡壁投影和材质法线重定向；窗口边缘混合细/粗坡度。近处1m，远处受Clipmap驻留覆盖限制，不声称全世界1m；未接VT。当前基础法线中的面片折痕显著减轻，最终材质仍有部分格纹，2026-11-05用户已验收当前地形观感，手机未验收。[前后图、shader diff与证据](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/SourceNormals02/README.md)。下文此前三角面基础法线描述为历史。

**2026-11-05最新高度路径：** DirectDelta与Delta现在各自持有1295²×5 R16高度Clipmap，均以Bilinear/Repeat和SampleLevel固定mip0读取；级别由数组层选择。每侧高度载荷16,770,250B，两侧合计33,540,500B；DirectDelta不再分配完整常驻高度数组。控制图差异保持，Scene两种预览都使用高度Clipmap，VT继续不接。[验证及本次shader diff](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/DualHeightClip03/README.md)。本次没有实现源高度场平滑法线或双三次高度重建，后文全驻留高度说明仅历史。

**2026-11-05当前实现：** 共享CDLOD网格进一步加密到128×128 quad，32m叶块最近顶点间距0.25m，512m根块间距4m；实际16900顶点/32768三角每patch，为64quad版4倍三角面。1m高度格点/名义2m DEM、父级XZ/Y morph和16MiB高度Clipmap预算保持。两侧共享确定性直接三平面陡壁投影：平地XZ、侧壁YZ/XY，投影权重由几何法线决定；投影UV与显式梯度同步变换，normalmap转到各面世界基并对齐几何法线后混合。每位置仍三个控制点、最多三材质，源采样由平地最多6次增至三面最多18次。未接VT或随机单平面，不称完全复刻三角洲；面法线折面仍存在。[当前GPU/Play记录与原理](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/CliffDirect02/README.md)、[本次shader diff](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/CliffDirect02/shader-change.diff)。下方此前XZ和固定六次源采样描述为历史，须按当前有效投影面数计费。

**2026-11-04当前主数据改为Maladetas。** `DirectDeltaSurface10km.unity`绑定`DirectDeltaMaladetas10km`高度和`DirectDeltaControls16MaladetasV1`控制。实际1m格点来自名义2m真实DEM的限幅双三次重建，Z两侧镜像扩展到10km，保留约1951m高差并使用数据集2048m解码范围。材质分布为8m地貌特征驱动、1m控制输出的程序创作，不是实测类型图。原包没有配套PBR/splat/类别图。[来源、限制与桌面检查](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Maladetas/README.md)。两种控制存储、压缩材质契约和高度混合不变；后文旧合成高度及v5目录描述为历史记录。

2026-11-04覆层顺序修复：主场景改用`Surface16Ordered.asset`、`DirectDeltaSurface16Ordered`压缩源与`DirectDeltaControls16LandscapeV5`控制图。全局ID重新按岩石/砂砾→泥土/林地地表→枯草/草排列；土壤ID8–12，草ID13–15，草土组合必须Bottom=土、Top=草。旧下载编号中草排在前，直接拿它排序曾使草位于泥下，该旧安排不再用于当前主场景。

这个层序在离线制作时确定，不按各张Height图的灰度或相机距离动态排序。TopWeight永远表示Top材质的覆盖率，例如60%草与40%干泥应编码为Bottom9（干泥）、Top14（草）、TopWeight=0.6。换层后必须同步选择正确的覆盖率，不能只交换两个ID。运行时ID直接等于数组slice，没有新增槽映射；原BC7/ASTC整层mip链按新ID离线搬运，名称/UV/法线强度一起重排，不解码重压。新控制图和新材质资产携带同一层序标识，Game与Scene初始化时拒绝新旧误配。

2026-11-04 shader阅读顺序如下。DirectDelta和Delta使用同一个shader，现在几何高度统一走Clipmap，仅通过`DD_CONTROL_RESIDENT`选择控制图存储（原`HEIGHT_CLIPMAP`变体已移除），不是两套独立的材质算法。

1. [DeltaMesh.shader](Shaders/DeltaMesh.shader)：绑定属性、关键字、Game实体与Scene线框/深度三个pass。
2. [MeshGeometry.cginc](Shaders/MeshGeometry.cginc)：高度读取、CDLOD morph、片元入口；材质入口是`Frag → DDShade`。
3. [DeltaSurface.cginc](Shaders/DeltaSurface.cginc)：共享输入结构和主流程，先看末尾`DDShade`，再按调用跳转。
4. [DeltaControls.cginc](Shaders/DeltaControls.cginc)：选择控制源、三个整数Load、三角重心坐标、min/mid/max与诊断opacity。
5. [DeltaProjection.cginc](Shaders/DeltaProjection.cginc)：陡壁投影权重、显式梯度、各投影面的UV和法线基，以及法线重定向。注释区分公开思路与本工程确定性三平面选择。
6. [DeltaHexTiling.cginc](Shaders/DeltaHexTiling.cginc)：每材质/投影的三个随机副本、显式梯度、颜色和法线斜率混合；含作者来源和适配说明。
7. [DeltaMaterial.cginc](Shaders/DeltaMaterial.cginc)：每材质在有效投影面采两张源数组并混合，再逐控制点Height blend、合成最终表面。重点看`DDHeightBlendWeights`的亮带修复顺序。
8. [DeltaLighting.cginc](Shaders/DeltaLighting.cginc)：对混合完成的表面做本工程GGX直射光和ambient。

注释中的`[PPT原文]`保留用户提供的p73 ID提取、p74三角权重和mid/max累加代码，`[PPT公式]`保留p82公式；原文放在对应实现旁的注释中，不重复执行。`[等价改写]`说明变量映射，`[工程实现]`/`[工程约定]`/`[亮带修复]`标识本工程自己的选择。特别注意：PPT有序opacity仍用于F6/F7诊断，当前最终表面使用修复后的逐控制点混合再插值顺序；不能把后者误读成PPT直接提供的代码。

在原 Terrain 工程中打开 `Scenes/DirectDeltaSurface10km.unity`，按Play，或使用 `Tools > DirectDelta Mesh > Open compressed Surface16 scene`。左侧DirectDelta的几何高度和ID/权重图都全驻留，右侧Delta分别使用高度Clipmap和ID Clipmap。两侧共用材质契约、压缩源和shading算法；远处控制密度不同，画面可能有差异。

2026-11-04按用户反馈修订高度与混合：主shading场景改用`DirectDeltaLandscape10km`的新高度源，去掉原来50m/300m周期正弦小丘；新源在16m制作格上构造不规则宽山脊、丘陵和曲线谷地，以双三次插值输出同一份1m量化高度。纯mesh场景与旧`MaxTerrain10km`高度数据仍保留为验收检查点。自然区控制图重新依据新高度生成，绘制草/土、裸岩/碎岩、泥/砂砾等主副材质，制作格的副材质覆盖在35%–45%间缓变，随后沿用插值、top-two和repair。

2026-11-04增加西南角单材质对照区，默认机位展示4×4总览。`8`返回总览，点击格子名称进入近看；`9`近看当前材质，`[` / `]`循环16种材质。每格128m，区域为世界X/Z的[128,640)米；ID从西南角开始，向东递增、向北换行。每个格子内部控制点均为`Bottom=Top=ID，TopWeight=0`，沿用普通地形shader和原始压缩材质。格子之间只有控制三角插值形成的窄边界。

俯视时从上到下的格子顺序如下。

| 西侧 | | | 东侧 |
| --- | --- | --- | --- |
| 12 Forest Ground 04 | 13 Withered Grass | 14 Grass Ground | 15 Leafy Grass |
| 8 Dry Ground 01 | 9 Brown Mud Dry | 10 Brown Mud 02 | 11 Aerial Mud 1 |
| 4 River Small Rocks | 5 Dry River Pebbles | 6 Coast Sand 01 | 7 Damp Sand |
| 0 Rock Face | 1 Rock Surface | 2 Rocky Terrain | 3 Gravel Ground 01 |

两侧的材质数组按屏幕梯度采样。以下按键切换 Game 的通道显示，Scene 预览跟随组件的 `Surface Debug`。Albedo、Height、Normal、Roughness、AO 显示光照前的混合结果。ID 和 Weight 直接取自三角控制点解码及插值结果。

2026-11-03按用户要求移除屏幕LOD驱动的混合参数渐退。2026-11-04因4/4将大量中间权重压为纯材质，改为固定Scale=4、Sharpness=0.5。随后按用户指定机位修正三材质交界亮带：三个控制点各自在原始Bottom/Top覆盖上执行Height混合，再以三角重心权重汇总各材质最终贡献；不再对已经拆分的mid/max opacity分别执行非线性混合。只在同一屏幕位置采最多三种材质、共六次源采样，三个角点复用这些材质Height，不额外采三套纹理。此计算顺序是本工程的亮带修复，不宣称等同PPT先插值权重再混合的顺序。[精确机位验证](../../../Terrain/Docs/Validation/DirectDeltaSurface16/BrightBandFix/README.md)。

Height与其他材质通道均保持普通SampleGrad自动mip；不根据相机或材质LOD改变A、Scale、Sharpness，也不手算混合用LOD，不强制任何材质通道采mip0。权重为0/1的端点仍精确返回0/1；F7继续显示PPT公式的Height混合前有序控制权重，最终shading使用逐控制点混合后插值的贡献系数。

| 按键 | 显示内容 |
| --- | --- |
| F1 / F2 / F3 / F4 / F5 | Albedo RGB / Height 灰度 / 世界法线 RGB / Roughness 灰度 / AO 灰度 |
| F6 | 高度混合前覆盖最多的材质 ID，以固定颜色表示 |
| F7 | 两层上层覆盖权重，分别显示在 R 和 G |
| F8 / F9 / F10 | 底层、上层 1、上层 2 的材质 ID，缺失层显示为黑色 |
| F11 / F12 | 控制 LOD / 光照结果 |

全地图固定16个材质ID，直接对应数组切片，ID同时规定从底到顶的覆盖次序。两个源数组均为1024²、16层、完整11级mip：AH存sRGB Albedo RGB与线性材质Height，NRAO存线性Normal RG、Roughness和AO。材质Height只参与混合，不改变mesh。

运行时只加载当前平台的一套压缩块：桌面BC7，两数组合计44,740,096B（42.67MiB）；Android/iOS准备ASTC 6×6，两数组20,041,216B（19.11MiB）。加载后检查原生GPU压缩格式与颜色空间，释放Unity CPU纹理镜像；不允许悄悄转成RGBA。ASTC已有离线编码/alpha信号证据，尚未真机GPU验收。

每个控制点R16编码：低4bit Bottom ID，次4bit Top ID，高8bit Top覆盖权重。单材质点规范为相同ID、weight0。三个控制点按固定反对角三角插值，解码后对齐最多三种材质，再按固定覆盖次序作高度混合。完整控制源基础间距1m；六级1/2/4/8/16/32m分别生成、检查和修复，不能平均整数ID。

当前控制源位于 `Assets/StreamingAssets/DirectDeltaControls16LandscapeV5`，旧v4/v3/v2控制图保留为历史数据。按2026-11-03的后续要求，DirectDelta一次加载完整10001² R16基础源，纹理载荷200,040,002B（190.77MiB），整个世界固定1m控制密度；没有控制窗口、控制LOD切换或自动生成mip。加载校验SHA和原生R16格式后释放Unity CPU纹理镜像，相机移动不再读取或上传它。设备必须支持至少10001边长的R16纹理，不支持时明确报错，不缩小地图或改用Clipmap。

Delta保持655²×6 R16 ID Clipmap，纹理载荷5,148,300B（约4.91MiB），间距1/2/4/8/16/32m。最粗层固定覆盖世界，其余32采样格对齐、环形增量行列更新；CPU按文件行读取所需条带。两种控制存储沿用同一4/4/8解码及三角插值，均与几何高度存储独立，也不是DTA。以上数字只计纹理载荷，不含加载/上传暂存和驱动分配。

当前控制分布是依现有高度场制作的山地河谷生态，参考[NPS优胜美地地貌资料](https://www.nps.gov/yose/learn/nature/geology-exhibits.htm)，属于地貌参考的程序创作。当前shading场景使用新的平滑合成高度源，原已验收纯mesh高度数据另存保留。规则先在32m制作格上平滑地形特征（仅用于分区，9点二项式核、96m采样间隔，沿X/Z各一次），再提取海拔、坡度、局部凹凸和D8汇流，安排草地/林地、裸岩/崩积碎石、湿泥/河床砂砾；再展开成1m控制图，六级分别检查修复。32m为分布规则的制作尺度，不改变DirectDelta的1m控制存储。对照区保证全部16种材质可独立查看；自然区域按地貌选择材质。旧模块只提供离线原始素材，运行时程序集不引用旧`TerrainResearch`。

本版不开DTA、RVT或SVT，也没有动态材质槽位、高低源拆分及材质缺页回退。源材质全部常驻且在两Game相机间共享；Scene预览使用自己的压缩源，并按DirectDelta/Delta选择常驻控制图或ID Clipmap，额外Editor资源不能算进Game预算。

操作沿用右键 WASD、E/C、Shift、数字 1–6 及 P。新高度场景按 `1` 到(1792,1024)的草土近看点，相机离地10m。按 `7` 到指定机位，旧高度源位置为 `(221.6581, 169.3483, 5159.819)`，新高度源使用相同XZ、地面上40m，旋转为 `(78.105°, -28.775°, 0°)`。该位置的控制分布也改为新地貌规则，旧机位7的草土诊断图属于历史版本。`8`/`9`和`[`/`]`用于材质对照。`Shift + F2–F5` 查看几何 LOD、morph、高度源或格线。Scene 工具条的 Solid 或 Shaded Wire 可查看材质，LOD Wire 显示几何配色。

实现文件：`MeshMaterialSet.cs`负责压缩格式/文件与共享GPU源；`MeshControlLandscape.cs`负责地形相关分布和16格纯材质区域；`MeshControlMap.cs`负责4/4/8、控制源烘焙和Delta的ID Clipmap；`MeshResidentControlMap.cs`负责DirectDelta的完整1m控制图；shader按上面的八步阅读，`DeltaSurface.cginc`组织共用控制读取、两图采样、亮带修复和光照；`MeshComparison.cs`分别绑定两侧高度和控制源；Editor构建器创建独立资产、控制源和新场景。

构建原下载顺序源入口为 `MeshMaterialBuilder.Build(sourceProjectRoot)`，覆层排序入口为 `MeshMaterialOrderBuilder.Build(sourceSet)`，其后由 `MeshNormalPreparation.Build(sourceProjectRoot,orderedSet)` 创建法线校正材质集。新高度源为 `MeshHeightBake.Build(heightDirectory,MeshLandscapeHeight.Recipe)`，控制源为 `MeshControlBake.Build(directory,1,heightDirectory)`，新场景为 `MeshSurfaceSceneBuilder.Build(sourceProjectRoot)`。平常复用已烘焙数据；不要在Game/Scene持有控制文件时覆盖重烘焙。当前按层序排列的压缩块位于 `Assets/StreamingAssets/DirectDeltaSurface16Ordered`，原下载顺序源`DirectDeltaSurface16`保留。

2026-11-03常驻控制图的[桌面定向验证](../../../Terrain/Docs/Validation/DirectDeltaSurface16/Resident16/resident-control-report.json)通过：实际10001² R16、单级mip、CPU不可读；128个GPU原始控制词精确相同，128个三角重建probe的ID误差0、opacity最大误差1.1921e-7；24个移动probe中常驻源读入/上传均为0。两种Scene预览的独立资源、记录与释放通过。三机位×lit/IDs/controlLOD共9组原生对照有效；另9组强制同控制源对照最大差异0，原生lit对照的差异保留，不以相同画质表述。自然Play的36帧、双相机渲染、移动零常驻IO/上传及禁用/重启释放通过；这是桌面功能检查。

当前限制：Delta控制LOD按窗口覆盖硬选一个层，没有跨级平滑；DirectDelta固定完整1m层，两侧均只读三个控制点。IO同步、源高度场法线（远处受Clipmap层级限制）、简单单日光/ambient；已有直接三平面投影，未加入随机投影、VT、阴影、道路、贴花或湿润。移动中的Delta控制切级和整体画质仍待用户验收，Android包内控制/高度加载、10001纹理支持及性能未验证。此前Surface15的49组差0仅属于双方共享ID Clipmap的历史版本。持续状态仍以Docs/Research唯一S00–S17/G01表为准，此版不宣称完整复刻三角洲shading。
