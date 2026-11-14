# 渲染配置与 PC / Mobile 总控

当前生效的 `MaxRendererPipelineAsset` 是唯一档位入口。在 `Max Renderer Pipeline Asset.asset` 的 **Platform** 选择 PC 或 Mobile，七个模块同时读取对应参数和已有算法变体；各模块配置不再提供独立的档位开关。使用 Unity Quality 的管线覆盖时，以该 Quality 实际启用的管线资产为准，不按操作系统自动改档。

## 配置归属

默认资产位于 `Assets/MaxRendererPipeline/`，配置代码位于其 `Runtime/` 目录：

| 模块 | 默认配置资产 | 配置类型 | 内容 |
|---|---|---|---|
| 大气与天空 IBL | `DefaultAtmosphere.asset` | `MaxAtmosphereConfig` | 大气介质、地面反照率、太阳盘、AP、天空 IBL；大气 Shader、IBL / AP Compute 引用 |
| TAA | `DefaultTemporalAA.asset` | `MaxTemporalAAConfig` | 启停、历史权重、方差参数和 Temporal Shader |
| 云 | `DefaultCloud.asset` | `MaxCloudConfig` | `Layer` 平面云与 `SixWay` 六向云的参数、启停和资源 |
| 阴影 | `DefaultShadow.asset` | `MaxShadowConfig` | 级联距离 / 分配 / 混合、PCSS / PCF、偏移和蓝噪声 |
| Clustered Lighting | `DefaultClusteredLighting.asset` | `MaxClusteredLightingConfig` | 启停、tile / 深度层 / 每格灯容量和建表 Compute |
| AO | `DefaultAO.asset` | `MaxAOConfig` | PC XeGTAO、Mobile UE SSAO 的参数和 Shader 资源 |
| 后处理 | `DefaultPostProcess.asset` | `MaxPostProcessConfig` | 曝光、Bloom、调色、Film、输出及旧输出兼容设置 |

每个配置都可分别编辑 PC / Mobile 两份参数。TAA、云、阴影和 Clustered Lighting 目前两档共用各自的算法；本次将原管线参数和资源复制到新拆出的两档，没有借拆分调整画质预算。大气已有的相函数、反馈和数值预算差异，AO 的 GTAO / SSAO 差异，以及后处理已有的分支均保留。配置中出现两份参数，不代表新增了一套移动算法或完成了手机性能调优。

大气物理参数继续使用 km，天空 IBL 的参考高度继续使用世界米单位。共同 Shader / Compute 保留序列化资产引用；管线资产主要保存档位、七个配置引用和核心 Lighting Shader。

AO 配置也由管线资产引用，PC（GTAO）和 Mobile（SSAO）参数各有独立的 `Enabled`，默认均开启。场景中的 `MaxAOSettings` 控制所属场景的 AO 启停与 `AOOnly`，只有组件和当前档开关都开启才执行 AO；旧场景的隐藏配置引用用于兼容，不是新的配置入口。

## 编辑与生效

Inspector 修改配置后，会通知当前管线。切换总控档位，以及改变活动档的大气、TAA、云、阴影、Clustered 参数或构造资源，会在下一次 Render 边界重建相关管线状态，释放旧缓存并重新建立共享 TAA 历史，不在 Inspector 回调中途销毁正在使用的资源。

修改非当前档参数不改变当前构造状态，不清空共享 TAA。普通曝光、Bloom、调色与 Film 参数按后处理自身的更新规则生效，也不因此重建整条管线或清空 TAA；AO 生效参数通过已有 LightingHistoryKey 使受影响相机重新累积。更换被 Pass 构造函数固定持有的 Shader 等资源仍需重建。

运行时代码通过活动管线资产控制：

```csharp
var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
    as MaxSRP.MaxRendererPipelineAsset;
if (asset != null)
{
    asset.Platform = MaxSRP.MaxRenderPlatform.Mobile; // setter 会自动请求刷新

    // 直接改配置参数或引用后，显式通知；运行时不会触发 Editor OnValidate。
    asset.TemporalAAConfiguration.GetProfile(asset.Platform).HistoryWeight = 0.90f;
    asset.RefreshConfiguration();
}
```

`RefreshConfiguration()` 请求检查活动配置，在下一次 Render 生效；它不会立即开始渲染，也不会把参数自动写回磁盘。

## 验证边界

2026-11-14，本轮单独完成了以下验证：

- 30 项序列化迁移检查通过：新拆出的两档参数与原管线数据一致，Shader / Compute / 纹理引用保留，根资产不再保存内联模块参数。
- 七个配置及新建默认引用、16 个模块 Shader / Compute 引用检查通过；Player / Editor Unity API 编译通过，原工程刷新编译完成。
- BistroTOD 的 1920×1080 Game 相机完成 PC → Mobile → PC 切换。检查了七模块的活动 Profile、云 / 阴影 / 大气 Pass 的实际参数引用、AO 资源释放及有限的 GPU 可见度输出。SkyView / AP 深度随档切为 192×104 / 32 和 96×50 / 4，LUT 生成材质的移动变体随总控切换。
- 修改普通后处理参数和非当前档 TAA 参数后，TAA 对象及历史纹理保持；总控切档则重新建立 TAA 状态。临时参数均还原，最终保持 PC，场景和配置未因验证变脏，最终 Console 无 Error。

本地报告位于已忽略的 `OfflineBaseline/ConfigurationRefactor/`，测试代码仅在临时目录与内存中运行，没有新增测试场景。此轮为桌面 DX11 验证，未做手机真机或 Player 构建验收。各算法文档中的历史日期和 GPU 结果仍对应当时版本，不将历史报告当作本轮重跑的结果。
