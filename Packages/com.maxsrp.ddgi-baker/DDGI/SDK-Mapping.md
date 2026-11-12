# Unity DDGI 与 RTXGI-DDGI SDK 对照

`DDGI` 固定在 RTXGI-DDGI v1.3.7 的静态、单体积、非 bindless 路径。上游源码是本地依赖，由安装脚本按提交和 SHA-256 取回。Baker 的太阳轨迹、场景追踪、迭代和导出策略在另一个程序集。 [v1.3.7 源码](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/tree/c233266e95931231961f35c842c78e7c4bdc50cf) · [逐文件清单](Shaders/SDK/Sources.json)

## 上游文件逐项核对

路径相对于本地 `Shaders/SDK/`，与上游仓库相对路径一致。19 项与 v1.3.7 的原始字节一致，`ProbeRelocationCS.hlsl` 与 NVIDIA 的后续修复提交一致。该修复将步长缩放应用到最远正面命中的方向向量。 [NVIDIA issue #78 修复](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/commit/a6c4d9917ac6223be9ee97f304b7d92c20dda970)

| 上游文件 | 用途 | 来源 |
| --- | --- | --- |
| `License.txt` | NVIDIA SDK 许可 | v1.3.7 |
| `rtxgi-sdk/include/rtxgi/Defines.h` | 枚举与预处理约定 | v1.3.7 |
| `rtxgi-sdk/include/rtxgi/ddgi/DDGIRootConstants.h` | SDK 根常量布局 | v1.3.7 |
| `rtxgi-sdk/include/rtxgi/ddgi/DDGIVolumeDescGPU.h` | GPU 体积描述符及打包、解包 | v1.3.7 |
| `rtxgi-sdk/shaders/Common.hlsl` | SDK 公共数学、采样函数 | v1.3.7 |
| `rtxgi-sdk/shaders/Platform.hlsl` | 平台、着色器编译约定 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/Irradiance.hlsl` | 八邻点可见性与线性辐照度查询 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/ProbeBlendingCS.hlsl` | 辐照度、距离矩与八面体边界更新 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/ProbeClassificationCS.hlsl` | Probe 分类 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/ProbeRelocationCS.hlsl` | Probe 重定位 | `a6c4d99` |
| `rtxgi-sdk/shaders/ddgi/include/Common.hlsl` | DDGI 公共辅助函数 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/DDGIRootConstants.hlsl` | Shader 根常量绑定 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/ProbeCommon.hlsl` | Probe 世界坐标等公共函数 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/ProbeDataCommon.hlsl` | Probe 偏移、状态读写 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/ProbeIndexing.hlsl` | 线性索引、纹理与数组层坐标 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/ProbeOctahedral.hlsl` | 八面体方向编码和解码 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/ProbeRayCommon.hlsl` | 固定射线、旋转射线、RayData 编码 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/validation/ProbeBlendingDefines.hlsl` | Blend 宏校验 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/validation/ProbeClassificationDefines.hlsl` | 分类宏校验 | v1.3.7 |
| `rtxgi-sdk/shaders/ddgi/include/validation/ProbeRelocationDefines.hlsl` | 重定位宏校验 | v1.3.7 |

## Unity 实现对应关系

| Unity 实现 | SDK 对应位置 | 适配差异 |
| --- | --- | --- |
| `ProbeVolume.Description`、`PackedVolume`、`UploadConstants` | `DDGIVolumeDescGPU.h` 的 `DDGIVolumeDescGPUPacked` / `PackDDGIVolumeDescGPU`，`DDGIVolume.cpp` 的 `GetDescGPU`，D3D12 的 `UploadDDGIVolumeConstants` | 128 字节、相同位段与 UNORM16 阈值编码。Unity 通过 `SetBufferData` 上传，描述符由 Baker 校验范围。 |
| `RootConstants`、`BindUpdateResources` | `DDGIRootConstants.h`、`DDGIRootConstants.hlsl`、D3D12 的根签名和描述符绑定 | 单体积使用全零的 32 字节常量缓冲，选择索引 0。Unity 按资源名反射绑定。 |
| `ProbeVolume` 构造、`CreateAtlas`、`Dispose` | `DDGIVolume.cpp` 的 `GetDDGIVolumeTextureDimensions`，D3D12 的 `CreateManagedResources` / `Destroy` | XZ 平面打包在同一数组层，Y 为层索引。Unity 负责资源和状态转换，数组通过 `AllDepthSlices` 绑定。 |
| `ClearLighting`、`ClearProbeData` | D3D12 的 `ClearProbes` 与重定位、分类 reset 入口 | Unity 显式清零照度、距离和 ProbeData。照度 alpha 清为 0，原版 `ClearProbes` 为 1，辐照度使用 RGB。 |
| `Blend` / `DispatchBlend` | D3D12 的 `UpdateDDGIVolumeProbes`，`DDGIProbeBlendingCS` | 同一上游 kernel 按宏编译成 irradiance 和 distance 两个入口，分别使用含边界的 8×8 和 16×16 tile。分组为 `(nx, nz, ny)`。 |
| `Relocate`、`Classify` / `DispatchLayout` | D3D12 的 `RelocateDDGIVolumeProbes` / `ClassifyDDGIVolumeProbes`，对应同名 HLSL kernel | 每组 32 点。Baker 明确控制几何轮数以及最后一次偏移后的分类。 |
| `UnitySdk.hlsl`、`ProbeUpdate.compute.txt` | SDK shader 编译配置和入口选择 | 左手 Y-up、非 bindless、反射绑定。编译入口别名和宏在 Unity 包装文件中，上游 kernel 函数体保持原样。 |
| `BindRayTracingResources` | SDK 集成指南中应用绑定 RayData 及 Volume 资源的步骤 | Unity 负责绑定；Baker 的 raygen 调用 SDK 方向、位置和 irradiance 查询，场景相交及着色由 Baker 执行。 |
| `DDGI.asmdef`、`Install.ps1`、`Sources.json`、`UPSTREAM.md`、`.gitignore` 和 Unity `.meta` | 工程侧程序集、依赖和资产元数据 | 用于冻结来源和稳定导入。 |

[Unity 宿主](ProbeVolume.cs) · [Unity kernel 配置](Shaders/ProbeUpdate.compute.txt) · [SDK 体积与纹理布局](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/c233266e95931231961f35c842c78e7c4bdc50cf/rtxgi-sdk/src/ddgi/DDGIVolume.cpp) · [SDK D3D12 宿主](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/c233266e95931231961f35c842c78e7c4bdc50cf/rtxgi-sdk/src/ddgi/gfx/DDGIVolume_D3D12.cpp) · [SDK 集成指南](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/c233266e95931231961f35c842c78e7c4bdc50cf/docs/Integration.md)

距离 Atlas 用 RGBA32F UAV，距离矩仍取 RG，保持 SDK 的二分之一存储约定。Variability 纹理用完整 irradiance tile 大小，为上游 inactive 分支的写入留空间。当前范围是静态体积、完整 F32 RayData/irradiance/ProbeData，滚动体积、bindless、压缩格式和 variability 归约均在该范围之外。

NativeGI 用 `RTXGI_DDGI_RESOURCE_MANAGEMENT=1` 和 `RTXGI_DDGI_SHADER_REFLECTION=0` 走原版 D3D12 宿主。Unity 将两者分别设为 0 和 1。两个后端均使用 `RTXGI_COORDINATE_SYSTEM=0`，关闭 bindless、blend shared memory 和 scrolling shared memory。 [Unity 宏配置](Shaders/SDK/UnitySdk.hlsl) · [Unity blend 宏配置](Shaders/ProbeUpdate.compute.txt)

Baker 使用前 32 条固定射线做布局，在最终偏移位置重新分类，随后冻结 ProbeData。每轮以实际 hysteresis 更新，再以 1 重跑 blend 填稳边界。太阳光、Lambert 反射率、旋转序列以及 `TraceRay` 的范围属于烘焙策略。SDK 描述符的 `MaxRayDistance` 字段与 Baker 当前的 DXR `TMax` 独立。 [Baker 调度](../Baker/DdgiGpuBake.cs) · [Baker raygen](../Baker/Shaders/ProbeTrace.raytrace.txt)

## 复核

1. 在工程根目录运行下方命令安装依赖。随后用 `-VerifyOnly` 核对本地每个文件的字节数和 SHA-256。
2. 用 DX12 按 [NativeGI 对照说明](../README.md#reproduce-a-nativegi-comparison) 烘焙正午对照。比较 encoded irradiance、距离矩、偏移、分类和解码后的线性辐照度。
3. 固定几何、采样和设备时，可对拆分前后的 `official_irradiance_0.ngatlas`、`shared_distance.ngatlas`、`shared_probe_data.ngatlas` 直接比较 SHA-256。当前基线在 RTX 3080 Ti 上三份数据均与拆分前逐字节一致，对 NativeGI 的 2,048 个线性辐照度查询相对 RMSE 为 0.0000708%，Probe 分类差异为 0。

```powershell
& .\Packages\com.maxsrp.ddgi-baker\DDGI\Shaders\SDK\Install.ps1
& .\Packages\com.maxsrp.ddgi-baker\DDGI\Shaders\SDK\Install.ps1 -VerifyOnly
```
