using System.Globalization;
using System.Text.Json;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Optional NDJSON logger. Enable with env <c>TWP_RPS_DEBUG_LOG</c> set to a file path.
/// </summary>
internal static class DebugSessionLog
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("TWP_RPS_DEBUG_LOG");
    private static readonly object Gate = new();

    public static void Write(string hypothesisId, string location, string message, object data)
    {
        if (string.IsNullOrWhiteSpace(LogPath)) return;

        // #region agent log
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = "4b08c5",
                ["runId"] = "h2-h3-explicit",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload);
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // probe must not fail because of debug logging
        }
        // #endregion
    }

    public static void WriteResult(string hypothesisId, string arm, LoadResult result, bool meetsSlo,
        int? serverConnections = null, int? maxCachedConnections = null)
    {
        Write(hypothesisId, "RampOrchestrator", "step-result", new Dictionary<string, object?>
        {
            ["arm"] = arm,
            ["concurrency"] = result.Concurrency,
            ["rps"] = result.Rps,
            ["errPct"] = result.ErrorRatePercent,
            ["p50Ms"] = result.P50Ms,
            ["p99Ms"] = result.P99Ms,
            ["meetsSlo"] = meetsSlo,
            ["serverConnections"] = serverConnections,
            ["maxCachedConnections"] = maxCachedConnections,
            ["httpVersion"] = result.NegotiatedVersionHint
        });
    }

    public static string FormatInvariant(IFormattable value) =>
        value.ToString(null, CultureInfo.InvariantCulture);
}
