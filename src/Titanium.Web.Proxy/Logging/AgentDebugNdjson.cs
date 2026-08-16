using System;
using System.IO;
using System.Text.Json;

namespace Titanium.Web.Proxy;

/// <summary>
/// Optional session debug NDJSON sink. Writes only when <c>TWP_RPS_DEBUG_LOG</c> is set to a file path.
/// Hot paths must stay silent in Release ramps — unconditional file I/O collapsed H2→H1 RPS.
/// </summary>
internal static class AgentDebugNdjson
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("TWP_RPS_DEBUG_LOG");
    private static readonly bool Enabled = !string.IsNullOrWhiteSpace(LogPath);

    public static void Write(string hypothesisId, string location, string message, object data)
    {
        if (!Enabled)
            return;

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
                File.AppendAllText(LogPath!, line + Environment.NewLine);
            }
        }
        catch
        {
            // never fail the proxy because of debug logging
        }
        // #endregion
    }
}
