# 原生分辨率 TAA 契约

TAA 是 MaxSRP 的公共基础设施。各 Pass 必须遵守本文件约定；不再为阴影建立独立的时间解析或颜色历史。当前不实现超分辨率、动态分辨率重建或帧生成。

## 算法依据

采用成熟方法的组合：Salvi 方差裁剪、YCoCg 颜色体积裁剪、自适应历史权重、五次双线性采样的 Catmull–Rom 历史重建。当前帧取中心样本；3×3 邻域只估计裁剪范围与方差，不平均当前帧阴影。历史重建仍有滤波足迹，TAA 并不保证所有运动条件下完全无模糊。

- [Salvi, An Excursion in Temporal Supersampling, GDC 2016](https://developer.download.nvidia.com/gameworks/events/GDC2016/msalvi_temporal_supersampling.pdf)
- [Intel TAA reference implementation](https://github.com/GameTechDev/TAA)：仅参考技术组合，不引入其 D3D12 后端；本仓库实现继续兼容当前 D3D11/SM5 路径。
- [Unity 2022.3 motion-vector reference](https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.universal/Shaders/ObjectMotionVectors.shader)：引擎之前帧矩阵、旧顶点及 MotionVectors Pass 的约定。

## 渲染顺序与颜色

`无 jitter 剔除/CSM → jitter GBuffer → MotionVectors → 无 jitter 大气 LUT/IBL → 恢复 jitter 相机矩阵 → Lighting + 雾 → 天空 → TAA → Editor gizmos → 曝光/tone mapping/输出 dithering`

- 场景颜色和历史始终为相同固定光度缩放的线性 HDR；最终曝光不写入历史。只调整最终曝光不要求清空历史。
- 解析内部临时使用有界 HDR YCoCg，完成后还原线性 HDR。历史为 RGBA16F，保留当前源 alpha；RG32F 辅助历史保存眼空间深度（米）与有效累积权重计数。
- ping-pong 历史；禁止在读取同一历史颜色的同时写入它。解析结果先写历史，再拷回当前视窗，最终曝光只有一次。
- 不支持的几何体不能用错误的零速度假装静止。GBuffer1.a=1 声明材质遵守完整运动契约；未声明且没有有效物体运动数据时拒绝历史。

## 投影与 jitter

- 每相机独立的 8 点 Halton(2,3) 序列，单位是原生渲染像素。`_MaxTaaJitter.xy/zw` 为当前/上次 **纹理 UV** 偏移；D3D 的 Y 符号在上传 GPU 投影时统一转换。
- 不修改 `camera.aspect` 或持久的 `camera.projectionMatrix`。GBuffer、运动向量的栅格位置、深度反投影、天空射线使用同一份 jitter 后 GPU VP 与逆 VP。
- CSM 使用原来的无 jitter 投影与 texel snapping。剔除使用无 jitter 视锥并保守扩展一像素，不能让 jitter 造成边缘物体突然被剔除。
- 大气 LUT 的方向采样使用无 jitter 投影，Lighting 查询 LUT 时从当前 UV 减去 jitter。LUT 构建不能使接下来的深度反投影悄悄改回另一套矩阵。
- 离线预览诊断与反射相机旁路 TAA，保持既有数值 baseline 可重复。
- Clustered 点光建表使用无 jitter CPU 投影，片元从 jitter 深度重建的世界位置重新投影查表，不混用 SV_POSITION、输出视窗偏移和 GPU Y 翻转。每相机独立灯表、溢出／范围外回退完整灯列表，不新增时间历史；详见 `Documentation/ClusteredLighting.md`。

## 运动向量

`Motion.xy = 当前无 jitter UV − 上次无 jitter UV`，有符号、视窗归一化单位，存 RGBA16F；Z 是上一位置在上一相机眼空间的正深度（米），W 为有效性（1 有效，-1 拒绝历史，0 表示未由物体 Pass 覆盖）。清屏为 W=0。

- PBR 材质的 `MotionVectors` Pass 与 GBuffer 使用相同 jitter 栅格位置，ZTest Equal、ZWrite Off；通过 Unity 的 `PerObjectData.MotionVectors` 请求逐物体旧矩阵。
- 刚体使用 `unity_MatrixPreviousM`；蒙皮通过引擎 `unity_MotionVectorsParams.x` 和 TEXCOORD4 旧顶点。需要开启 Renderer 对应的运动向量及蒙皮旧位置输出。自定义顶点动画、风、顶点位移必须自行实现相同的上一帧变形。
- ForceNoMotion 对当前几何体拒绝历史。Unity 会省略 Camera-only 对象的物体 MotionVectors 绘制，此时仅在 GBuffer1.a 声明支持时使用深度重建的相机运动；`unity_MotionVectorsParams.w=0` 表示 camera-only，1 表示有物体运动。静止几何体仍须包含相机运动。
- 天空使用方向向量（w=0）重投影，忽略相机平移。正交天空在相机朝向改变时拒绝天空历史。
- **颜色历史是解析后的规范像素网格**：`historyUV = uv − Motion.xy`，不再加入 jitter 差。
- **深度历史是原始栅格位置的数据**：`previousDepthUV = historyUV + previousJitter − currentJitter`。不能把颜色与原始深度历史的坐标约定混用。
- GBuffer2.rg 仍为保留通道，不再被误读为已实现的速度；速度真值来自独立 MotionVectors 纹理。

## 历史有效性和生命周期

- 每相机拥有历史、序列、上一份无 jitter VP、View、投影及 jitter，Game/Scene/Preview 不相互覆盖。
- 尺寸/视窗/投影/输出目标变化、原生 RT 丢失/销毁、超过 2 秒未渲染、明显相机跳变、禁用或旁路均使历史失效。第一次解析不能读取未初始化历史。
- 显式相机切换、传送、场景大幅替换时调用 `MaxRenderPipeline.ResetTemporalHistory(camera)`。自动切换阈值是保护措施，不能代替调用方明确的 cut 信号。
- 越界、前后深度不匹配、无有效上一位置均逐像素拒绝历史。邻域方差用于约束旧颜色；超出局部变化范围的着色变化降低历史权重。
- 静态历史默认上限 0.94，运动时降低，初始阶段按已有计数增加。既不是承诺固定累积 32 帧，也不是两帧平均。
- 只因某相机缺席本次 Render 调用不能清除它的 GBuffer、输出或 TAA 历史。销毁相机时释放；关闭 TAA 时移除历史及本模块增加的 depthTextureMode 位。
- `MaxSceneExposure` 仅在最终 Color Output 按场景覆盖 EV100 / 曝光补偿；Game 按相机所属场景、SceneView 按活动场景选择，Preview / Reflection 旁路。它不切换管线资产或质量配置，不改变 HDR 历史的光度缩放。
- Inspector 验证可同步释放旧管线。生成的 GameObject（隐藏阴影相机等）须经 `MaxRenderResources.Destroy` 释放：编辑模式将销毁移到下一次 Editor 更新，并通过 SessionState 保留跨 domain reload 的待清理 ID；运行时使用 `Object.Destroy`。不得在 OnValidate/渲染回调内直接 DestroyImmediate GameObject。资源所有者引用立即置空，避免旧管线继续使用已退役对象。
- Editor Scene 视图初始、相机或太阳变化后主动刷新至 32 次解析，避免停止操作后没有后续帧可供收敛。

## PCSS 契约

- PCSS 只计算该像素的可见度，原有运行时 2×2 邻域平均已移除。旧实现仅在 Editor 对照宏内保留。
- 启用 TAA 才逐相机推进 PCSS 采样相位；相位使用与投影 Halton 不同的 R2 序列。关闭 TAA 保持固定世界相位，避免无时间解析时产生持续闪烁。
- 不修改太阳角、世界半影半径、CSM 稳定拟合来掩盖噪声。硬接触边与软半影应分别与高采样参考比较。
- Shadow map 的 XY 栅格误差须独立验证：增加 PCSS 次数或累计同一张深度图的多个相位不能恢复漏掉的轮廓。方向光四级共享一张最大 4096×4096 的深度 atlas；2×2 固定 tile，当前资产每 tile 为 2048×2048、含每侧 2 texel 保护区，有效分辨率 2044×2044。Stable CSM、viewport、kernel padding 和 texel snapping 使用有效分辨率，不使用整张 atlas 尺寸。总覆盖仍为 500 米。
- 使用 UE 同类等比区间分布：`DistributionExponent` 为相邻区间长度比，默认 2，对应 1:2:4:8；替代旧固定权重与首级单独上限。相邻级重叠拟合与可见度混合保留，末级淡出保留。Inspector 修改通过管线重建清空旧 TAA 历史；不新增阴影历史。
- `PcssCascadeCount` 默认为 2，近两级保留 PCSS，远两级使用 3×3 tent 加权硬件比较 PCF；交界仍混合两侧可见度。PCF 核是阴影纹理空间，不引入屏幕空间模糊。PCSS 的遮挡搜索和过滤均限制在本级有效 UV 内，再变换到 atlas UV；逐样本平面修正使用限制后的实际偏移。采样保护不能仅依赖整张 atlas 的 Clamp。
- atlas 每相机只完整清一次，各级只设置自己的 viewport；不得在每级清整张共享深度。viewport tile 原点与纹理寻址采用相同数值坐标，局部投影 Y 仍由原有 `ShadowClipToUV` 处理；禁止再翻转 atlas 行。资源丢失/销毁后重建整张 atlas 并重绑；各相机使用自己的无 jitter CSM 计算。
- PCSS blocker search 和 PCF 的每个偏移采样必须使用接收平面在该 UV 上的比较深度，不能把中心深度用于整个宽核。blocker 深度平均前先移回中心平面参考；否则 TAA 会保留已经收敛的错误条纹。
- 接收面几何导数在背景返回、级联分支前统一计算，不能用 normal map 替代几何平面。投影到每个级联时按向量变换导数，统一 UV 原点及设备深度范围；不跨面拟合。世界法线偏移与 slope bias 使用几何导数重建、面向相机的接收面法线，不能沿 normal map 法线移动阴影查询。轮廓深度突变、退化或非有限几何导数关闭法线偏移，仅保留深度 bias；光空间投影退化时比较深度梯度回退为零。BRDF 光照继续使用贴图法线。
- 接收面运动向量无法表示投射到静止地面上的移动阴影；颜色变化检测只是近似。移动阴影残影与随机噪声收敛必须共同验收，不能保证普通 TAA 等同信号专用去噪器。

## 支持范围与验证

当前支持单相机输出的 Game/Scene、透视/正交、原生分辨率及局部视窗。相同输出在同一次 Render 中有多个相机时整组旁路；Depth/Nothing 叠加相机、XR、反射、Preview 旁路。多投影相机堆叠需要合成层面的运动/深度设计，不允许直接共享一份历史。

验证工具：`Tools/ValidateShaders.ps1`、`ValidateLightingShaderRetention.ps1`、`ValidateSceneShadows.ps1`、`ValidateColorOutput.ps1`；Unity MCP 执行 `Tools/UnityMcp/ValidateTemporalAA.cs.txt`（实际生产 Shader GPU 夹具）、`ValidateTemporalLifecycle.cs.txt`（真实 Unity 资源/矩阵生命周期）、`CaptureTemporalAA.cs.txt`（当前 Scene 同帧 HDR 输入输出与运动纹理）。结果输出到 git 忽略的 `OfflineBaseline/TemporalAA`。

当前实现优先建立正确的基线，新增持久 RT 逻辑存储约 40 字节/像素（1920×1080 约 79.1 MiB/启用相机，不含驱动开销）。移动端上线前须评估历史深度/计数打包、运动纹理格式与实际带宽，不把这版宣称为手机性能已达标。

尚未以移动真机 GPU 带宽/功耗、Player 构建、蒙皮动画、透明/粒子反应性和复杂移动阴影完成全面验收。透明渲染目前本身尚未接入管线；将来新增时必须补齐运动或反应性契约。
