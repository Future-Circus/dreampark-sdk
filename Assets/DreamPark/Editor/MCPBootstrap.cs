#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;

/// <summary>
/// DreamPark MCP Bootstrap — per-project port configuration.
///
/// Reads the .mcp-port file from the project root and sets MCPForUnity.HttpUrl
/// in EditorPrefs. This ensures each Unity instance shows the correct port in
/// the MCP for Unity window, even though EditorPrefs are machine-level.
///
/// The actual MCP server is started externally by run-pipeline.sh / new-park.sh
/// via: uvx mcp-for-unity --transport http --http-url http://127.0.0.1:{port}
///
/// Placed in DreamPark SDK — every cloned park inherits it automatically.
/// </summary>
[InitializeOnLoad]
public static class MCPBootstrap
{
    static MCPBootstrap()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string portFile = Path.Combine(projectRoot, ".mcp-port");

        if (!File.Exists(portFile))
            return;

        string portStr = File.ReadAllText(portFile).Trim();
        if (!int.TryParse(portStr, out int port) || port <= 0 || port >= 65536)
            return;

        string url = $"http://127.0.0.1:{port}";
        string currentUrl = EditorPrefs.GetString("MCPForUnity.HttpUrl", "");

        // Always set — another Unity instance may have overwritten it
        EditorPrefs.SetString("MCPForUnity.HttpUrl", url);
        EditorPrefs.SetBool("MCPForUnity.UseHttpTransport", true);
        EditorPrefs.SetBool("MCPForUnity.SetupCompleted", true);

        if (currentUrl != url)
        {
            Debug.Log($"[DreamPark] MCP port configured: {port} (from .mcp-port)");
        }
    }
}
#endif
