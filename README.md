# MaxSRP
Cook-Torrance BRDF + IBL

DXR Path Tracing

## 仓库结构

- `Packages/com.maxsrp.render-pipelines.maxsrp/`：独立 MaxSRP 渲染管线包，包含 Runtime、Editor、Shader、默认模块配置和运行时资源。
- `Packages/com.maxsrp.terrain/`：独立 MaxTerrain 地形包，依赖 MaxSRP 包，通过公开渲染桥提交地形绘制。
- `Packages/com.maxsrp.ddgi-baker/`：独立的 Editor 离线烘焙工具包。
- `Assets/Settings/MaxSRP/`：本工程使用的管线资产；`Assets/Samples/MaxSRP/`、`Assets/Samples/MaxTerrain/`：演示资源与场景。
- `Assets/Editor/MaxSRP/`：本工程的 Bistro 设置、专项验证与 Unity MCP 接入工具。

包的依赖方向、使用方式与迁移约定见 [Documentation/PackageStructure.md](Documentation/PackageStructure.md)。MaxSRP 可单独使用；安装 MaxTerrain 时需同时提供同版本依赖的 MaxSRP 包。

## 本地烘焙数据

仓库保留烘焙代码、场景和设置，不提交 Lightmap、天空可见度、太阳 GI 图集与流式帧。目录与复现步骤见 [Documentation/BakeData.md](Documentation/BakeData.md)。小体积云贴图随代码保留，参考 PDF 按来源记录下载到本地。
