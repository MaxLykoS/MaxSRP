# Unity package 结构

MaxSRP 与 MaxTerrain 以两个独立的嵌入式 Unity package 保存在本仓库 `Packages/` 下。代码和运行所需资源归包所有，工程实例、演示场景、大型地形输入和本地生成数据归测试工程所有。

| 路径 | 内容 |
| --- | --- |
| `Packages/com.maxsrp.render-pipelines.maxsrp/` | 管线 Runtime、Editor、ShaderLibrary、Shaders、默认模块配置和运行时资源 |
| `Packages/com.maxsrp.terrain/` | 地形 Runtime、Editor、Shaders、原生 BC3 插件与第三方 HexTiling |
| `Packages/com.maxsrp.ddgi-baker/` | 现有独立 Editor 烘焙工具 |
| `Assets/Settings/MaxSRP/` | 本测试工程选择的 `Max Renderer Pipeline Asset.asset` |
| `Assets/Samples/MaxSRP/` | 管线演示材质和网格 |
| `Assets/Samples/MaxTerrain/` | 地形演示场景、材质、纹理与 Maladetas 输入资料 |
| `Assets/Editor/MaxSRP/` | Bistro 导入、场景搭建、专项验证与本机 Unity MCP 引导 |
| `Assets/Scenes/BistroTest/` | 工程编写的 Bistro 测试资源 |
| `Assets/ThirdParty/Bistro/` | 仅本地保留的 Bistro 第三方源数据；其 `.meta` 正常版本管理 |
| `Assets/StreamingAssets/` | 工程生成或发布的地形、GI 流式数据 |

## 包与程序集依赖

`com.maxsrp.terrain` 在 `package.json` 中依赖 `com.maxsrp.render-pipelines.maxsrp`，`MaxTerrain.asmdef` 显式引用 `MaxSRP.Runtime`。MaxSRP 不依赖 MaxTerrain；只安装管线包时不需要地形代码、地形 Shader 或地形插件。

- `MaxSRP.Runtime` 引用 Unity SRP Core，并定义公开、可扩展的 `MaxRenderBridge` 渲染桥契约。
- `MaxSRP.Editor` 引用 `MaxSRP.Runtime`，包含通用 Inspector、Shader GUI、太阳 GI 导入和构建引用检查。
- `MaxTerrain` 引用 `MaxSRP.Runtime`，实现并注册渲染参与者。
- `MaxTerrain.Editor` 引用地形及管线 Runtime，只在 Editor 编译。
- 工程中的 Editor 工具通过 Unity 预定义的 `Assembly-CSharp-Editor` 消费上述包。

渲染桥由管线在 GBuffer、MotionVectors、CSM 和场景线框阶段调用。地形遵守同一份 jitter、速度和共享 TAA 契约，见 [TemporalAA.md](TemporalAA.md)。包拆分不改变渲染顺序或引入额外历史。

## 在其他 Unity 工程中使用

支持 Unity 2022.3。把所需包目录复制到目标工程 `Packages/` 即可作为嵌入式包使用；也可通过 Package Manager 的 **Add package from disk** 选择包内 `package.json`。使用地形时需同时提供 MaxSRP 包，版本与地形清单声明一致。包依赖中的 Unity 模块和 SRP Core 由 Package Manager 解析。

本仓库的场景通过原有 GUID 引用移动后的脚本、Shader 和配置资产。其他工程可通过 **Create > MaxSRP** 创建自己的管线和配置资产，再在 Graphics / Quality 中指定管线。包内 `Default*.asset` 是默认模板；工程需要独立调参时复制到 `Assets/` 并赋给工程管线资产。

MaxTerrain 包包含地形算法和生产 Shader；演示场景及 Maladetas 数据在测试工程的 `Assets/Samples/MaxTerrain/`。复制该目录以及场景所需的 `Assets/StreamingAssets/` 数据后再运行演示。地形编辑工具的生成输出留在 `Assets/`，不会写入已安装的包目录。

## 维护与验证

移动已有 Unity 资产时始终一起移动 `.meta`，保留 GUID 和导入设置。新的包代码通过各自 `.asmdef` 编译；不要重新引入 `Assembly-CSharp` 类型查找或从 MaxSRP 反向引用 MaxTerrain。

仓库验证入口：

```powershell
python Tools/ValidatePackageStructure.py
& ./Tools/ValidateShaders.ps1
& ./Tools/ValidateLightingShaderRetention.ps1
```

Python 脚本检查包清单、程序集依赖方向和迁移后的资源 GUID 引用。Shader 脚本独立编译生产 HLSL 入口和变体，支持工程根路径下的 `Packages/...` include；保留引用脚本检查默认资源引用，并分别编译管线和地形的 Player / Editor 程序集、以及工程 Editor 工具（MCP 引导除外）。该编译使用安装的 Unity API 与两个 SRP Core 辅助声明，不替代 Unity 原生导入、Player 构建或 GPU 效果验证。

结构迁移后还需在 Unity 刷新编译，确认 Console 没有编译或 Shader 导入错误，并打开目标场景检查引用。原生 BC3 插件的重建入口为 `Tools/MaxTerrainBc3D3D11/Build.ps1`，产物写入地形包的 `Plugins/`。

## 2026-11-15 迁移验证

- 结构、GUID、默认配置引用、独立 Player / Editor API 编译及现有 HLSL 入口和变体检查通过。Unity 实际解析两个包为 `Embedded`，管线和工具分别加载为 `MaxSRP.Runtime`、`MaxSRP.Editor`，地形加载为 `MaxTerrain`。
- 两个地形场景的检查通过：缺失脚本和缺失资源引用均为 0，制作／发布对象隔离、摄像机、太阳及生产 Shader Pass 校验通过。
- 实际 Play 检查通过：制作场景有 Direct 绘制和共享 TAA 历史；发布场景两侧均有绘制，VT 页面池及固定覆盖就绪，实际生成 253 个 RVT 页面、上传 27 个 SVT 页面，反馈错误与页面输入校验失败均为 0。发布场景双相机共享输出继续按既有契约旁路 TAA。
- 本地结果位于 `OfflineBaseline/MaxTerrain/PackageMigration/`，包含 `inspection.json`、`authoring-smoke.json`、`published-smoke.json`、`vt-smoke.json`；验证后已退出 Play 并恢复制作场景，Console 错误为 0。
- 保留已有有效 GUID、场景、材质及二进制资源内容；原有 `MaxShadowCullingDepthReadback.shader.meta` 的 GUID 为无效的 33 位值，导入时修为 32 位有效 GUID。该 Shader 通过路径加载，没有序列化 GUID 引用需要迁移。

以上是结构迁移与运行冒烟验证，没有执行 Player 构建、移动真机或画质对比验收。
