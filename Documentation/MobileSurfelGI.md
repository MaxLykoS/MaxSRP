# MaxSRP 移动平台 SurfelGI 可行性报告

2026-11-02。目标是主流中端 Android 和 iOS、30 FPS，允许有限的 Compute 软件射线，不以硬件光追为前提。现有 Lightmap 继续承担已支持的非太阳光间接光，SkyVisibility 继续用于程序天空 IBL；拟新增的 SurfelGI 只承担太阳反弹。

**建议推进一个缩小规模的太阳专用 SurfelGI 原型。两个仓库都适合参考，但都不应整套移植。** W298 的光照更新、缓存和回收设计更完整，但依赖 DXR 且默认资源很大；src-dgi 已有软件 BVH，但默认辐射级联规模大，其命中着色尚不足以完成 MaxSRP 所需的太阳反弹。第一版应结合前者的表面缓存管理与后者的软件求交思路，先验证单次太阳反弹，再决定是否加入缓存多跳。

## 当前工程与范围

已移除稀疏体素、共享 Probe、太阳重光照、预烘焙 TOD 光场、固定视角参考比较及对应场景组件。保留原生 Directional Lightmap、Lightmap UV2、光度单位转换、SkyVisibility、动态天空 IBL、太阳及 CSM、Clustered Lighting、云和公共 TAA。

本次完成清理和可行性研究，尚未接入 SurfelGI。当前太阳反弹为空，不会继续运行旧的 VolumeGI 更新。旧 PRT 测试场景、旧 Cubemap 探针辅助脚本及 SH 可视化资源已在后续清理中移除；当前程序天空 IBL、共用的 IBL 计算 Shader 和通用源素材保留。

已有 Lightmap 的定义仍是静态人工灯反弹；程序天空的零次表面反弹项由 IBL × SkyVisibility 表达。**SkyVisibility 是遮挡系数，不等于天空多次反弹。** 本次没有把天空能量重新烘入 Lightmap，也没有将人工灯反弹改成动态 GI。动态角色接收静态人工反弹仍需另外设计，不能从 SurfelGI 太阳分量推断它已经解决。

## 仓库审查版本

| 项目 | 固定源码版本 | 求交方式 | 对当前目标的用途 |
| --- | --- | --- | --- |
| [W298 SurfelGI](https://github.com/W298/SurfelGI/tree/8361942f7d799632b32d37356b8057814456a8a2) | `8361942f7d799632b32d37356b8057814456a8a2`，2024-08-27 | Falcor / DXR、TraceRay，部分直射使用 inline ray query | 参考 Surfel 生成、缓存、更新优先级、睡眠和回收；重写追踪与资源布局 |
| [mxcop src-dgi](https://github.com/mxcop/src-dgi/tree/01fc1465fc296b982c933be5fb051eb21b275b06) | `01fc1465fc296b982c933be5fb051eb21b275b06` | Vulkan Compute 中的软件 BVH | 参考软件遍历、表面维护与空间检索；先不采用整套 Radiance Cascades |

src-dgi 的公开仓库已于 2026-08-10 归档。两者均不是可直接装入 Unity 2022.3 自定义 SRP 的移动插件。W298 对 Falcor 场景、材质、加速结构和 Slang 有依赖；src-dgi 的 Vulkan/Slang 框架也需要转换成 MaxSRP 的 C#/HLSL、资源绑定及跨平台 Compute 路径。

## W298 SurfelGI 的适用部分与代价

### 它提供了较完整的 Surfel 光照缓存流程

根据屏幕覆盖生成表面点，用场景几何信息维护位置和法线；通过空间格查找邻近 Surfel；按估计方差和活跃状态分配路径；路径可以复用已有缓存。积分包含每个 Surfel 的均值/方差历史，也有睡眠、回收和方向距离信息。这些机制比只保存一批表面点更接近实际 GI 系统。

原版硬性检查 `RaytracingTier1_1`，调用 Falcor `Scene::raytrace`，着色器实际调用 `TraceRay`。最终直射 Pass 还使用 `SceneRayQuery`，因此无硬件 RT 路径需要同时替换求交、阴影查询、命中材质和场景绑定，不能只改一处发射函数。[构造与追踪入口](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelGI.cpp#L39-L40)、[追踪 Shader](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelRayTrace.rt.slang#L83-L123)、[最终直射](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGIRenderPass/SurfelGIRenderPass.cs.slang#L43-L54)

### 默认资源配置属于桌面规模

按固定版本的默认容量和字段布局推算，下列主要资源的逻辑申请量约 **784.7 MiB**：

| 资源 | 默认规模 | 推算字节数 |
| --- | --- | ---: |
| RayResult | 150,000 × 64 条容量，每条按 48 B 估算 | 460,800,000 |
| CellInfo | 250³ 个格子，每格 8 B | 125,000,000 |
| Reservation | 250³ 个格子，每格 4 B | 62,500,000 |
| Cell 到 Surfel 关联 | 150,000 × 125 × 4 B | 75,000,000 |
| 两张距离相关 atlas | 3840×2160，R32F + RG32F | 99,532,800 |
| 上述小计 | 不含其余资源 | 822,832,800 |

这是源码配置推算，不是实测 GPU 驻留。48 B RayResult 按十二个 32 位字段计算；宿主 ABI 的最终 stride 仍应在移植时核对。小计还不含 Surfel 本体及副本、加速结构、几何材质、索引和屏幕缓冲。[默认参数](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelGI.h#L62-L75)、[资源分配](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelGI.cpp#L589-L674)、[atlas 格式](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelGI.cpp#L460-L474)

每帧也有全网格维护和 Surfel 邻域扫描，不能只降低射线数就认为系统已经适合手机。移动版本需要紧凑的活跃格或稀疏索引、按更新批次分配的射线结果缓冲，以及明确的候选列表上限。[网格更新](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelUpdatePass.cs.slang#L159-L208)

### 路径数也需要重新制定

960 万是路径结果容量和发射线程上限；超出请求量的 raygen 会提前退出，不能称为每帧必追踪 960 万条有效路径。默认活跃 Surfel 分配 16–64 条路径，睡眠 Surfel 分配 1–4 条。普通路径最多六段、睡眠路径最多十二段，命中着色还会增加阴影射线。手机预算应计算所有求交段及阴影查询。[路径分配](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelUpdatePass.cs.slang#L113-L133)、[发射提前退出](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelRayTrace.rt.slang#L496-L501)、[路径深度](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelRayTrace.rt.slang#L584-L588)

原版命中着色加入 emission 和场景解析灯，miss 采样环境图。MaxSRP 若直接叠加该输出，会重复当前 Lightmap 和天空项。必须建立太阳专用的源项和缓存，不能从完整场景颜色反读光照。[命中来源](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelRayTrace.rt.slang#L371-L390)、[环境 miss](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/RenderPasses/Surfel/SurfelGI/SurfelRayTrace.rt.slang#L443-L449)

## src-dgi 的适用部分与代价

### 软件 BVH 符合不依赖硬件光追的要求

`assets/shaders/shared/bvh.slang` 使用自定义 32 元素栈、AABB 和三角形相交完成遍历，Gather 调用该函数；这不是 DXR 或 Vulkan 硬件 Ray Query。它证明了这套实验的追踪后端可以由普通 Compute 实现，但 Android Vulkan 和 iOS Metal 的编译、原子操作、数据布局及性能仍需各自验证。[软件遍历](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/shared/bvh.slang#L101-L181)、[Gather 调用](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/surfels/gather.slang#L141-L148)、[Vulkan 扩展要求](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/src/wyre/platform/vulkan/hardware/ext.cpp#L14-L20)

### 默认级联规模不能直接带到中端手机

默认六级，首级容量 262,144 个 Surfel，每升一级数量除以四、方向数乘以四，六级总 Surfel 容量为 349,440。各级方向数为 16、64、256、1024、4096、16384；每级方向容量相同，均为 4,194,304，总方向容量 25,165,824。每级各有两张 2048² RGBA16F 纹理，仅 radiance 和 merged 六级总计 **384 MiB**，未计其他缓存。[级联配置](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/src/wyre/platform/vulkan/pipelines/global-illumination/cascade.cpp#L9-L47)、[纹理与缓冲分配](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/src/wyre/platform/vulkan/pipelines/global-illumination/cascade.cpp#L90-L125)

默认 Gather 加载 `gather.slang`，使用整个 2048² 的 dispatch，六级共约 2,516 万线程容量，死 Surfel 会提前退出；这不是有效射线数量的测量。代码中的时间摊销选项默认关闭，不能把可选的 1/16 更新当成默认成本。五层 Merge 还增加约 2,097 万线程容量及邻居/角度样本查询。手机风险包含软件遍历、随机访存和级联合并，不只在射线数量。[默认 Shader 与 dispatch](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/src/wyre/platform/vulkan/pipelines/global-illumination/surfel-gather.cpp#L20-L77)、[时间选项](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/surfels/gather.slang#L69-L109)、[Merge](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/surfels/merge.slang#L73-L155)

Radiance Cascades 合并的是不同距离区间的方向光照，**不等于自动完成更多次表面反弹**。不能把其六级配置宣传成六次反弹。

### 当前命中着色与太阳 GI 的需求仍有距离

审查版本的 Gather 命中直接读取三角形自发光，最高级 miss 读取天空；没有完整的太阳命中照明、材质 BRDF 和缓存多跳路径。要用于当前 MaxSRP，仍需补充太阳可见性、材质反射率、正确的辐照度积分和能量反馈。[命中与 miss 的实际来源](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/surfels/gather.slang#L152-L161)

最终 Composite 自带 ACES，并写回其 albedo 目标。MaxSRP 需要抽取未曝光的线性 HDR 间接光输出，不能把这段最终合成直接叠到现有管线，否则会重复 tone mapping。[Composite](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/surfels/composite.slang#L112-L148)

场景 BVH 初始化后不会按一般几何移动重新构建，上传判断也偏向数量变化。作者维护文章明确说移动 Surfel 所需的 Transform Pass 未实现；因此角色、蒙皮、风动植被及移动遮挡不能因为用了 Surfel 表示就自动得到正确更新。其 Prefix Sum 使用 512 线程工作组，也需要针对目标手机限制调整。[BVH 维护](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/src/wyre/core/scene/bvh-maintainer.cpp#L16-L17)、[Prefix Sum](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/assets/shaders/prefix-sum/prefix_sum.slang#L9-L46)

作者公开的约 6 ms 降至约 1 ms 数字针对 Surfel 生成优化；文章还有六级维护统计，但这些均不是 Gather、Merge、Composite 合计的完整 GI 手机数据，不能据此承诺移动端毫秒数。[作者维护文章](https://m4xc.dev/articles/surfel-maintenance/)

## MaxSRP 的太阳专用实现建议

### 能量归属

| 分量 | 继续使用的路径 |
| --- | --- |
| 太阳直射 | 实时太阳、现有大气透射、CSM |
| 太阳反弹 | 拟新增的太阳专用 Surfel 缓存 |
| 人工灯直射 | 现有 Clustered Lighting |
| 已烘焙的非太阳光间接光 | 现有 Directional Lightmap 与接收材质解码 |
| 天空漫反射 | 现有程序天空 IBL × 已烘焙 SkyVisibility |
| 自发光与天空镜面 | 现有路径 |

首版计算一次太阳表面反弹。射线从接收 Surfel 发出，命中表面后，用该表面漫反射率、太阳入射、太阳遮挡计算源辐亮度，再积分为接收端 `E_sun_indirect / pi`。没有命中时返回零，不向该缓存加入环境天空、人工灯或 emission。最终接收材质在 Lighting 中只应用一次。

后续若加入多跳，反馈只能读取同一太阳分量的缓存。缓存若保存 `E/pi`，需要在命中表面应用一次源材质响应，才能作为该表面的出射辐亮度参与下一跳。GBuffer3 已包含自发光和已乘接收材质的人工反弹，Scene Color 随采样阶段可能包含天空、雾或已曝光输出，均不能直接当作太阳 Surfel 源项。

太阳可见性可以在覆盖可靠时读取现有 CSM；光线命中点可能在相机 CSM 覆盖范围外，必须有明确的软件阴影查询或有效域策略。不能把离屏命中无条件当作无遮挡，也不能把所有 CSM 外位置置黑后声称结果正确。

### 初始实验预算

以下是可调整的工程起点，不是任何仓库或本项目的实测结果：

| 项目 | 建议起点 |
| --- | --- |
| Surfel 池 | 8,192，质量档 16,384 |
| 每帧更新 | 256 / 512 / 1024 个 Surfel，按可见性、年龄与输入变化排优先级 |
| 每次更新 | 1 / 2 / 4 条半球射线，形成可比较的预算档 |
| 每帧主射线 | 由更新数相乘，256–4096；另统计阴影和附加段 |
| 场景 | TinyBistro 静态建筑，完整保留离屏遮挡物 |
| 追踪 | Compute 软件 BVH，优先简化遮挡代理及紧凑材质数据 |
| 重建 | 先建立可核验的原生分辨率 Gather，再单独比较低分辨率方案 |

16,384 个 Surfel 若采用每点 64–96 B 的目标布局，基础池约 1–1.5 MiB；这只是本项目建议的基础字段预算，不是完整 GI 内存。距离/方向信息、历史、活跃格、几何材质和临时缓冲都要单独计入。不得沿用原版数百 MiB 的无关容量。

轮询延迟同样需要核算：16,384 点、每帧更新 512 点，均匀轮询一遍需 32 帧，30 FPS 下约 1.07 秒。可见新生点和太阳变化区域必须优先处理。后续可重新加入北京冬季太阳轨迹作为 ToD 测试输入，太阳变化本身不再触发每时刻预烘焙，但较快的时间倍率仍可能超过缓存更新速度。

### 需要控制的移动端风险

1. **几何与材质。** 简化 BVH 仍需保留门窗、薄墙和 alpha-cutout 的遮挡含义。低分辨率颜色访问不能把镂空变成实体；不再复用旧太阳实验的 2048² 大型材质阵列作为手机默认配置。
2. **表面覆盖。** 屏幕生成的 Surfel 容易缺失离屏反弹源；需要保留近期离屏点并优先补充新区域。只有屏幕空间射线会退化成屏幕空间 GI，不能替代完整场景软件求交。
3. **空间邻域。** 采用有容量上限的活跃格或稀疏索引，并统计哈希冲突、溢出及候选数量。不要按整个大世界体积扫描空格。
4. **动态物体。** 初版先固定遮挡几何；之后再加入有限刚体的局部 BVH 更新。蒙皮、顶点动画、破坏和透明物体分别验收，不能将静态 BVH 结果称为全动态遮挡。
5. **能量和历史。** 世界空间 Surfel 的均值/方差历史可以存在，但它不是第二套屏幕 TAA。太阳突变、表面位移、法线/材质变化、槽位回收均需要局部失效；间接光先在 Lighting 合成，再进入雾、公共 TAA 和曝光。
6. **光照估计。** 先用 Lambert 平面、零光源、强度线性和已知遮挡验证归一化。复用仓库代码不意味着其采样 PDF、缓存量定义与 MaxSRP 的 HDR 光度单位一致。

## 开发顺序与真机判断标准

第一步实现太阳单次反弹的最小链路：GBuffer 生成/维护 Surfel、软件 BVH、命中太阳照明、积分、表面 Gather。先不同时加入六级辐射级联、缓存多跳、动态蒙皮与复杂降噪。

第二步进行数量固定的真机预算扫描。分别记录生成/回收、空间索引、软件遍历、命中着色/阴影、积分和 Gather 的 CPU/GPU 时间，以及资源内存、上传量和候选列表溢出。用关 GI 的同场景作为基线，记录完整帧时间与新增成本；33.3 ms 是整帧预算。

可先为新增 GI 设定约 2 ms 的筛选目标，再按现有管线的实际余量调整。这是项目预算建议，不能当作达标承诺。必须覆盖主流中端 Android 的不同 GPU 厂商及目标 iPhone，并连续运行至少 15–20 分钟观察降频后的 P95 帧时间。

第三步验证新增区域、快速转身、薄墙暗室、低太阳角、ToD 加速、太阳强度归零等条件下的稳定性，再决定多跳与更高质量档。若最小版本的求交/访存已占不合理预算，先减少更新规模、改善代理和调度；不要在没有成本依据时扩大级联和缓存。

**可行性判断：限制场景和更新预算的太阳专用软件 SurfelGI 值得验证；当前资料不能支持“主流中端手机上完整复刻任一仓库即可量产”的结论。** 优先复用 W298 的维护/统计思想和 src-dgi 的软件求交思路，建立符合 MaxSRP 能量归属的小系统，再用真机决定继续投入。

## 授权与维护

两仓库均以 MIT 授权源码，复用代码应保留相应版权和许可证。W298 的 Falcor 子模块及其他依赖另有各自许可；不能将整个依赖树统一视为 MIT。src-dgi 已归档，所需平台适配和错误修复应按自行维护评估。[W298 LICENSE](https://github.com/W298/SurfelGI/blob/8361942f7d799632b32d37356b8057814456a8a2/LICENSE)、[src-dgi license](https://github.com/mxcop/src-dgi/blob/01fc1465fc296b982c933be5fb051eb21b275b06/license.md)

## 本次清理验证

Unity 2022.3.62f2c1 已完成 C# 编译及 TinyBistro 实际 Lighting/天空 IBL 渲染验证：一组原生 Directional Lightmap、26 个有效 Lightmap 接收体、一层 SkyVisibility，零 Missing Script。该验证设备为 RTX 3080 Ti，只验证清理后的工程可用性，不是 SurfelGI 或手机性能测试。

独立检查确认 TinyBistro 仅删除四个实验 YAML 对象块和两条组件/根列表引用；LightmapSettings、SkyVisibility 组件、全部 Renderer 的材质与 Lightmap 索引/ST 保持一致。1,691 个受保护文件哈希复核无变化。验证记录位于 `OfflineBaseline/VolumeGIRemoval/`；未重烘 Lightmap 或天空可见性。
