# 本地烘焙数据与复现

Git 只保存代码、场景、材质源配置、烘焙设置、UV recipe 和生成方法。Lightmap、LightingData、天空可见度、太阳 GI 图集及流式帧全部在本地生成，不提交二进制，也不使用 Git LFS 保存这些产物。小体积的云贴图与来源说明保留在仓库。

## 目录

| 路径 | 内容 | 版本管理 |
| --- | --- | --- |
| `Assets/Scenes/BistroTest/Lighting/` | LightingSettings、UV 预算 recipe、灯位修正配置 | 提交 |
| `Assets/LocalGIBakes/Bistro/Lightmaps/` | Unity LightingData、颜色/方向贴图及 shadow mask | 整个目录本地保留 |
| `Assets/LocalGIBakes/Bistro/SkyVisibility/` | 天空可见度数组及生成的材质绑定 | 整个目录本地保留 |
| `Assets/LocalGIBakes/Bistro/SolarGI/` | 太阳 GI volume、共享距离/布局及驻留图集 | 整个目录本地保留 |
| `Assets/StreamingAssets/MaxSRP/SolarTOD/` | 流式 GI irradiance 帧；volume 保存相对目录 | 整个目录本地保留 |
| `Assets/ThirdParty/Bistro/LightmapUV/` | 离线 xatlas 缓存与 manifest | 只提交 `.meta`，缓存重新生成 |
| `Assets/ThirdParty/Bistro/NativeGI/` | 本地参考几何导出 | 只提交 `.meta` |
| `OfflineBaseline/` | 烘焙原始输出、报告、捕获、参考 PDF 与准备备份 | 整个目录本地保留 |

旧版 TOD、TODCompact 和当前 Uniform2mColorTOD 数据位于 `SolarGI/` 的各自子目录，方便对照；不自动删除旧数据。原生烘焙会短暂在场景旁写文件，完成回调将其移到 `Lightmaps/`；忽略规则同时覆盖场景旁的临时产物。通过 Unity AssetDatabase 移动已有资产及其 `.meta`，保留 GUID；不要单独复制或重建元数据。流式帧保留既有相对路径，避免改变运行时加载位置。

## 人工光与天空可见度

1. 按项目说明在 `Assets/ThirdParty/Bistro/` 放置 Bistro 源模型与纹理。源文件始终不提交，已有 `.meta` 应保留。
2. 打开 `BistroTest.unity`，按 [Lightmapping](Lightmapping.md) 捕获 before-UV 源数据，用 `Tools/Bistro/generate_lightmap_uv.py` 和相邻 requirements 生成并验证 xatlas UV2 缓存。保留 recipe 中的来源哈希与实例预算，不使用新随机 GUID 替换缓存元数据。
3. 执行 `MaxSRP/Bistro/Lightmap/1 Prepare Full Bistro`，再执行 `2 Preflight`。准备阶段不会启动烘焙；检查 `OfflineBaseline/BistroLightmap/status.json` 和实际导入 UV 的验证结果。
4. 明确启动 `3 Start Full Bake`。原生 Lightmap 完成、绑定与 UV overlap 检查通过后自动烘焙天空可见度；使用 Status 查看状态。不把旧产物或准备成功当作本次烘焙完成。
5. 查看 `Lightmaps/`、`SkyVisibility/` 和本次报告，保存场景。烘焙产物不进入 Git；场景中的本地资源引用在另一机器上需要重新烘焙并绑定。

## 太阳 TOD GI

1. 使用支持 DXR 的 GPU，以 `-force-d3d12` 启动 Unity，打开目标场景。
2. 执行 `MaxSRP/GI/DDGI TOD Baker`。按 [DDGI Baker 说明](../Packages/com.maxsrp.ddgi-baker/README.md) 选择 Scene Materials，设置网格、采样与时间键；完整场景可使用 compact 2 m preset。输出到新的 `OfflineBaseline/DDGIBaker/<run>/`，保留本次配置和报告。
3. 通过 `MaxSRP/Lighting/Import Compact Solar TOD` 导入 compact manifest；非 compact 数据使用 `Import Baked Solar TOD`。菜单默认保存到 `Assets/LocalGIBakes/Bistro/SolarGI/`，生成的 volume/图集必须保存在 `Assets/LocalGIBakes/` 内。
4. compact 导入同时在 StreamingAssets 写出 irradiance 帧。原有驻留 volume 可通过 `Assets/MaxSRP/Create Streamed Solar TOD Volume` 转换；保留原 volume 以便对照。
5. 将新 volume 和太阳绑定到场景 `MaxBakedSolarGI`，保存场景。使用 `-force-d3d11` 重新打开进行正常播放与对照，运行时不要求 DXR。新机器必须重新生成并绑定自己的 volume；仓库中的本地 GUID 本身不包含烘焙结果。

## 验证与提交

运行 `Tools/ValidateShaders.ps1` 检查 Shader 入口与 Lightmap 变体；在 Unity 刷新编译后检查 Console，并在目标场景验证运行时绑定、缺数据回退和效果。历史图像与数值记录见 [GIBakeValidation](GIBakeValidation.md)；历史临时夹具并不一定保留在当前仓库，不能据此宣称当前代码已重新验证。

提交前检查 `git diff --cached --numstat`，不应出现新增的烘焙二进制；`Assets/ThirdParty/Bistro/` 下暂存文件必须全部为 `.meta`。不要用强制添加绕过忽略规则。个人 `UserSettings/` 也不提交。

## 大体积参考资料

Far Cry 6 讲义只提交 [来源及 SHA256](References/FarCry6/source.json)。按其中 `sourceUrl` 下载到 `OfflineBaseline/References/FarCry6/SimulatingTropicalWeather_GDC2022.pdf`，核对 `sha256` 后阅读；PDF 不提交。云源图与运行时贴图的来源、许可证和生成方法分别见 [CloudLayer](CloudLayer.md) 与 [SixWayClouds](SixWayClouds.md)。
