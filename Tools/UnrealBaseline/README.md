# MaxSRP → UE 5.3 移动端对照

目标工程：`D:\UnityProject\Unreal\我的项目\我的项目.uproject`。

关卡：`/Game/MaxSRP_Reference/Maps/MaxSRP_Mobile`。

该关卡来自当前 Unity **实际打开的** `SkyboxScene`，不是历史 Blender 场景。Unity 原场景、灯光、贴图导入设置和渲染公式未修改。

## 已迁移的数据

- 6 个网格对象：球、头盔、Cerberus 枪械、地面及两个方块；三角形数以 `import_report.json` 为准。网格通过 Unity Editor 读取实际导入数据，包含非 Read/Write 网格，不扫描 Library。
- 对象位置、法线、UV 和世界尺寸。旋转与缩放已烘焙到各网格，Actor 的位置保留原物体枢轴；Unity 米映射到 UE 厘米，轴为 `UE(X,Y,Z)=Unity(Z,X,Y)`。
- 3 个实际材质、10 张实际 GPU 导入纹理。Compute 逐 texel 解码 BC/sRGB；底色重新编码为 sRGB PNG，金属度/粗糙度保存线性 16-bit PNG，法线按生产 DXT5nm/BC5 约定解码，再由 UE normal texture 翻转绿色通道。UE 使用原生纹理导入、MIP 与压缩，不能宣称逐像素采样完全相同。
- 三个点光保留坎德拉强度和 Range，关闭阴影；太阳保留 120467 lux、方向、颜色与 2°角直径。
- 原相机 1920×1080、垂直 FOV 55.3°，UE 使用对应水平 FOV 85.9301453°。`MaxSRP Comparison Camera` 自动供 Player 0 使用，编辑器可 Pilot 此相机。

## 原生 UE 设置

使用 Mobile Forward、Mobile HDR、TAA、传统四级 CSM。UE 5.3 原生移动分配器将四级横排；实测每级预算 2048 时为 8192×2048 atlas。为遵守最大边不超过 4096 的限制，最终 UE 每级预算设为 1024（保护边由 UE 管理），实际 atlas 为 4096×1024；500 米距离、分布参数 2、10% 级联过渡和末端淡出。禁用 VSM、Lumen、烘焙光照、AO、bloom、motion blur，保留 UE 原生 PCF 与 bias。MaxSRP 近两级 PCSS 的接触硬化不会在 UE 常规移动 PCF 中自动出现。

UE SkyAtmosphere 使用同样的行星半径、60 km 大气厚度、Rayleigh/Mie 密度高度、散射/吸收系数和各向异性。移动端需要带 `Is Sky` 的 skydome，关卡已经创建相应材质及太阳盘节点。SkyLight 在 2 米参考高度捕获远处天空，避免把地面/模型反射当作 MaxSRP 已实现的 GI。UE 5.3 的移动端源码会禁用实时 SkyLight capture，因此此关卡显式关闭该选项；改变太阳后需要重新捕获 SkyLight，不假装具有 MaxSRP 同样的动态 IBL 更新。太阳盘节点在反射捕获时由 UE 自动排除，避免双重太阳能量。地表反照率为黑，保持当前大气地面反弹约定。

曝光为手动 EV100=15：ISO100、f/16、1/128秒，曝光补偿 0。**UE 的原生电影 tone mapper 与 MaxSRP 的 ACES 拟合不同；BRDF、天空 LUT、IBL 卷积、阴影 bias 和采样也由两边独立实现。** 本关卡用于比较原生渲染效果，不是让两边输出逐像素相等，也不能仅凭最终截图亮暗判断某一边缺能量。

## 生成和验证

1. Unity MCP 执行 `Tools/UnityMcp/ExportUnrealScene.cs.txt`，导出当前场景的 OBJ、GPU 线性纹理和 manifest。
2. Python 执行 `Tools/UnrealBaseline/prepare_inputs.py`。
3. UE `UnrealEditor-Cmd.exe <project> -run=pythonscript -script=<build_unreal.py> -nullrhi -unattended`，创建并保存专用资产与关卡。
4. 在 UE 移动预览模式打开关卡；`open_reference.py` 会定位对照相机并进入 Game View。

Unity 中间数据位于 `OfflineBaseline/UnrealReference`；UE 报告位于目标工程的 `Saved/MaxSRPReference`。六个物体的世界包围盒都与 Unity 一致，最大误差约 `0.000092 cm`。原工程配置备份位于 `Saved/MaxSRPReference/Backups`。专用内容全部放在 `Content/MaxSRP_Reference`，没有覆盖 StarterContent 关卡。

工具使用 UE 自带 Python 插件；`remote.py` 只发现名为“我的项目”的实例，通过 loopback/TTL0 连接，不连接其他工程。Windows 的 PC D3D Mobile 是移动路径预览，不是 Android/iOS 真机性能测试。


## 本次实际预览

已在 PCD3D ES3.1 的移动前向预览中打开并 Pilot 对照相机；实际读回 HDR=1、移动 TAA=2、CSM=4、每级预算 1024、VSM/Lumen/静态光照均关闭。灯光、曝光、贴图色彩空间和法线翻绿设置已读回核对。近裁剪面设置为 30 cm，对应 Unity 的 0.3 m。

`Saved/MaxSRPReference/UE_Mobile_Comparison.png` 与 `Unity_Comparison.png` 为同构图的 1920×1080 最终输出参考，不能替代统一显示曲线/采样条件的线性 HDR 能量测量。首次启动的编译缓存已经生成；启动脚本加了 `-NoRemoteShaderCompile`，避开当前 XGE 后端的慢启动。


阴影精度约束：UE 此配置每级有效尺寸低于 MaxSRP 的 2044²。这是原生 atlas 布局在 4096 边长上限下的资源分配差异；比较阴影时必须同时考虑该分辨率差异，不能把更粗的轮廓全部归因于 PCF。未修改 UE 引擎分配器，也未绕过 4096 限制。

资源池实际读回：最终活动 `MobileCSMAndSpotLightShadowmap` 为 **4096×1024，约 8 MiB**。初次 2048 探针留下的旧 atlas 已退役并清理其资源池缓存。
