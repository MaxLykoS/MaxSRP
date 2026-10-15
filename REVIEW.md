# MaxSRP 问题检查与修复记录

更新日期：2026-10-15（动态 IBL 闪黑自查：绘制绑定隔离、异常值隔离与编译状态保护）。

范围：实时 SRP、PBR、IBL、大气和级联阴影。待处理章节沿用原编号，便于逐项处理；已完成的项目集中放在文末按日期排列的“已修复”区域。

待处理：**19**，共 1 项；另保留实机光度/色彩对照与未启用的遗留 GI 工具事项。8、10、18 及运行时同步 IBL 烘焙已移入文末动态天空 IBL 修复记录。

P1 表示优先修复的正确性问题；P2 表示条件性错误、功能未接通或性能问题。画面症状主要由源码和数值分析推导，尚未在 Unity 中做 GPU 截帧验证。

## 19. [P1] Lighting Shader 没有构建引用，Player 可能无法初始化管线

状态：**待修复，本轮新增**。

位置：[MaxLightPass.cs:13](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxLightPass.cs:13)。

Lighting 材质完全依赖 Shader.Find("MaxSRP/PBRLitPass")。本轮检查该 Shader 的 GUID b7b56707e539de6499dfad1cb0cf1464，未发现 Assets/ProjectSettings 中的材质、场景、prefab 或资产引用，也不在 Resources 或 Always Included Shaders 中。ColorOutput 已有的序列化保留机制没有覆盖 Lighting。

触发：按当前资源依赖构建使用这条管线的 Player。编辑器能找到工程内的 Shader，并不保证构建包含它；若被剥离，材质创建或后续 Lighting 绘制会失败。目前 Build Settings 场景列表为空，本轮没有执行构建，不能声称已经复现 Player 黑屏。依据：[Unity 2022.3 Shader.Find](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Shader.Find.html)。

修复：在管线资产中序列化引用 Lighting Shader，并像 ColorOutput 一样检查 null/isSupported；用实际 Player 构建验证保留情况。

## 补充：性能与潜在问题

- **实机光度与色彩对照**：太阳/天空/IBL 已统一入射能量和 lux 参考，见文末修复记录。当前使用 RGB 大气近似和固定 HDR 缩放，尚未做光谱到 XYZ 的完整标定、目标硬件照度对照或相机 EV100/白平衡系统；不能将 CPU 数值检查等同于真实场景的测量标定。
- **PRT/SH 遗留路径未启用**：MaxLightProbeSkybox 未执行 SH 投影，c0..c8 实际上传零；GlobalIllumination.hlsl 的 PRTApprox 绿通道最后一项用 c8.z 而不是 c8.y。当前 PBR 漫反射走 cubemap，不受该 SH 错误直接影响；若恢复 PRT，应单独审查归一化、卷积和系数约定。

## 后续修复顺序

1. 19：保留 Lighting Shader，补足最小 Player 构建验证。
2. 在 Unity 实测动态天空 IBL 的方向、粗糙度、太阳变化与更新耗时，再决定是否需要分帧更新。
3. 后续再接入 cluster lighting；当前保留最多 32 个点光的原路径。

## 复查阶段的验证范围（2026-10-15，修复前）

重新读取当前工作区的相机/输出、GBuffer/PBR/IBL、四级阴影与大气积分及 LUT 代码，并检查资产引用；没有把历史“已修复”记录当作本轮结论。原 8、10、18 仍存在，已更新代码定位并补充无方向光状态。

六个现有脚本在 PowerShell 7.6.5 下通过：ValidateCameraViewport、ValidateColorOutput、ValidateSceneShadows、ValidateShadowCameraIsolation、ValidateAtmosphereGround、ValidateAerialPerspective。先用 Windows PowerShell 5.1 调用时，五项因旧版 Add-Type 编译器/程序集支持失败；换到 PowerShell 7 后通过。这些脚本应使用 pwsh 执行，5.1 的失败不视为 Unity 编译错误。

本轮另做独立数值例核对，确认 Mie 基准差异、正交观察方向角差、PCSS 零 blocker 深度产生无穷半径，以及 GL 采样深度直接反投影的偏差；核对本机 Unity 2022.3 的法线解码实现。该审查阶段未新增或修改测试脚本，未启动 Unity 构建，未做实时 GPU 截帧，不能以现有回归通过证明显示方向、Shader 剥离或上述触发场景已经通过实机验证。全程未访问 Library。

## 已修复（2026-10-13）

本区域记录 2026-10-13 完成的修复，已从上面的待处理清单移除；先前已删除的修复记录不重复加入。

| 原编号 | 修复结果 | 代码位置 |
| --- | --- | --- |
| 4 | 四级连续覆盖相机 near 到 min(camera far, ShadowDistance)，CPU 与 Shader 共同使用累积分界 0.07 / 0.20 / 0.45 / 1.00。采样使用重建位置的相机视空间深度；范围外和空深度返回全亮。正交宽度覆盖光空间 AABB，并预留 texel 对齐余量；远两级 HardShadow 补齐世界位置的 w=1。ShadowDistance 每次更新同步到生成与采样。 | [MaxShadowCasterPass.cs:150](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs:150)、[ScreenSpaceShadowMapPass.hlsl:56](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl:56)、[Shadow.hlsl:150](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:150) |
| 5 | 增加每个采样点到太阳的行星遮挡判断，接入天空单次散射及多重散射 LUT 的直射源项。高空仍按自身地平线保留受光；通用的大气顶端透射率查询继续用于其他路径。 | [RayMarching.hlsl:24](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:24) |
| 12 | Rayleigh、Mie、Ozone 倍率接入对应系数；Mie 倍率同时缩放散射与吸收，保持原有比例。负倍率按 0 处理，1 保持原系数，0 关闭对应介质贡献。 | [Scattering.hlsl:28](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:28) |
| PCSS 补充项 | blocker 计数从整数 0 开始，无 blocker 时在除法前返回有效的 (0, 0)，调用方继续返回全亮；有 blocker 时保留原平均值计算。 | [Shadow.hlsl:105](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:105) |

问题 5 的处理与 Hillaire 作者公开实现的逐点太阳可见性判定一致，参考 [RenderSkyRayMarching.hlsl](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl#L164-L180)。目前采用几何遮挡边界，未加入有限太阳圆盘的半影平滑。

验证：Player/Editor C# 使用本机 Unity 2022.3 托管程序集进行绑定编译（CoreUtils 与 CommandBufferPool 使用接口替身）；22 个 HLSL SM5 入口独立编译通过，覆盖阴影、PBR、IBL 及五个大气 Pass。数值核验通过：1,200 个级联区间内部深度样本、10,000 组 texel 对齐后包围盒覆盖、10,025 个太阳可见性案例与独立射线球求交对照，以及倍率 -1/0/1/2、无/单个/多个 blocker 的边界情况。这些是编译与数值检查，尚未进行 Unity 运行或 GPU 画面对照；全程未访问 Library。

画面复验建议：跨四个分界放置长地面与遮挡物并移动相机；调整 ShadowDistance；降低太阳高度观察地面与高空；分别将大气倍率设为 0、1、2；检查完全无遮挡区域的 PCSS 输出。

## 已修复（2026-10-14）

### 11. 大气步内积分的数值精度改进

天空单次散射、多重散射，以及多重散射 LUT 的 G_2 / f_ms 统一使用进入本步前的透射率和步内积分权重；散射贡献累加完成后再推进 opticalDepth。保留原有 32 步采样及步中介质/光源采样位置。

对本步近似恒定的消光 sigma_t 和光源项 source，贡献为 `T_before * source * (1-exp(-sigma_t*ds))/sigma_t`。实现把小值判断放在无量纲光学厚度 `tau=sigma_t*ds` 上；tau < 0.01 时用泰勒展开计算权重，避免 `1-exp(-tau)` 的消减误差，零消光时权重恰好为 ds。RGB 分量独立处理，兼容某个分量为零的情况。

代码：[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)。处理方式参考 [Hillaire 作者公开实现](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl#L205-L212)。这是离散积分质量改进，步内密度和光源仍采用常量近似。

验证：22 个 HLSL SM5 入口独立编译通过。将实际标量积分函数抽取为 CPU 单精度计算，与稳定双精度参考比较 10,033 个零/极小/较大光学厚度及分支边界案例，最大相对误差约 2.61e-6；零步长、零消光、零光源、混合 RGB 消光和分段常量介质/光源通过。均匀介质总光学厚度 4、单位消光和光源、32 步的结果从 0.92160699 改为 0.98168439，解析参考为 0.98168436；1 / 2 / 64 / 256 步也通过误差检查。这些数值不代表实际天空画面的误差或 GPU 实测精度。

尚未进行 Unity 运行或 GPU 画面对照；全程未访问 Library。后续复验可对照 32 与 128 步的天空和多重散射 LUT，并检查零大气倍率及地平线方向。

### 补充项：HDR 场景颜色、统一曝光与 tone mapping

状态：**输出链路已接通**。

管线现在将物体光照和天空写入同一套线性 ARGBHalf 场景颜色与独立深度缓冲，再由 ColorOutput Pass 统一应用曝光与 ACES 拟合曲线，输出到屏幕或 camera.targetTexture。输出 Pass 同时复制场景深度并保留 alpha。大气最终片元输出使用 float4。

相机按最终目标共享 HDR 颜色/深度：同一显示器的 Game 相机，以及指向同一个 RenderTexture 的相机共用一套；SceneView/Preview 等无 targetTexture 的相机分别持有。Depth / Nothing 相机继续在保留的线性结果上绘制，每次输出重新读取 HDR 原图，避免把已经 tone mapping 的颜色再次作为叠加底图。HDR 缓冲覆盖整个输出目标，GBuffer 保留相机自身尺寸；分屏相机使用 pixelRect。无深度的 RenderTexture 在输出端使用匹配 MSAA 样本数的独立深度。资源在尺寸变化、目标不再使用及管线销毁时释放。

当前管线资产的 **Color Output Settings** 默认设置为 **Tone Mapping = ACES、Exposure EV = -0.5**。曝光倍率为 `2^EV`，+1 档翻倍，-1 档减半；物体与天空共同应用。Tone Mapping = None 仅跳过曲线，曝光仍生效；用于对照或线性 HDR 输出时，应使用浮点、线性的 targetTexture，并按需将 Exposure EV 设为 0。普通 SDR 目标在 None 模式仍可能截断超范围亮值。输出 Shader 已通过资产引用，确保 Player 构建能够保留它。

ACES 采用 [Narkowicz 的拟合曲线](https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/)，是线性 RGB 的紧凑近似，不是完整 ACES 色彩管理或 UE 的全部 Film 设置。当前工程使用 Linear 色彩空间，sRGB 输出目标执行显示编码，Shader 不再重复 pow/gamma 处理；线性 RenderTexture 保留线性数值。此实现提供手动曝光，自动曝光和 HDR 显示器输出校准不在本次实现范围内。

代码：[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxColorOutputPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxColorOutputPass.cs)、[ColorOutputPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl)、[管线资产](<D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset>)。

验证：Player/Editor C# 托管 API 绑定编译通过（Unity 2022.3 程序集，CoreUtils/CommandBufferPool 接口替身），24 个 HLSL SM5 入口独立编译通过。新增 [Tools/ValidateColorOutput.ps1](D:/UnityProject/MaxSRP/Tools/ValidateColorOutput.ps1) 可直接运行：从当前源码抽取输出分组、尺寸、资源方法，在内存图形 API 替身上检查相机共享/隔离、分屏与小数视口、尺寸变化、MSAA 与目标深度变化、分配失败及幂等释放；从 Shader 抽取 ACES 表达式检查 10,001 个输入的单调性/有限性/范围、黑白点、超 1 的颜色、曝光和叠加顺序。另检查光照/天空接线、输出顺序、alpha/深度输出及构建所需的 Shader 引用。

尚未启动 Unity 或做 GPU 画面对照，全程未访问 Library。运行复验应覆盖 Game/SceneView、Depth/Nothing 相机叠加、分屏、窗口缩放、带/不带深度和 MSAA 的 targetTexture；在同一曝光下切换 None/ACES，检查太阳周围与地平线层次，并确认画面方向、深度和 Gizmos 正确。太阳强度标定和问题 8/10 的 IBL 链路仍单独待处理。

### HDR 输出的方向与亮度回归修正

用户在正确工程的 Unity 中反馈相机画面上下颠倒、天空与整体偏亮。之前独立编译与 CPU 数值检查没有覆盖真实 GPU 的投影和纹理方向约定。

修正：GBuffer 与 HDR 场景使用 renderIntoTexture=true 的 GPU 投影；_ProjectionParams.x 从实际投影方向计算。最终全屏输出按目标区分纹理与 backbuffer 的 Y 方向，颜色与深度使用同一 UV；SceneView/Preview 按纹理目标处理。屏幕空间阴影及大气 LUT 的 utility Blit 之后恢复相机矩阵；PostImageEffects Gizmos 切回最终目标的投影约定。顺带修复 unity_MatrixInvP 上传 viewMatrix 的笔误，现上传实际逆投影，已从待处理补充项移除。

默认曝光从 0 EV 调整到 -0.5 EV。当前 ACES 拟合曲线在 0 EV 下将线性中灰 0.18 映射为约 0.2669，而 -0.5 EV 映射为约 0.1748；此调整减轻中间亮度的抬升。曝光仍可在管线资产上修改。这是依据中灰响应的显示基准调整，不是对实际天空光照的完整标定。

验证：C# 托管绑定和 24 个 SM5 Shader 入口独立编译通过；ValidateColorOutput.ps1 加入屏幕/纹理目标、顶/底原点的投影与采样行一致性检查，并检查离屏投影与逆投影接线。真实 Unity 画面仍需刷新后复验；本轮尝试读取运行中的 HDR 数值，但尚未取得 GPU 统计，不能据此声称视觉回归已验证消失。

保留手动诊断菜单 **MaxSRP > Diagnostics > Capture HDR Buffer**。它读取已渲染的主相机 HDR 缓冲（无主相机输出时取其他活动输出），写入系统临时目录的 MaxSRP-hdr-probe.txt，包含亮度范围、非法数值比例、ACES 预测亮度及投影方向；不自动运行。若需要进一步排查偏亮，可先渲染一帧再执行该菜单。全程未访问 Library。

### 天空最终绘制改为显式 Pass

用户的 Frame Debugger 显示 Atmosphere Scattering 只有四次 LUT 绘制，随后直接进入 HDR Color Output，没有最终天空绘制。场景序列化的全局天空材质为空；旧路径临时设置 RenderSettings.skybox、调用内置 DrawSkybox 后又恢复，依赖内置天空材质选择与上下文状态。

现改为显式 **Procedural Skybox** 全屏三角形绘制，直接指定程序天空材质和 HDR 颜色/深度目标，不再修改全局 RenderSettings.skybox，也不再调用内置 context.DrawSkybox。阶段位置为：Atmosphere Scattering（四张 LUT）→ Procedural Skybox（Draw Procedural）→ HDR Color Output。

天空顶点位于远平面，ZTest LEqual、ZWrite Off，仅覆盖没有不透明物体的背景并保留场景深度。观察方向用当前离屏逆 VP 与相机位置重建，正交相机使用逆 view 的前向方向；避免依赖内置天空盒 mesh 或 object transform。太阳方向与单次散射使用同一朝向，并夹取 acos 输入到 [-1,1]。

位置：[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[SkyboxProceduralPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl)、[SkyboxProcedural.shader](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProcedural.shader)。

验证：C# 托管绑定和 24 个 HLSL SM5 入口独立编译通过；ValidateColorOutput.ps1 检查显式材质绘制接线、远平面深度规则、平移/旋转相机的天空射线回投影与正交观察方向，涵盖顶/底纹理原点及普通/reversed Z 模型。实际天空画面和新增 Frame Debugger 事件仍需 Unity 刷新后复验；没有据独立编译推断 GPU 画面已验证。全程未访问 Library。

### 接近海平面时天空出现弧形黑带

用户反馈相机接近 Y=0 时天空出现多条弧形黑带。默认 PlanetRadius=6,360,000 米、SeaLevel=0；这一量级下单精度间隔约 0.5 米，相机高出海平面 0.1 米仍会舍入到行星表面。旧球面求交连续进行大数相减，近交点可能变成零或微小负值，并错误选择行星另一侧的远交点；天空和多重散射只处理 d>0，又会忽略零距离地面命中，继续向行星内部积分。

修正球面求交：以 `(r-radius)*(r+radius)` 计算半径平方差，在开方前判断是否相交；先计算不会发生近似相等数相减的根，再由根的乘积求另一个根。支持非单位方向，返回最近的非负交点。

地面使用独立的固体边界规则：位于地表时，朝下返回零距离命中，朝上或相切不遮挡；起点在地下直接阻断。天空、多重散射及太阳盘统一使用该规则；积分接受 d>=0 截断，并跳过零长度或无效路径。采样高度夹取到非负，避免微小舍入误差产生地下密度。积分内的透射率和多重散射 LUT 查询改为显式 LOD 0，避免新增分支触发隐式纹理导数和循环展开限制。

SkyView LUT、太阳盘和空气透视 LUT 统一调用 AtmosphereCameraPosition。大气计算的相机高度下限为海平面以上 1 米，低于该高度或进入地下时使用此下限；这是近地表的数值边界约定，不会移动实际相机或改变场景几何。保留散射系数、采样步数、太阳强度及曝光设置。

代码：[Helper.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)、[Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl) 及对应天空、太阳盘和空气透视 Pass。

验证：[Tools/ValidateAtmosphereGround.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAtmosphereGround.ps1) 抽取实际 HLSL 函数进行 CPU 单精度计算，对照独立双精度求交，覆盖从地下 100 米到高空 60 千米的 140,910 组高度/方向及 LUT 行，并检查地表朝上/朝下、相切、地下起点、球内出射、未命中、平移球体、非单位和零方向。最大交点距离相对误差约 3.80e-6；贴地的 64 个向下 LUT 行，旧实现有 38 行零距离、15 行远交点，新实现均命中近处地面。共享相机原点、地面截断和零长度保护的接线检查通过；24 个 HLSL SM5 入口独立编译和 ValidateColorOutput.ps1 回归检查通过。

尚未进行实际 GPU 画面对照。Unity 刷新后应在 Y=-1、0、0.1、1、2、10 米连续移动相机，观察地平线及太阳盘；另修改 SeaLevel 检查边界随海平面移动，并检查高处和向下观察时地平线仍正常。全程未访问 Library。

### 太阳圆盘变扁：隔离阴影相机对主相机的修改

用户确认上一版修复后近海平面天空已稳定，但太阳在高于地平线、画面中央时仍呈椭圆，排除了单纯地平线裁切。检查发现阴影 Pass 使用主相机临时渲染四个级联，并将 aspect 改成 1，再恢复保存的数值。Unity 的 [Camera.aspect 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera-aspect.html)明确说明：赋值后宽高比保持固定，直到调用 ResetAspect；恢复数值不能恢复自动跟随视口的状态。这会在窗口或视口比例变化后拉伸物体及太阳盘。

阴影现在使用独立、禁用且不保存的 Camera，复用四次级联绘制，并在 Pass 销毁时释放其 GameObject。级联范围仍从原相机视锥计算；源相机的 aspect、正交状态、位置、旋转、裁剪面、targetTexture、自定义 projection/view 矩阵均不再被临时修改。阴影相机清除从源相机复制的自定义矩阵，使用全幅视口，显式设置 1024×1024 阴影视口。删除旧的主相机参数保存/恢复代码。

新增菜单 **MaxSRP > Diagnostics > Reset Camera Aspect To Automatic**，显式恢复主相机与 Scene View 相机的自动宽高比，用于清除旧代码在当前会话留下的覆盖。修复不会自动重置用户有意指定的 Game 相机宽高比；执行该菜单才会重置主相机的覆盖。

同时处理用户粘贴的 LUT 报错：调用栈在 Blit，Expected 为大气 LUT Shader，Actual 为 Hidden/Internal-Loading。大气数据 LUT 不应使用异步编译期间的占位 Shader 生成数据。Editor 中仅在四张 LUT 烘焙命令范围内调用 ShaderUtil.SetAsyncCompilation(cmd,false)，随后 RestoreAsyncCompilation 恢复原模式；Player 不引入 UnityEditor 依赖，也不修改全局异步编译设置。参考 [Unity 的命令缓冲异步编译控制](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderUtil.SetAsyncCompilation.html)。该处理针对占位 Shader 路径，尚未实测确认日志断言已消失。

代码：[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[MaxColorOutputDiagnostics.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Editor/MaxColorOutputDiagnostics.cs)。太阳角半径和屏幕射线公式保持原实现。

验证：[Tools/ValidateShadowCameraIsolation.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShadowCameraIsolation.ps1) 抽取实际 Execute/Dispose 方法，在图形 API 替身上检查源相机自动/手动 aspect、视口缩放、自定义矩阵、分屏视口、目标纹理、四个级联、隐藏相机复用、剔除失败、异常、空范围、无太阳以及幂等资源释放，并检查 LUT 编译模式作用域。按 Unity 文档模拟 aspect setter，旧实现从 5:4 视口切换到 16:9 后，居中角圆盘宽高比为 1.422；新路径保留自动更新，宽高比为 1。Player/Editor C# 使用 Unity 2022.3 托管程序集绑定编译通过（CoreUtils/CommandBufferPool 接口替身）；ValidateColorOutput.ps1 与 140,910 组近地表求交回归通过。

这是编译、API 模型和几何数值验证，未取得实际 GPU 的圆盘形状或最新报错统计。刷新后可执行一次恢复自动宽高比菜单，再在 Game/Scene View 改变窗口比例，保持太阳位于画面中央并高于地平线，检查圆盘及阴影。全程未访问 Library。

### 13. Lighting Pass 接入大气雾

状态：**功能已接通**；已从待处理列表移除。用户已反馈此前天空稳定性和太阳圆盘修复结果正常，本项画面仍需单独复验。

大气数据准备移至 Lighting 之前，每个相机生成自己的 LUT 并恢复离屏相机矩阵。顺序为：GBuffer → 屏幕空间阴影 → Atmosphere Scattering（透射率、多重散射、天空及空气透视 LUT）→ LightPass（物体光照与雾合成）→ Procedural Skybox → HDR Color Output。最终天空绘制不重复烘焙 LUT。

空气透视保留 32×32×32 的体素规模，按深度切片横向打包为 1024×32 的二维图集。改用一次全屏三角形 MRT 绘制，分别输出线性 ARGBFloat 的 RGB 散射光和 RGB 透射率；保留透射率的颜色差异。透射率直接来自与散射光相同路径的 exp(-opticalDepth)，删除原来的两次顶端透射率查询相除及 RGB 求平均，避免不匹配路径和极小值相除。

LUT 使用该相机实际 renderIntoTexture=true 的逆 GPU 投影与 camera-to-world 矩阵重建射线，覆盖真实 FOV、宽高比和投影翻转。输入采用专用材质矩阵，避免前面的 utility Blit 改写 Unity 内置矩阵。正交相机使用平行射线和各像素在相机平面上的起点；保持此前海平面以上 1 米的最低大气计算高度约定。

深度切片使用 `distance = (slice/31)^2 * maximumDistance`，第 0 层明确为距离 0（S=0、T=1），第 31 层为最大覆盖距离，近处采样更密。消费者按物理距离在相邻深度层间插值，每层内部双线性采样；屏幕坐标夹在该切片的 texel 中心范围内，避免横向边界串到另一层。超过最大距离使用最后一层；这是一套有限距离、有限分辨率的近似，不代表无限远积分。

Lighting 根据 GBuffer 深度重建物体位置，透视相机使用相机到物体的射线长度，正交相机使用向前深度。在线性 HDR 中合成 `surfaceRadiance * RGBTransmittance + RGBInScattering`，之后与天空共同曝光和 tone mapping。空深度背景提前走原有分支，天空不再叠加一次物体雾；Depth/Nothing 叠加相机仅对本次新绘制的物体合成，保留背景已有结果。

管线资产 **Atmosphere Scattering Setting > Enable Aerial Perspective** 默认启用；`_AerialPerspectiveDistance` 默认 32,000 米，表示 LUT 的最大覆盖距离，并非雾浓度。关闭开关或设为 0 会停用物体雾并跳过空气透视 LUT 绘制，天空仍可绘制。雾也可作用于非 Skybox 清屏相机。移除无消费者的旧 AerialPerspective 合成材质及错误的独立合成 Shader 引用；新增透射率图集与既有资源一起释放。

代码：[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[PBRLitPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl)、[AerialPerspective.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl)、[AerialPerspectiveLUTPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUTPass.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)。

验证：[Tools/ValidateAerialPerspective.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAerialPerspective.ps1) 抽取实际 HLSL 的图集坐标、深度插值和 32 步积分，对照独立图集/解析参考：10,000 组屏幕坐标与距离、全部切片边缘、零距离、零介质、RGB 消光和散射、地面截断、线性 HDR 合成、108 组透视和 36 组正交的 FOV/宽高比/纹理原点/Z 约定射线回投影及距离。图集采样最大绝对误差约 3.90e-6，均匀介质 RGB 积分通过 3e-6 的绝对误差检查。检查当前相机数据准备先于 Lighting、背景绕过合成、双 MRT/纹理接线、开关和资源释放。Player/Editor C# 托管绑定、24 个 HLSL SM5 入口独立编译，以及 HDR 输出、140,910 组近地表求交和阴影相机隔离回归通过。

这些是独立编译和 CPU 数值/API 模型验证，尚未取得 Unity GPU 画面对照。刷新后应将物体放在数十米、数百米和数千米处，在同一曝光下切换雾开关，观察远处对比度与色彩变化；改变 FOV、窗口比例、太阳高度及相机高度，检查太阳和天空保持正常，并复验正交、分屏与相机叠加。当前雾不额外查询场景遮挡来生成体积光束，也不增加透明物体渲染功能；这些不属于本次问题 13 的接线修复。全程未访问 Library。

### Scene View 阴影位置及覆盖范围修正

用户反馈 Scene 视图有阴影，但位置或范围不对，Game 视图尚未对照。当前管线已按每个相机重建级联，屏幕空间阴影资源也按相机持有；本次修正这些路径中的矩阵依赖和覆盖计算。

屏幕空间阴影及两次模糊改为显式全屏三角形绘制，指定目标尺寸、Cull Off、ZWrite Off 和 ZTest Always。接收点世界位置及级联深度使用传入相机的专用 inverse VP/view 矩阵，逆 VP 来自与 GBuffer 相同的 renderIntoTexture=true 投影；噪声坐标使用对应相机尺寸。消除 Blit 顶点变换与内置相机矩阵可能被 utility draw 改写的依赖，Scene 相机不读取其他相机的重建矩阵。

级联角点由当前相机实际 CPU 逆投影射线与目标 view-depth 平面求交，再用实际 inverse view 变换到世界空间，支持正交、非对称投影和自定义 view 矩阵。光空间包围盒、texel 对齐与阴影相机统一使用同一 Quaternion.LookRotation/TRS 基底；太阳方向接近竖直时选用不会共线的 up 向量。光空间深度半范围取 ShadowDistance 与包围盒实际半深度加对齐余量的较大值，避免宽视角或负 near 的正交 Scene 相机在 Z 方向被固定范围裁掉。前两级 PCSS 使用各自实际深度半范围进行距离换算。

阴影贴图使用真实的纹理目标 GPU 投影，Caster 顶点通过专用 _ShadowCasterVP 绘制；接收端 PCSS 和 HardShadow 使用同一个 ShadowVP，并统一将 NDC 转换为纹理 UV 的 Y 原点约定。PCSS 对贴图或深度范围外的接收点返回全亮。阴影 Pass 返回是否实际取得有效绘制路径；无太阳、无级联范围或所有级联剔除失败时，当前相机的屏幕阴影清为全亮，避免沿用其他相机结果。

ShadowDistance 仍按当前相机向前深度限制，默认 500 米，不改为以 Scene View 焦点为中心的额外范围。问题 18 的多方向光选择差异仍单独待处理。

代码：[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)、[MaxSceenSpaceShadowMapPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxSceenSpaceShadowMapPass.cs)、[ScreenSpaceShadowMapPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl)、[Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl)、[ShadowInput.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/ShadowInput.hlsl) 及管线调用和 Shader Pass 状态。

验证：[Tools/ValidateSceneShadows.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSceneShadows.ps1) 抽取实际角点、光空间包围盒和阴影相机配置函数，核验 384 个非对称透视/正交/负 near 角点、1,536 个光空间覆盖案例（包括宽视口和竖直太阳）、24 组透视/正交及纹理原点/Z 约定的深度重建，并检查专用相机输入、Caster/receiver 矩阵和纹理行对应关系。阴影相机隔离测试增加实际可用状态和剔除失败检查。Player/Editor C# 托管绑定、26 个 HLSL SM5 入口独立编译（新增显式 Caster 顶点/片元入口），以及 HDR 输出、近地表大气与空气透视回归通过。

尚未取得实际 Scene/Game GPU 截帧。刷新后在 Scene 视图中平移、旋转、缩放并切换透视/正交，再与 Game 视图交替观察同一地面和遮挡物，检查阴影是否贴合物体并覆盖当前相机范围；对照四级边界和 500 米范围外的全亮结果。全程未访问 Library。

### Game 视窗比例随实际尺寸变化：撤销固定比例适配

上一版错误地把用户“避免压缩画面”的要求解释为固定比例居中适配，新增了视窗缩小及黑边。用户未要求固定比例；这些实现已撤掉，对应文档说明和测试也已替换。

Game 相机直接使用实际 pixelRect；有 targetTexture 时使用其完整尺寸。GBuffer 按此视窗分辨率分配，HDR 输出保留完整目标画布以支持分屏/叠加；删除 FitViewport、黑边清理调用及 ViewportBars Shader Pass。1920×1080 的全幅 Game 视窗对应 1920×1080 GBuffer、480×270 屏幕空间阴影和 1920×1080 HDR 输出。

新增统一的有效投影查询：Game 的水平投影系数由实际视窗宽高比计算，保持原垂直 FOV、透视/正交类型、深度投影及 lens shift，通过变化水平视野适配窗口，避免残留 Camera.aspect 覆盖值压缩画面。返回矩阵副本，不修改源相机 aspect、rect 或 projectionMatrix。Scene/Preview/Reflection 保持原投影。

相机剔除矩阵及六个剔除平面、GBuffer/光照/天空矩阵、阴影级联角点、屏幕空间阴影和空气透视 LUT 全部使用同一有效投影，直接上传 Shader 使用的 view、GPU projection、VP 及逆矩阵，避免只修正颜色而让剔除、阴影或雾仍使用旧比例。Gizmos 与最终输出使用真实视窗。

代码：[CameraViewport.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/CameraViewport.cs)、[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[ShaderBinding.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs) 及阴影、大气和输出调用。

验证：[Tools/ValidateCameraViewport.ps1](D:/UnityProject/MaxSRP/Tools/ValidateCameraViewport.ps1) 执行实际布局/投影代码，核验 10,000 组真实视窗、圆形不变椭圆和 GBuffer 尺寸案例，窗口横竖切换、分屏偏移、targetTexture、非 Game 相机隔离及 FOV/lens/depth/源设置保持，并检查有效投影贯穿剔除、绘制、阴影和大气。Player/Editor C# 托管绑定和 26 个 HLSL SM5 入口独立编译通过；HDR、Scene 阴影、阴影相机隔离、近地表大气和空气透视回归通过。

尚未取得实际 Game GPU 画面对照。刷新后应检查 GBuffer 尺寸与实际 Game 视窗一致，窗口比例变化时圆形保持圆形、可见水平视野相应变化，画面内无管线新增黑边，并复验分屏、阴影和雾。Game View 自身的分辨率预设和编辑器面板布局仍由 Unity 控制。全程未访问 Library。

2026-10-15 收尾检查：正交相机的 unity_OrthoParams 宽高也改由实际投影系数倒数取得，不再使用残留的 camera.aspect。视窗测试从实际 ShaderBinding 源码抽取这段参数计算，在同一批 10,000 组比例案例中检查宽高与有效投影一致；Player/Editor C# 绑定及全部现有数值回归通过。当前共享 Editor.log/Editor-prev.log 不属于 MaxSRP，不能用其日志声称本工程的 GPU 刷新已验证。

### Game 画面再次上下颠倒的回归处理（2026-10-15）

用户反馈 Game 画面再次上下颠倒。最近相机与阴影调整新增了 CommandBuffer.SetViewProjectionMatrices，并将已由 GL.GetGPUProjectionMatrix 转换的 GPU 投影传给它，混入已有的显式 SRP 矩阵上传流程。[Unity 2022.3 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetViewProjectionMatrices.html)明确说明该接口不兼容自定义 SRP；这是本次撤销的直接回归点。

删除主相机与阴影 Pass 的两处调用。相机继续直接上传 GPU view/projection/VP 和逆矩阵；阴影 Caster 与 receiver 使用同一个专用 ShadowVP。保留此前 RenderTexture/屏幕目标的输出 UV 约定，颜色与深度仍使用同一采样方向；实际视窗尺寸、无固定比例/无新增黑边的逻辑保留。

代码：[ShaderBinding.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs)、[MaxShadowCasterPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs)。

验证：Player/Editor C# 托管绑定、视窗/正交参数、HDR 输出方向、阴影相机隔离和 Scene 阴影数值检查通过。ValidateColorOutput.ps1 新增检查，禁止在相机与阴影路径中混用该 Built-in 接口，并确认相机及专用 Caster 矩阵直接上传。此前这些测试只检查矩阵数值与采样公式，没有覆盖该 Unity 状态 API 的兼容性，不能用之前通过的 CPU 检查否定用户的实际画面反馈。

尚未取得本次真实 GPU 画面对照，需刷新后复验 Game 与 Scene 的方向、阴影、深度和 Gizmos。全程未访问 Library。

## 已修复（2026-10-15，20～26 与大气 LUT 缓存）

本轮按用户指定处理 20、21、22、23、24、25、26，并优化大气 LUT 更新。以上七项已从待处理区移除；当时剩余的 8、10、18 已由下方动态天空 IBL 记录解决，19 仍待处理。

| 原编号 | 修复结果 | 代码位置 |
| --- | --- | --- |
| 20 | Mie 吸收改为 extinction - scattering，即 (4.440 - 3.996)e-6 /m；倍率同时作用于散射与吸收，总消光恢复地球基准的 4.440e-6 /m。未用曝光补偿这个介质参数错误。 | [Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl:62) |
| 21 | 统一观察方向查询：正交使用 inverse view 的 +Z（指向观察者），透视保留 cameraPosition - position；点光循环只更新光线方向，不再覆盖 V。 | [Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl:8) |
| 22 | 法线解码覆盖 RGB、AG/DXT5nm、BC5 和 ASTC；两处 Shader 的缺省法线改为 bump，与新解码组合得到切线空间 +Z。 | [Packing.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Packing.hlsl:5)、[PBRGBuffer.shader](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBuffer.shader:9) |
| 23 | blocker 距近平面为零时，使用由 Depth24 精度和最小世界距离约束的正分母；滤波 UV 半径限制在 [0,1]，搜索和滤波越界样本按无覆盖处理。保留 count=0 提前返回，两条零分母路径均有边界检查。 | [Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:143) |
| 24 | 四级 DepthNormalBias 接入 PCSS/HardShadow，定义为世界单位，按级联完整深度范围换算为归一化深度，并在掠射角适当放大。reversed-Z 与普通 Z 分别向光源方向偏移。保留独立的 ShadingPointNormalBias 世界位置偏移。 | [Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:105)、[ShadowSetting.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/ShadowSetting.cs:50) |
| 25 | 增加采样深度 [0,1] 与 clip depth 的双向转换，Lighting/屏幕空间阴影反投影共用。PCSS/HardShadow 的比较、距离换算、有效深度范围和 bias 符号均按 UNITY_REVERSED_Z 分支处理。 | [SpaceTransform.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/SpaceTransform.hlsl:6)、[Shadow.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl:86) |
| 26 | 稳定求大气球的完整 entry/exit 区间，只积分与大气及地面裁剪后重叠的部分。maxDis 仍从原观察者计量，正确跳过真空；层外透射率查询移到入射点，无大气相交时返回单位透射率，太阳盘在太空仍可见。LUT 映射约束边界输入，避免负判别式开方。 | [Helper.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl:76)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl:218)、[SkyboxProceduralPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl:26) |

Mie 基准来自 [Hillaire 作者公开的 SetupEarthAtmosphere](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Application/SkyAtmosphereCommon.cpp#L30-L35)，原文件单位 /km，此处已换为 /m。默认 Mie 消光较修复前减小，因此天空、雾及太阳衰减会相应变化；曝光设置保持原值。

### 大气 LUT 改为按依赖失效

代码：[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs:160)。透射率和多重散射全局共享，SkyView 与两张空气透视贴图按相机分别保存。仅需要天空或雾时生成对应数据，命中缓存时仍重新绑定当前相机的雾纹理及布局。

| LUT | 重新生成条件 |
| --- | --- |
| Transmittance | 行星半径、大气高度、介质散射/吸收倍率与密度剖面改变，或纹理丢失。太阳、相机变化不重建。 |
| Multiscattering | Transmittance 已重建、Mie 各向异性改变，或纹理丢失。太阳方向参数化在 LUT 中，不随太阳移动重建。 |
| 每相机 SkyView | 大气版本、有效观察高度、太阳方向/强度/颜色改变，或该相机天空贴图丢失。仅改变朝向、投影或太阳盘角度不重建。 |
| 每相机 Aerial Scattering/Transmittance | 大气版本、观察高度、太阳方向/强度/颜色、实际 GPU 投影、camera-to-world、正交模式或雾最远距离改变，或纹理丢失。 |

太阳方向明确传给缓存判断，避免在 CPU 上读取尚未提交的 CommandBuffer 全局变量。此阶段未改变主光选择；后续动态天空 IBL 修复已统一为 RenderSettings.sun，详见下方记录。

纹理只在需要时分配。完整天空+雾缓存约增加 1.5 MiB/相机；相机销毁时或管线 Dispose 时释放。Editor 的 Scene/Game/Preview 可能在不同 Render 调用中传入，因此不会仅因相机缺席本次相机列表就销毁其 LUT。分配或命令录制失败会使缓存失效；纹理内容丢失后重建。Editor 项目资源变化（包含 Shader 导入）会使缓存失效，退出管线时取消事件订阅。

### 本轮验证

- Player/Editor C# 绑定编译通过：本机 Unity 2022.3 程序集，22 个源文件，CoreUtils/CommandBufferPool 为接口替身；10 处池化 CommandBuffer 分配均有 finally 释放。
- [ValidateShaders.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShaders.ps1)：26 个 SM5 入口，加普通 Z/GL clip 范围模拟、RGB 和 ASTC 的 15 个片元编译组合，共 41 项通过。GL 深度分支是在 HLSL 编译器中模拟，不是原生 GLSL 编译或 OpenGL GPU 验证。
- [ValidateLightingShadowMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateLightingShadowMath.ps1)：抽取当前 Shader 实际函数，四种组合各检查 10,000 组采样/clip 深度往返及投影还原、bias 与比较方向、有限 PCSS 半径；覆盖零距离 blocker、不同 Mie 高度/倍率、中性与倾斜法线、正交平行视线及透视回归。另核对四级 bias 调用和两处法线默认值。
- [ValidateAtmosphereCaching.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAtmosphereCaching.ps1)：完整执行生产缓存类，100 个静止帧及 20 轮双相机交替不产生 LUT 绘制；验证各参数对应的失效集合、缓存命中重绑纹理、关闭/重开雾、纯色背景、纹理丢失、Editor 资源失效、分配/录制失败、相机销毁及幂等 Dispose。
- [ValidateAerialPerspective.ps1](D:/UnityProject/MaxSRP/Tools/ValidateAerialPerspective.ps1)：新增 70 km 层外观察者的真空段、入射点前/后、地面裁剪、无限距离、背向和相切案例；原有 10,000 组打包采样及 144 组射线检查通过。透射率独立检查覆盖层外朝太空、侧向不相交及穿过壳层。
- 原有视窗、HDR 输出、Scene 阴影、阴影相机隔离和 140,910 组近地表检查全部通过；相关接线检查已随新的缓存归属及入口签名更新。

没有启动 Unity 构建、读取 Library 或进行 GPU 截帧；以上不代表实机视觉与耗时已经验证。后续应复验 Game/Scene 方向和正交高光、无贴图材质、四级 bias 调整、太阳变化、海平面与层外天空，并在 Frame Debugger 中确认静止帧不再重建 LUT。非 reversed-Z 的真实平台画面仍需在对应 API 下验证。

## 已修复（2026-10-15，动态程序天空 IBL：8、10、18）

实时管线移除固定室内 cubemap 的启动烘焙。Lighting 前先准备大气，再从独立的环境 SkyView LUT 生成 HDR cubemap、漫反射卷积和 GGX 镜面预滤波，最后一起绑定给 Lighting。天空、物体雾和间接光由同一套大气参数与太阳驱动；所有 IBL 输入均为曝光前的线性辐射，最终仍统一经过现有曝光及 tone mapping。

| 原编号/事项 | 当前结果 |
| --- | --- |
| 8：实时 IBL 的 HDR 格式 | 环境 cubemap、漫反射、全部镜面 mip 和 BRDF LUT 均使用线性 ARGBHalf，环境 SkyView 使用 ARGBFloat。计算结果经数组 UAV 和 GPU CopyTexture 写入 cubemap，全程不经过 8 位像素存储。 |
| 10：程序天空与 IBL 不一致 | 使用同一大气模型生成独立的全局环境；太阳方向、颜色、强度及大气参数改变时更新，环境结果在 Lighting 前生效。原管线资产 m_envMap 已移除。 |
| 18：主光/阴影不一致及无主光残留 | 只使用 RenderSettings.sun；每次 Render 捕获共享太阳数据，直射、阴影、天空、雾和 IBL 使用同一来源。额外方向光被忽略；未指定、禁用、非方向光或 intensity 为零时清零直射颜色和方向，并关闭太阳散射源项及太阳阴影，避免沿用上一帧数据。大气内部保留安全占位方向以防 normalize(0)。 |
| 启动同步 IBL 烘焙 | 活跃管线不再调用旧 BakeAndSubmit，不使用 ComputeBuffer.GetData、CPU 像素数组或 SetPixels/Apply。BRDF LUT 首次渲染时在 GPU 生成，后续环境变化复用。 |

环境观察高度固定为海平面上方 **Reference Height**（默认 2 米，最小 1 米），不跟随 Game/Scene 相机。相机移动或旋转不会改变全局 IBL；各相机背景与空气透视仍按自己的高度和视图计算。这是一个全局环境近似，尚未加入局部反射探针或室内遮挡。

管线资产新增 **Sky IBL Settings**：镜面分辨率默认 128（32～256，取最近二次幂），漫反射固定 32；Diffuse Samples / Specular Samples 默认各 128（32～512）。镜面拥有从基准尺寸到 1×1 的完整 mip，mip 0 直接复制环境，其他 mip 按 GGX 粗糙度预滤波；消费者使用 roughness × (mipCount - 1)。漫反射存储 E/π，与当前 GetDiffuseIBL 直接乘 albedo 的约定匹配。BRDF LUT 为 256×256，每 texel 使用原 IntegrateBRDF 的 1024 样本。

环境中保留天空散射和太阳附近光晕，不加入解析太阳圆盘，避免该高能小光源同时进入 IBL 与方向光 BRDF；可见天空仍正常绘制圆盘。Sun Disk Angle 只改变可见太阳，不触发环境卷积。此阶段太阳颜色使用线性颜色乘 intensity，大气仍另乘原有 SunLightIntensity 与天空色调。这项遗留增益随后导致天空 IBL 过强，已在下方能量统一修复中移除。

环境 SkyView 按太阳、大气版本、参考高度或纹理丢失失效；IBL 按环境源/版本、采样数、分辨率或资源丢失更新。静止帧只重绑纹理，没有 compute dispatch 或 cubemap 拷贝。BRDF 独立缓存，纹理丢失或 Compute Shader 依赖变化时重建；Editor 资源导入会使大气缓存失效。漫反射与镜面双缓冲，整套录制完成后才发布；执行失败保留先前发布的输出并标记重试，分配失败清理已分配资源，Dispose 释放全部纹理并取消事件订阅。

GPU 路径要求 Compute Shader、2D Array 与不同纹理类型间 CopyTexture 支持；不满足时明确报错。当前仍是一次完成更新，太阳连续运动时每帧更新环境卷积，尚未采用跨帧摊销；实际耗时需要在目标硬件量测。点光保留原有最多 32 盏路径，cluster lighting 留待后续。

旧 Runtime/GI 的手动/离线 probe 工具保留，仍含同步回读和 RGBA32 输出；它们已不参与这条实时天空 IBL 路径。问题 8 的关闭范围是当前实时管线，不能解读为这些遗留工具也已改为 HDR。问题 19 的 Lighting Shader 构建引用仍未处理。

代码：[SunLightData.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/SunLightData.cs)、[MaxUpdateGIPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxUpdateGIPass.cs)、[DynamicSkyIBL.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/DynamicSkyIBL.hlsl)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)、[MaxRendererPipeline.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs)、[MaxLightConfigurator.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxLightConfigurator.cs)。

验证：

- [ValidateDynamicSkyIBL.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDynamicSkyIBL.ps1) 执行实际太阳、光配置和 IBL 运行时代码，使用命令/纹理替身检查六面和全部 mip 初始化、HDR GPU 拷贝、100 个缓存帧、整套切换、源/版本/质量变化、BRDF 独立丢失、执行与全部七处纹理分配失败恢复、重复释放；覆盖额外方向光、40 个可见点光、太阳不在可见光列表及各无太阳状态。这不等同于 GPU 像素验证。
- [ValidateSkyIBLMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSkyIBLMath.ps1) 抽取实际 HLSL 采样与卷积循环，对照独立 cube 寻址与解析半球积分：1,200 组 cube/天空 UV 往返、含上下极点的 106 个法线、全部粗糙度 mip 的恒定 HDR 归一化和有限 LOD。解析漫反射最大绝对误差约 0.002412。
- Player/Editor 使用本机 Unity 2022.3 程序集完成 23 个 C# 文件绑定编译（CoreUtils/CommandBufferPool 为接口替身），11 处池化命令均有 finally 释放。[ValidateShaders.ps1](D:/UnityProject/MaxSRP/Tools/ValidateShaders.ps1) 的 30 个 SM5 入口及 15 个深度/法线编译组合均通过，包含四个新增 compute 入口；GL 分支仍是 HLSL 编译模拟。
- 大气缓存、空气透视、近地表、Game 视窗、HDR 输出、Scene 阴影、阴影相机隔离及光照/阴影数值回归全部通过；环境缓存检查增加固定参考高度、相机移动隔离、太阳颜色与禁用太阳的更新条件。

本轮未访问 Library、未启动 Unity 构建或取得 GPU 截帧。刷新工程后，在 Frame Debugger 首次/太阳变化帧应看到 Sky Environment、Sky Diffuse Convolution、Sky GGX Prefilter，BRDF LUT 只在需要初始化时出现；静止帧没有这些计算。用光滑金属球和粗糙漫反射球对照太阳旋转/调色/调强度，切换 Game/Scene 并移动相机，检查反射方向、粗糙度过渡与全局 IBL 稳定性；禁用太阳应清除太阳来源贡献。实测耗时后再确定是否分帧更新。

## 已修复（2026-10-15，太阳/天空/IBL 能量比例与地面太阳衰减）

用户验证动态 IBL 后反馈白色地面被厚重蓝色覆盖。上一版虽然共享太阳颜色与 intensity，大气仍额外乘旧 SunLightIntensity=31.4；该增益通过新 IBL 路径进入物体照明，而太阳直射没有同等增益。背景来源一致不代表能量比例一致。本次删除大气独立的 SunLightIntensity 和 SunLightColor 字段及资产数据，改为唯一的 SolarIrradiance 输入，直射、天空、雾、IBL 共用。

管线资产新增 **Sun Illuminance Lux**，默认 **120000 lux**，表示白色太阳在大气外、垂直光束平面上的照度；指定太阳 Light.intensity 作为乘数，Light.color.linear 作为 RGB 滤色，调色会同时影响全部太阳来源。该值不是地面水平照度；实际地面照度还包含大气透射率、N·L 和场景阴影。颜色为白且 Light.intensity=1 时，该资产值就是入射照度；着色后 RGB 的 Rec.709 亮度权重和可用于读取模型的照度/亮度，彩色滤光会改变实际亮度。

所有太阳来源统一使用固定缩放：`E_internal = E_lux_RGB / 100000`，`L_internal = L_cd/m²_RGB / 100000`。散射相函数带有 sr⁻¹，漫反射 BRDF 含 1/π，天空半球积分返回照度，现有漫反射 IBL 仍存储 E/π；不再额外增加天空倍数。固定缩放在 direct/sky/fog/IBL 和最终 HDR 颜色中一致，用于给现有浮点纹理与曝光留出数值范围。它与相机曝光分开；Exposure EV 仍是相对补偿，+1 加倍，不是物理相机的 EV100。本次保持原曝光 -0.5 和 ACES 不变，也没有对天空去色。

Lighting 的方向光新增 `SurfaceSunTransmittance`：用物体实际世界高度、SeaLevel 和 PlanetRadius 映射到与天空相同的局部行星柱，查询同一 Transmittance LUT，并经过行星遮挡判断。太阳变低时直射随大气路径变暗、偏暖；移除原来按 lightDir.y 硬切太阳的独立分支，高空物体可按自身地平线受光。局部平面世界假设保持不变，水平位置不改变所在行星柱。即使关闭物体雾或相机采用纯色背景，也会准备并绑定太阳透射率与所需介质全局参数；Dispose 解除自己持有的全局透射率贴图。

可见太阳盘也从同一能量推导：均匀圆盘亮度 `L = E / (π sin²α)`，α 为原 Sun Disk Angle（角半径，单位度）；默认角半径仍为原来的 1°，本次不改轮廓大小。圆盘再乘沿观察方向的大气透射率。天空 IBL 仍不包含解析太阳盘，避免重复计算直射。亮度会随角半径改变而保持圆盘积分能量一致；极端强度/极小角半径仍受现有 ARGBHalf 场景目标的有限范围约束。

验证：[ValidateDaylightEnergy.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDaylightEnergy.ps1) 抽取实际 HLSL 的系数、相函数、球求交、32 步透射率/多重散射/天空积分，重建 256×64 Transmittance 与 32×32 Multiscattering LUT，再用独立 48×96 半球求积计算无场景遮挡的水平面照度。白太阳、默认大气、海拔 2 米、120000 lux 入射时结果如下（CPU float 与双线性纹理模型，非 GPU 测量）：

| 太阳高度 | 地面水平直射照度 | 天空照度 | 天空占总照度 |
| --- | --- | --- | --- |
| 90° | 104778 lux | 6213 lux | 5.6% |
| 45° | 70454 lux | 5936 lux | 7.8% |
| 10° | 10362 lux | 4082 lux | 28.3% |

在相同地面直射基线上，单独把天空乘回 31.4，天顶天空占比会变成约 65.1%，解释了原增益为何会造成强蓝色覆盖；这个对照用于隔离增益，不是旧 GPU 帧的精确复现。当前天顶白色理想 Lambert 平面的曝光前 RGB 约为 (0.369, 0.351, 0.335)，不再由蓝色分量主导；这项参考不含介电 Fresnel、镜面或场景阴影。

没有把天空占比强制拟合到上一轮引用的 UE 20% 示例。该值依赖太阳高度、气溶胶、地面反射和天气；当前 RGB 模型没有地面多次反弹/完整光谱计算，与 UE 的示例配置也不同。保留阴影中的蓝天照明与金属反射中的蓝天色。

测试还检查垂直大气柱的解析 Beer–Lambert 透射率、太阳输入加倍时天空严格加倍、0.2725°/1°/2° 圆盘积分能量、夜间行星遮挡、太空无衰减、真空无衰减及关闭太阳无残留；单太阳测试增加 lux 转换与零/负入射照度，缓存测试改用共享太阳能量驱动失效。C# Player/Editor 绑定、45 项 Shader 独立编译、动态 IBL/归一化、大气缓存/空气透视、HDR 输出和光照阴影数值回归通过。

代码：[SunLightData.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/SunLightData.cs)、[MaxRendererPipelineAsset.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxRendererPipelineAsset.cs)、[Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl)、[RayMarching.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl)、[Scattering.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Scattering.hlsl)、[MaxProceduralSkybox.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs)。

未访问 Library 或取得本轮 Unity GPU 对照。刷新后首先在阳光下看白色粗糙地面，再看阴影区；受光区应由直射主导，阴影可偏蓝。改变 Light.intensity 或 Sun Illuminance Lux 应同时改变直射/天空/雾/IBL，改变 Exposure EV 应只改变整体显示亮度。此阶段点光仍保留旧单位、输出仍缺少物理曝光基准；这两项随后造成亮度适配不完整，已在下方补齐。自动曝光、白平衡和 cluster lighting 仍未增加。

## 已修复（2026-10-15，Lighting 全源单位适配、EV100 与场景灯光）

用户反馈物理太阳接入后 Game 画面发黑，并指出其他光源与曝光也需要适配。检查确认：太阳/天空已经使用照度与统一 HDR 缩放，点光仍直接上传无单位的 VisibleLight.finalColor，输出端也只有原有 2^补偿，没有物理相机基准。上一轮白色理想平面照度通过，并不能证明真实材质与最终显示亮度已经闭环。GBuffer 的 albedo 是线性反射率，normal/roughness/metalness 也是材质数据，不能在这里乘 lux 或曝光；本次保持 GBuffer 数据约定和 BRDF 不变。

### 每盏灯只有一个强度来源

删除管线资产 Sun Illuminance Lux 与 Light.intensity 相乘的双参数设计。指定太阳直接把 **Light.intensity 解释为大气外 lux**；点光将 Light.intensity 解释为 **candela 或 lumen**。所有灯共用 Light.color.linear，启用色温时再乘 Unity 的线性色温 RGB，颜色不再由不同路径分别处理。RenderSettings.sun 仍是唯一平行光，其照度同时驱动直射、大气、雾与天空 IBL。

新增 [MaxPhysicalLight.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/MaxPhysicalLight.cs) 和 [MaxPhysicalLightEditor.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Editor/MaxPhysicalLightEditor.cs)：在灯上添加 **MaxSRP / Physical Light**，或 Light 组件菜单的 **MaxSRP Photometric Controls**，可见带 lux/lm/cd 单位的强度输入。该面板编辑的是原生 Light.intensity，没有另外保存一个重复强度；点光切换单位时按 4π 转换数值并保留物理能量，支持 Undo、多选及 prefab 修改记录。组件仅存点光单位，默认 candela；没有该组件的灯也按方向光 lux、点光 candela 的明确约定读取，旧的 1/5/10 不会被自动当成任意亮度倍率。

点光 lumen 表示各向均匀发光的总光通量，`I_cd = Φ_lm / (4π)`；上传 `linearColor × I_cd / 100000`，Lighting 使用 `I/r² × N·L` 得到照度，再乘同一 BRDF 得到亮度。原有 `(1 - r²/range²)²` 有限范围淡出保留，所以接近 Range 时仍是有意的工程近似；一世界单位约定为一米。上传/读取的点光颜色改为 float4，避免低值先被 half 精度截掉；零范围返回零，光源中心使用有界方向计算以避免 normalize(0)。仍保留最多 32 个点光，按用户选择未扩展聚光灯、面光源或 cluster lighting。

### HDR 缩放接到物理曝光

所有太阳、点光和天空/IBL 的场景线性亮度都使用 `L_internal = L_cd/m² / 100000`。Color Output 新增 **Camera EV100**（默认 15）与独立 **Exposure EV** 补偿（默认 0）；实际乘数为：

`100000 / (1.2 × 2^CameraEV100) × 2^ExposureEV`

其中 100000 只用来抵消场景固定缩放，1.2 采用饱和亮度相机标定约定，来源为 [Filament 曝光说明](https://google.github.io/filament/main/filament.html#imagingpipeline/physicallybasedcamera/exposure)。EV100 增加 1 档减半，补偿增加 1 档加倍。该乘数在最后统一作用于光照、天空与雾；相机叠加仍先在线性 HDR 中合成，再输出。没有恢复天空 31.4 倍增益，也没有对白色材质或 GBuffer 单独补亮。

资产当前设置 **Camera EV100=15，Exposure EV=0**，是晴天外景起点；人工灯照明的室内可从 EV100 6～9 调整。现在是手动曝光，没有自动测光或眼适应，因此走入室内或夜间不会自动补偿。HDR Diagnostics 改为调用实际曝光函数，并输出亮度 cd/m²、EV100 和补偿，避免诊断工具继续使用旧显示换算。

### 当前天空场景迁移

[SkyboxScene.unity](D:/UnityProject/MaxSRP/Assets/Scenes/SkyboxScene.unity) 中四盏灯已添加单位组件，并写入显式数值：

| 灯 | Light.intensity | 单位 |
| --- | --- | --- |
| Directional Light | 120000 | lux，大气外 |
| Point Light 1 | 15000 | lumen |
| Point Light 2 | 7500 | lumen |
| Point Light 3 | 1500 | lumen |

点光的数值是本次场景布光起点（按原来 10/5/1 的相对强弱配置），不是实测灯具数据，也不保证与旧任意单位产生相同亮度。保留位置、颜色、Range 与阴影配置。其他旧的 PRT/Offline 测试场景未批量改写；使用实时管线查看它们时应按新约定重新设置灯光与曝光。Unity 中若当前场景仍保留旧内存状态，需要重新加载 SkyboxScene 才能使用磁盘中新的强度和单位组件。

### 验证与限制

[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 执行实际单位转换、光配置器、BRDF 和方向/点光 HLSL：1000 lux 方向光与 1 米处的 1000 cd 点光，在没有大气/场景遮挡的同一材质上得到相同结果；距离加倍贡献约四分之一。覆盖 lumen/candela 转换、色彩/色温只转换一次、上传绕过 finalColor、1000 组距离/范围、光源中心、零范围和负强度。相机测试覆盖完整 EV100 范围、补偿符号和 HDR 缩放抵消。

独立 18% 灰卡参考：100000 lux、EV100=15 时曝光后/tonemap 前约 0.14571；500 lux、EV100=8 时约 0.09325。旧的固定缩放加 -0.5 档补偿会把晴天灰卡压到约 0.04051；本次实际曝光乘数从约 0.7071 改为 2.5431。这体现了相机基准的补全，未改变太阳与天空的物理比例。测试同时核对四盏场景灯的强度/单位组件、fileID 唯一性以及资产 EV100。

Player/Editor 使用 Unity 2022.3 程序集完成运行时代码与新增 Inspector 的绑定编译；Shader 45 项独立编译和现有 HDR 输出、光照/阴影、大气能量、动态 IBL/缓存回归通过。这些仍是 CPU/API 模型与独立编译，未取得本轮真实 GPU 帧，也没有访问 Library。更新后应在 Game/Scene 查看同一灰色/白色漫反射物与金属物，分别调太阳 lux、点光 lm/cd 和相机 EV100；低功率点光在晴天阳光下贡献很弱是合理结果，确认人工灯照明时应选择合适的室内曝光。

## 动态 IBL 接入后的偶发黑帧自查（2026-10-15）

状态更新：**用户先后反馈全屏闪黑与固定在左下角的马赛克消失；随后 Lighting 出现沿砖缝/法线细节的黑斑。** 下方记录数值边界修补，以及对该补丁引入的 BRDF 硬切回归的修正；原闪黑根因仍未由问题帧证实。

用户反馈动态天空 IBL 接入后出现偶发黑色画面。通过 RenderDoc 检查 frame18483：Lighting 前的 GameView RT 确实有大面积黑色并保留部分天空；该纹理在捕获帧内直到 EID 4621 才首次被 Color Output 写入，编辑器在 EID 4658 读取显示。EID 4585 的 Lighting 写入另一张 SceneColorHDR。该帧最终重放正常，只能证明这次重放的最终结果，不能否定运行时的黑闪，也不能直接把初始纹理内容断定为上一帧故障。

随后自查动态 IBL 的数组 UAV、六面拷贝、mip 生成、双缓冲、缓存失效、资源丢失重建和消费链路。当前命令使用同一图形队列，没有异步 compute 队列；不能仅凭 GPU 工作是异步执行就认定缺少 CPU 等待或盲目添加同步回读。发现并修补以下风险：

1. **Lighting 依赖可被其他代码写入的全局 IBL 变量。** 遗留探针工具也会写 _IBLDiffuse、_IBLSpec 与 _BRDFLUT。现在 MaxIBLGIPass 把实际持有的活动输出、BRDF LUT 和 mip 数写入 MaterialPropertyBlock，随 Lighting 的 DrawProcedural 一起提交；保留旧全局绑定用于兼容，当前 deferred draw 的输入由自己持有。测试主动用无关纹理替换全局值，确认绘制绑定仍指向活动输出，更新切换后也跟随新一套纹理。
2. **异常 IBL 值能污染有效直射光。** 旧消费者直接把间接光加到总光照；NaN/Inf 可以让太阳贡献也失效，单纯少一份环境光则不应产生这种结果。新增共享 FiniteIBLColor，环境辐射转半浮点前拒绝非有限/负分量并限制到 65504；漫反射与 GGX 卷积在累加前处理异常样本；Lighting 消费漫反射和镜面 IBL 时再次隔离非有限结果。正常有限的 HDR 数据继续保留，曝光、太阳/点光强度和天空颜色均未修改；移除消费者多余的 half3 镜面中间变量。异常分量会失去相应间接光，不代表源数据错误已经被找出或自动恢复。
3. **Lighting 没有数据 LUT 已采用的 Editor 同步编译保护。** 在实际 Lighting 绘制前后加入局部 SetAsyncCompilation(false)/RestoreAsyncCompilation，防止首次编译或 Shader 重导入时使用加载占位 Shader 写入场景颜色/深度。只覆盖这一条命令缓冲区中的绘制，不修改全局 Editor 编译开关。用户更早提供的日志中有 Hidden/Internal-Loading 与大气 Shader 关键字空间不兼容记录，但它是历史证据，不能据此认定当前闪黑也由异步编译导致。

验证：[ValidateDynamicSkyIBL.ps1](D:/UnityProject/MaxSRP/Tools/ValidateDynamicSkyIBL.ps1) 增加全局绑定被覆盖、按活动输出显式绑定和切换后的绑定更新检查，并核对 DrawProcedural 使用属性块及编译状态作用范围。[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 抽取实际 GetDiffuseIBL/GetSpecularIBL 与直射 BRDF，向漫反射/镜面/BRDF 数据注入 NaN、正负 Inf，确认有效直射仍保留；有限 HDR 和其他正常通道仍参与照明。[ValidateSkyIBLMath.ps1](D:/UnityProject/MaxSRP/Tools/ValidateSkyIBLMath.ps1) 原有面向、归一化和解析卷积对照通过。Player/Editor C# 绑定及 45 项 Shader 独立编译通过，相关 HDR 输出和大气/IBL 回归通过。

代码：[MaxUpdateGIPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxUpdateGIPass.cs)、[MaxLightPass.cs](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Runtime/Passes/MaxLightPass.cs)、[IBLValidation.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/IBLValidation.hlsl)、[DynamicSkyIBL.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/DynamicSkyIBL.hlsl)、[GlobalIllumination.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/GlobalIllumination.hlsl)。

本次没有证明现场曾发生 IBL 全局变量覆盖、NaN/Inf 或加载占位 Shader；以上补丁是针对自查发现的缺口，不能把风险推断写成抓帧证实的根因。未访问 Library，未要求用户重新抓帧；需要继续以实际运行是否再闪黑检验补丁。

## 局部黑色马赛克的数值边界修补（2026-10-15）

用户提供新截图：全屏闪黑已消失，左下角仍有块状黑色异常。截图本身不能确定异常最早发生在 GBuffer、光照、雾还是输出；本轮没有取得该异常区域的逐 Pass GPU 数据，因此不将下列代码缺口断言为截图的已证实根因。

继续自查发现，上一版仅检查 IBL 纹理样本，未覆盖后续计算产生的非有限值。本轮作以下修正：

- **Fresnel 与 BRDF 边界**：直射和间接 Fresnel 的余弦均限制到 [0,1]，五次幂改用乘法，避免浮点误差导致负数底数进入 HLSL `pow`。GGX 使用等价的稳定分母形式，并保护零分母；光线与视线相反时避免归一化零半程向量。漫反射 IBL 在乘材质与 Fresnel 权重后再次检查结果，防止样本检查之后产生的异常污染有效直射。
- **空气透视合成**：对采样后的每个透射率/散射分量检查有效性。非有限透射率使用 1，非有限散射使用 0，使故障雾分量退化为中性效果，保留有效表面光照；其余通道正常合成。有限透射率限制到 [0,1]，散射限制为非负。这不能检测所有有限但错误的纹理内容，也不能替代对异常 LUT 来源的定位。
- **HDR 输出**：ACES 拟合曲线对大于 1 的输入采用代数等价的倒数形式，避免 HDR 乘曝光后因平方溢出发生 `Inf/Inf`。正无穷视作超范围高光映射为白色，NaN 与非正输入归零；普通有限 HDR 保持原曲线。没有通过降低太阳、点光、天空强度或修改曝光来掩盖问题。

代码：[BRDF.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl)、[GlobalIllumination.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/GlobalIllumination.hlsl)、[Lighting.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl)、[AerialPerspective.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/AerialPerspective.hlsl)、[ColorOutputPass.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl)。

验证：45 项 Shader 独立编译通过；光照测试新增余弦越界、零半程向量、GGX 峰值与乘权后异常值；空气透视测试注入 NaN/正负 Inf，确认有效表面光照与其他正常通道保留；输出测试使用实际单精度算法验证极大有限值、正负 Inf/NaN，以及 10001 个普通 HDR 输入与双精度曲线的误差不超过 3e-7。动态 IBL、天空卷积、大气缓存与光照/阴影数值回归通过。这些是独立编译与 CPU/API 模型验证，尚未确认新补丁在 Unity GPU 上消除了截图中的马赛克；未访问 Library。
## 修正上一版 BRDF 引入的法线贴图黑斑回归（2026-10-15）

用户反馈上一版后左下角马赛克不再出现，但 Lighting 输出在砖缝及凹凸细节上出现黑斑。检查发现，上一版为处理退化半程向量，在 `CookTorranceBRDF` 中新增了 `NdotL <= 0 || NdotV <= 0` 时整体返回零的判断；这里的 N 是法线贴图产生的着色法线，不能把它背向 V 等同于整个可见几何表面背向相机。

当几何地面仍然可见，且着色法线满足 `N·L > 0、N·V <= 0` 时，这条提前返回会额外删除原本的直射漫反射，产生沿法线细节突然变暗的边界。新增回归先在旧实现上失败，错误为 `Normal-map N.V horizon discards directly lit diffuse`；撤掉 `NdotV <= 0` 的整体清零后通过。保留 `NdotL <= 0` 判断及半程向量除零保护；Smith 几何项在 `NdotV <= 0` 时仍令镜面贡献为零，漫反射恢复原有计算。没有翻转法线或增加额外补光。

本轮产品代码仅修改 [BRDF.hlsl](D:/UnityProject/MaxSRP/Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl)。[ValidatePhysicalLighting.ps1](D:/UnityProject/MaxSRP/Tools/ValidatePhysicalLighting.ps1) 增加 401 个法线跨越 `N·V=0` 的连续样本、背向视线时漫反射解析对照、相同照度方向光/点光一致性，以及纯金属背向镜面保持零的检查。45 项 Shader 独立编译与光照/阴影回归通过。

已确认并修正上述代码回归；本轮仍未取得截图黑斑像素的逐指令 GPU 证据，实际画面恢复程度待 Unity 运行验证。当前 RenderDoc 打开的捕获只有 LightCookies/UberPostProcess 等事件，没有本管线的 Lighting/大气事件，未把它当作本次截图的证明。未访问 Library。