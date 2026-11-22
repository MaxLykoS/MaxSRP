# MaxSRP Render Pipeline

Unity 2022.3 的独立延迟渲染管线包，包名为 `com.maxsrp.render-pipelines.maxsrp`，版本为 `0.1.0`。

## 内容

- `Runtime/`：管线、配置类型、渲染 Pass、光照与共享时间渲染基础设施，编译为 `MaxSRP.Runtime`。
- `ShaderLibrary/`、`Shaders/`：可供项目和扩展包引用的 Shader 与 HLSL。
- `Art/`、`ThirdParty/`：管线默认资源及其来源、许可说明。
- `Editor/`：光源 Inspector、离线材质 Inspector、烘焙数据导入和构建引用检查，编译为仅 Editor 使用的 `MaxSRP.Editor`。
- `DefaultConfigs/`：七个模块的默认配置及显式 Shader 引用。

## 使用

安装本包及 `com.unity.render-pipelines.core` 14.0.12。创建 `MaxSRP/MaxRendererPipelineAsset` 资源，将七个配置资源和 `MaxSRP/PBRLitPass` Shader 赋给管线资源，再在 Graphics / Quality 设置中选择该管线。需要自定义参数时，将默认配置复制到项目的 `Assets/` 下并使用这些副本。

此仓库直接以 embedded package 使用本包。项目管线资源位于 `Assets/Settings/MaxSRP/`，演示材料和模型位于 `Assets/Samples/MaxSRP/`，Bistro 及验证工具位于 `Assets/Editor/MaxSRP/`。这些目录不是本包运行所需的依赖。

其他 Shader 应使用包路径，例如：

```hlsl
#include "Packages/com.maxsrp.render-pipelines.maxsrp/ShaderLibrary/Realtime/Core/CommonInput.hlsl"
```

## 渲染扩展

本包不依赖 MaxTerrain。扩展包引用 `MaxSRP.Runtime`，实现 `MaxSRP.IMaxRenderParticipant`，并通过 `MaxRenderBridge.Register` / `Unregister` 注册。可选的 `IMaxRenderDiagnosticParticipant` 选择线性材质诊断输出。

管线拥有相机矩阵、渲染目标、剔除和 Pass 顺序。扩展在管线指定的 GBuffer、MotionVectors 和 CSM 级联中提交几何体；不得自行增加第二份 TAA 或颜色历史。运动数据必须使用相同 jitter 栅格、当前和上一帧无 jitter 变换，以及有效的上一帧形变；没有有效上一状态时拒绝历史。相机切换或内容突变通过 `MaxRenderBridge.InvalidateTemporalHistory` 通知宿主，几何或输入变化通过 `InvalidateShadows` 使缓存阴影失效。

完整渲染契约与验证说明保存在仓库 `Documentation/TemporalAA.md` 中；修改时间渲染相关代码前必须阅读并保持其约定。
