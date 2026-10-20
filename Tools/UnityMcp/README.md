# Unity MCP

项目使用 [MCP for Unity](https://github.com/CoplayDev/unity-mcp) 10.3.0，仓库中的 Unity 包引用固定为 `https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#aa5fc638d623f56178d50329d9ad8c541a57fe66`，锁文件也固定到相同提交。

当前机器的工作副本保留 `file:C:/Users/pc/.codex/unity-mcp/MCPForUnity`，其 checkout 为同一提交，避免已打开的 Unity 再次下载整个上游仓库。这个本机覆盖没有提交：`git status` 中 `Packages/manifest.json` 和 `Packages/packages-lock.json` 的差异是预期的本机包路径。换机器使用仓库的 Git 引用即可，但 Codex 服务端仍需单独安装和配置。

- Unity 包：`Packages/manifest.json`。
- 自动连接：`Assets/MaxRendererPipeline/Editor/MaxUnityMcpBootstrap.cs`，仅 Editor 生效，每次程序集重载后恢复本机 stdio 桥接。
- Codex 配置：`C:/Users/pc/.codex/config.toml` 中的 `mcp_servers.unity`。默认实例固定为 `MaxSRP@7d90526a`，避免命中其他已打开工程。
- 本机服务端：`C:/Users/pc/.codex/unity-mcp/Server/.venv/Scripts/mcp-for-unity.exe`，由客户端按需启动和结束。
- 服务端和 Unity 包均关闭遥测；禁用服务端自动抢焦点。传输使用本机桥接，不启动常驻 HTTP 服务。

优先使用 Codex 提供的 Unity MCP 工具。如果当前会话未热加载新增工具，可直接通过同一个 MCP 的 Python SDK 调用，无须重启 Unity：

```powershell
$unityMcpPython = 'C:/Users/pc/.codex/unity-mcp/Server/.venv/Scripts/python.exe'
& $unityMcpPython Tools/UnityMcp/client.py read 'mcpforunity://project/info'
& $unityMcpPython Tools/UnityMcp/client.py read 'mcpforunity://editor/state'
& $unityMcpPython Tools/UnityMcp/client.py call refresh_unity '{"mode":"force","scope":"all","compile":"request","wait_for_ready":true}'
& $unityMcpPython Tools/UnityMcp/client.py call read_console '{"action":"get","types":["error","warning"],"count":"20","format":"detailed"}'
& $unityMcpPython Tools/UnityMcp/client.py call execute_menu_item '{"menu_path":"MaxSRP/Diagnostics/Capture Final Output And Editor"}'
```

执行刷新后检查 `editor/state` 与控制台，不能仅凭刷新请求成功断言编译通过。首次安装需要 Unity 解析包并完成编译；之后的刷新和诊断菜单通过 MCP 触发。屏幕截图诊断仍要求被测视图可见、无遮挡，这与后台脚本编译是两个条件。

沿用项目 `AGENTS.md`：不通过 MCP 文件工具访问、搜索或修改 `/Library/`。状态和刷新工具的服务端外部变更检测范围为 `Assets`、`Packages`、`ProjectSettings`。

本次工具接入不代表天空 banding 已解决；后续继续以实际视图和 GPU 分段读回验证。

## 2026-10-19 接通验证

- MCP 实际返回工程根路径 `D:/UnityProject/MaxSRP`、Unity `2022.3.62f2c1`，stdio 桥接监听本机端口 6400。
- `refresh_unity` 强制请求脚本编译，程序集重载断开后自动重连成功。
- 重载后 `read_console` 返回 0 条错误或警告；`execute_code` 直接检查 `isCompiling=false`、`isUpdating=false`，当前管线为 `MaxSRP.MaxRenderPipeline`。同次调用确认 `InternalEditorUtility.isApplicationActive=false`，无需让 Unity 保持前台。
- `execute_menu_item` 执行 `MaxSRP/Diagnostics/Compare Sky Halo Sampling` 后，四组 GPU 导出实际完成，控制台返回完成日志，`OfflineBaseline/SkyHalo/capture.json` 的 UTC 时间为 `2026-10-19T15:24:43.9482354Z`。此前数据保存在其 `BeforeMcp_20261019_2318` 子目录。
- 当前 stdio 下 `editor/state` 的部分状态字段可能为 null，而 `ready_for_tools` 仍为 true。编译验证应结合控制台和 `execute_code` 直接检查 Editor 状态，不能只看 ready 标志。

当前聊天的原生工具列表尚未加载新增 Unity 工具，已通过上面的 SDK 入口实测同一 MCP。无需为此重启 Unity。
