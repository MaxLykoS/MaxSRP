using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using UnityEditor;
using UnityEngine;

namespace MaxSRP.EditorDiagnostics
{
    /// <summary>Reconnect this project's local MCP bridge after editor/domain reloads.</summary>
    [InitializeOnLoad]
    internal static class MaxUnityMcpBootstrap
    {
        static MaxUnityMcpBootstrap()
        {
            if (Application.isBatchMode)
                return;

            // Scope transport selection to this Editor session, preserving other projects' prefs.
            EditorConfigurationCache.Instance.PinStdioForSession();
            EditorPrefs.SetBool("MCPForUnity.TelemetryDisabled", true);
            SessionState.SetBool("MCPForUnity.SetupWindowCheckedThisEditorSession", true);
            EditorApplication.delayCall += Connect;
        }

        private static async void Connect()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Connect;
                return;
            }

            if (!StdioBridgeHost.IsRunning)
            {
                bool connected = await MCPServiceLocator.TransportManager.StartAsync(TransportMode.Stdio);
                if (!connected)
                    Debug.LogWarning("MaxSRP: local Unity MCP bridge is not ready; use Window/MCP for Unity to inspect it.");
            }
        }
    }
}
