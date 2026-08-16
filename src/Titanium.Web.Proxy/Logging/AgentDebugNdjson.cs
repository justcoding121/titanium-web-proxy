using System;
using System.IO;
using System.Text.Json;

namespace Titanium.Web.Proxy;

/// <summary>
/// Session debug NDJSON sink (debug mode). Writes to workspace <c>debug-4b08c5.log</c>.
/// </summary>
internal static class AgentDebugNdjson
{
    private static readonly object Gate = new();
    private static readonly string LogPath = ResolveLogPath();

    private static string ResolveLogPath()
    {
        var env = Environment.GetEnvironmentVariable("TWP_RPS_DEBUG_LOG");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        // Prefer repo-root when running from tools/RpsLoadProbe bin/
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "debug-4b08c5.log");
            if (File.Exists(Path.Combine(dir, "Titanium.Web.Proxy.sln")) ||
                File.Exists(Path.Combine(dir, "src", "Titanium.Web.Proxy", "Titanium.Web.Proxy.csproj")))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "debug-4b08c5.log"));
    }

    public static void Write(string hypothesisId, string location, string message, object data)
    {
        // #region agent log
        try
        {
            var payload = new
            {
                sessionId = "4b08c5",
                runId = "h2h1-bridge",
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload);
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // never fail the proxy because of debug logging
        }
        // #endregion
    }
}
