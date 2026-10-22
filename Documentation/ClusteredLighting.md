# Clustered Deferred 点光

当前在既有 GBuffer Lighting Pass 中使用 per-camera 三维灯列表。太阳继续走独立方向光与 CSM 路径；点光的坎德拉、固定 HDR 缩放、距离衰减、BRDF 与曝光约定保持一致。暂不新增聚光灯、面光、局部光阴影或 GI。

## 数据与执行

`MaxLightConfigurator` 收集 Unity 当前相机 cullingResults 返回的全部点光，不再在第 32 盏截断。每灯两个 float4：世界位置／范围、线性颜色／物理强度，共 32 字节。原 32 灯 uniform 接口仅保留给旧离线诊断；正式渲染始终使用完整 StructuredBuffer，关闭 clustered 也遍历同一份完整数据。

每相机拥有独立的世界灯数据、视空间光球、格子计数和灯索引 buffer。命令缓冲记录数据上传、`PrepareLights` 与 `BuildClusters` dispatch，再在 Lighting 的 MaterialPropertyBlock 中绑定该相机资源。相机暂时缺席一次 Render 调用不释放资源；相机销毁或管线 Dispose 时释放，原生 buffer Release 后自动重建。

默认设置为 64×64 像素、32 层深度、每格存储 64 个灯索引。最后一个 XY tile 按实际视窗尺寸截断，透视深度采用对数分层，正交采用线性分层并支持负 near。参数在管线资产的 Clustered Lighting Settings 中调整；关闭 Enabled 可使用完整逐灯参考。

建表只使用视锥与灯光范围，不读取 GBuffer 深度。每线程处理一个格子，以四角射线与两端眼空间深度生成保守 AABB，按输入次序遍历光球并建立索引。一个格子会保守保留部分实际不影响该像素的灯；像素仍使用原有精确距离衰减。

## 投影、回退与生命周期契约

建表和查表统一使用 `CameraViewport.GetProjection(camera)` 的 Unity CPU 投影、无 jitter、clip Z 为 [-1,1]。每个片元从重建的世界位置重新投影到这份投影，取得 canonical UV 和眼空间深度；不使用输出目标上的 SV_POSITION，避免部分视窗偏移、GPU Y 翻转和 TAA jitter 混用。屏幕／深度范围外的点回退完整灯列表。GBuffer、运动、天空、HDR 以及共享 TAA 的既有约定不变，不新增历史。

计数保存真实相交灯数，存储容量只限制索引写入。超过每格容量时，该格像素回退完整灯列表，绝不以灯排序或截断损失能量。禁用、缺失／不支持 Compute、无效投影、缩放／剪切的自定义 view 矩阵或异常格子也使用完整遍历。启用时 Player build validator 要求序列化 Compute 引用，新建管线资产也包含默认引用。

单相机格子计数加索引限制为 32 MiB，超过预算使用完整遍历，避免超高分辨率造成失控分配。1080p 默认为 30×17×32=16320 格，计数与索引约 4.05 MiB／相机，另有每灯 32 字节和视空间范围 16 字节（按容量取整）。这不包含驱动开销，也不是手机性能承诺。每格超限或广范围灯密集重叠时，回退保证正确性，但不会保证性能。

## 验证入口

- `Tools/ValidateShaders.ps1`：两个生产 Compute kernel、Lighting 与既有深度／法线变体编译。
- `Tools/ValidateLightingShaderRetention.ps1`：Runtime Player／Editor 的 Unity API 编译及 Lighting 依赖。
- `Tools/ValidatePhysicalLighting.ps1`：物理强度、BRDF、衰减、曝光及超过 32 灯的收集／清空。
- Unity MCP 执行 `Tools/UnityMcp/ValidateClusteredLighting.cs.txt`：真实 GPU 建表，独立点／光球相交 oracle 检查无漏灯，包含非对称投影、正交负 near、非整 tile 尺寸、零灯、容量恰满及溢出。
- Unity MCP 执行 `Tools/UnityMcp/ValidateClusteredRendering.cs.txt`：隔离预览场景的生产 GBuffer／Lighting HDR 开关对照，96 盏局部光，透视／正交／非对称投影、Game TAA jitter、容量溢出／零灯、自定义 view 回退、相机隔离与 buffer 丢失恢复。测试恢复设置，不修改场景资产。

读回与报告存入忽略目录 `OfflineBaseline/ClusteredLighting`。当前验证设备为 Unity 2022.3 / D3D11；移动原生 Shader、手机 GPU 耗时／带宽／功耗和 Player 构建仍需另测。

2026-10-23 实测：8 组生产 Compute 测试、24523 个视锥内采样点、300835 组点／光球检查无漏灯，包含第 32 盏以后的索引和 129 灯溢出。生产渲染的 96 灯透视／正交／非对称投影、Game 共享 TAA 非零 jitter、容量 8 的溢出、零灯及缩放 view 回退共 7 组，clustered 与完整遍历的读回 HDR 在该夹具内逐像素一致；414 个溢出格子已实际走全灯回退。不同相机的灯 buffer 独立，活动相机暂时缺席不释放，原生计数 buffer Release 后自动恢复。该精度结论仅针对这些受控夹具。
