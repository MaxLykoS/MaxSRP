# 单层主云

> 验证入口说明：下文 Validate/Capture 脚本及报告属于历史验证记录，临时夹具未保留在当前仓库。当前可运行 `Tools/ValidateShaders.ps1`，并在 Unity 检查 Console 与场景效果；不将历史记录视为本次重新验证。资源生成与 Setup 工具仍保留。

当前为移动端研究使用的 2.5D 天空云层，入口为 Inspector 的 **Cloud Layer Settings**。它已在默认管线资产中关闭，保留作对照；当前主云使用 Unity 官方六向演示资产，见 [SixWayClouds.md](SixWayClouds.md)。对照时关闭 Six Way Cloud Settings 后再启用本层。

## 表示与绘制

- `Res/MainCloudDensity.png` 是 2048² 的世界平面密度图。默认采用 Unity 官方 Cloud Layer 云图的积云通道，离线按 2 km 参考高度反投影到 16 km 正方形区域，并接合两对边缘，使风可以连续平移。原始资源、固定提交、SHA-256 和 Unity Companion License 说明位于 `Tools/CloudAssets/Unity`。`Tools/GenerateCloudDensity.py` 可重复生成资源；`--plane-size`、`--tile-size`、`--altitude` 控制参考投影。
- 图中不含太阳颜色或光照方向，也不是六向烘焙。R 通道保存归一化密度的平方根，运行时平方解码，使线性 R8 的精度更多分配给薄云边缘；导入必须关闭 sRGB。U/V 均使用 Repeat。默认含 mip 的密度图逻辑存储约 5.33 MiB。
- 用相机世界位置和观察射线求水平云面交点，在固定世界 XZ 上取样。因此相机平移、升降具有云层视差，风沿世界平面移动；不旋转天空方向或相机矩阵。它仍是二维薄层，不支持云内飞行；相机高于云面时向上的天空不绘制云。
- 高分辨率云图直接决定最终轮廓，并使用 mip 过滤；轮廓不经过 256×128 晴空 LUT。低仰角淡出控制远处重复与采样混叠。
- 独立 256² RGHalf 缓存保存太阳可见性和环境填充近似，默认逻辑存储 256 KiB。沿光方向在二维高度场中积分 16 步，近似云内自遮挡；可调整步数与分辨率。
- 太阳光使用现有物理太阳、云层高度处的大气透射及管线统一 HDR 光度单位。相函数与各向同性填充为艺术近似，不是完整体积多次散射。
- 现有天空全屏 Pass 直接合成：`云预乘辐亮度 + 云透射率 * (晴空 + 太阳盘)`，随后进入共享 TAA、最终曝光。没有新增屏幕分辨率云颜色 RT。

## 缓存与风

密度图与自遮挡纹理共用 `UV = 0.5 + 世界平面交点XZ / TileSize - 累计风位移 / TileSize`。Wind Direction 为世界 XZ 方位（0° 为 +X，90° 为 +Z），Wind Speed 单位为米/秒；双精度累计世界位移，改变风向/风速时不直接重置位置。只在二维周期纹理上寻址，默认 16 km 图案在 12 m/s 下约 22 分钟重复。没有球面旋转、双相位回摆或定时重置；每层只查询一份密度和光照缓存。

Altitude 为云面距海平面的高度，Tile Size 为世界重复区域大小。源图是参考视点的投影，改变观察位置会有真实平面透视和形变，不能提供完整体积视角。远处逐渐淡出，避免二维重复纹理压成地平线条带。当前没有云体生长、Flowmap 或第二层云。

太阳方向超过 Sun Update Angle（默认 0.5°）、云参数/资源变化、Shader 重新导入或 GPU RT 丢失时重算光照。缓存不保存太阳颜色；颜色和大气衰减在绘制时评估。旋转到新太阳角度会有量化更新，较小角度阈值会增加烘焙频率。

屏幕和 IBL 共用 `EvaluateMaxCloudLayer`，其输出为预乘辐亮度与透射率。IBL 不含解析太阳盘，云环境参考点随相机 XZ 以 128 m 网格更新，高度使用 Sky IBL 的 Reference Height。风导致的环境卷积默认最多每秒刷新一次；云内容、参考点和光照缓存变化立即刷新。原有大气自身的环境 revision 仍保留，因此连续太阳变化仍可能触发原有 IBL 更新，当前并未把整个 IBL 卷积改造成跨帧调度。

共享 TAA 对移动云覆盖区域限制历史权重，同时考虑风、相机 XZ 平移和高度变化导致的上一时刻覆盖；云启停/参数改变及相机穿越云面时拒绝天空历史。该策略优先减少拖影，动画云的抗锯齿收敛弱于静止天空。没有独立云时间解析。

## 验证与边界

`Tools/UnityMcp/ValidateCloudLayer.cs.txt` 使用真实 SRP 验证天空/IBL 变化、零覆盖、太阳变化、静止缓存、RT 丢失、风与共享 TAA、无太阳、关闭释放，并将图片和报告写到 `OfflineBaseline/CloudLayer`。

`Tools/UnityMcp/ValidateTemporalAA.cs.txt` 增加移动云、静止云、空云、参数变化与关闭的生产 Shader GPU 夹具。`Tools/ValidateShaders.ps1` 包含云光照缓存的 HLSL 编译。

`ValidateCloudTranslation.cs.txt` 用生产采样函数追踪世界平面上的同一物质点：3 个风向、0–900 秒平移、3 个相机位置/高度，共 45 组 GPU 对照。`CaptureCloudTranslation.cs.txt` 从实际天空 Shader 导出固定机位、12 m/s 横向风的定时帧；`OfflineBaseline/CloudLayer/Translation/world_translation_10x.gif` 为 10 倍速预览。

太阳自遮挡采用平面厚度场近似，没有保存完整三维密度或多视点形态。默认图由官方积云数据重投影得到，可按相同世界平面、周期边界和平方根密度编码约定替换；不同观察位置下仍可能暴露二维平面感。暂不包含地面云影、云与山体交错、云内飞行或月光。桌面 Editor 验证不代表手机 GPU 耗时、功耗或 Player 构建已经达标。

默认资源准备需要 NumPy、Pillow。`--procedural` 选项保留离线三维密度投影的研究版本，需要 SciPy；三维缓存和本机临时依赖位于 git 忽略的 `OfflineBaseline/CloudLayer`。仅最终单通道 PNG 和 `.meta` 进入 Assets，原始 RGBA 云图不被运行时引用。

参考：[Unity HDRP Cloud Layer](https://docs.unity.cn/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Override-Cloud-Layer.html)。这里采用独立轮廓与光照缓存的定制实现，不依赖 HDRP 运行时。
