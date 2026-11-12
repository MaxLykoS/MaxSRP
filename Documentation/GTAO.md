# GTAO

通过场景中的 `MaxSRP/GTAO` 组件启用 XeGTAO，Compute 绑定到 `Shaders/Realtime/GTAO/MaxGTAO.compute`。Game 相机使用所属场景，Scene 视图使用活动场景；透视相机和支持所需 compute/RT 格式的平台才执行，否则释放资源并回退。

计算顺序为深度 mip 预处理、GTAO 求值、边缘感知空间去噪，再将可见度接入间接光。Strength 控制混合，Radius 单位为世界米，Quality 为 0–2。AOOnly 显示线性灰度可见度，最终输出旁路曝光、tone mapping 与 dithering。

GTAO 与烘焙天空可见度取较小值再按 Strength 混合，避免相同遮挡重复相乘；太阳 Probe GI 使用屏幕可见度调制，人工光 Lightmap 和实时直射沿用既有路径。不新增 AO 时间历史，结果随场景颜色进入共享 TAA；改变组件参数时使颜色历史失效。屏幕空间估计无法包含屏外遮挡。

Intel XeGTAO 的固定源码版本记录在 compute 文件中，MIT 许可证随 Shader 一起提交。`Tools/ValidateShaders.ps1` 包含三个生产 compute kernel 的编译检查；实际效果和设备性能仍需在 Unity 与目标硬件验证。
