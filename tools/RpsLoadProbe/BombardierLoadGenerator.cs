using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Shells out to <c>bombardier</c> on PATH for origin-saturation calibration.
/// Labeled <c>bombardier</c> in CSV — not used for publishable TWP÷peer ratios.
/// </summary>
internal static class BombardierLoadGenerator
{
    public const string GeneratorName = "bombardier";

    public static string? ResolveExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = Path.PathSeparator;
        var isWindows = OperatingSystem.IsWindows();
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, isWindows ? "bombardier.exe" : "bombardier");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static bool IsAvailable() => ResolveExecutable() != null;

    public static Task WarmupAsync(Uri target, int concurrency, TimeSpan duration, WorkloadOptions workload,
        CancellationToken cancellationToken) =>
        RunCoreAsync(target, concurrency, duration, collectLatency: false, workload, cancellationToken);

    public static Task<LoadResult> RunAsync(Uri target, int concurrency, TimeSpan duration,
        WorkloadOptions workload, CancellationToken cancellationToken = default) =>
        RunCoreAsync(target, concurrency, duration, collectLatency: true, workload, cancellationToken);

    private static async Task<LoadResult> RunCoreAsync(Uri target, int concurrency, TimeSpan duration,
        bool collectLatency, WorkloadOptions workload, CancellationToken cancellationToken)
    {
        var exe = ResolveExecutable()
                  ?? throw new InvalidOperationException(
                      "bombardier not found on PATH. Install from https://github.com/codesenberg/bombardier/releases");

        if (!workload.KeepAlive)
            throw new NotSupportedException("bombardier arm requires keep-alive (saturation control is tiny-GET KA).");
        if (!string.Equals(workload.Method, "GET", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("bombardier arm supports GET only.");
        if (workload.RequestBytes > 0 || workload.IsWebSocket || workload.IsEarlyResponse || workload.IsSlowConsumer)
            throw new NotSupportedException("bombardier arm supports tiny GET only.");

        var durationSec = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(concurrency.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add($"{durationSec}s");
        if (collectLatency)
            psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("j");
        psi.ArgumentList.Add("--http1");
        psi.ArgumentList.Add(target.AbsoluteUri);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Failed to start bombardier: {exe}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"bombardier exited {process.ExitCode}. stderr: {stderr}");
        }

        return ParseJsonResult(stdout, concurrency, duration.TotalSeconds);
    }

    internal static LoadResult ParseJsonResult(string json, int concurrency, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var result))
            throw new InvalidOperationException("bombardier JSON missing result object.");

        var ok = GetLong(result, "req2xx") + GetLong(result, "req1xx") + GetLong(result, "req3xx");
        var errors = GetLong(result, "req4xx") + GetLong(result, "req5xx") + GetLong(result, "others");
        var total = ok + errors;
        var rps = 0.0;
        if (result.TryGetProperty("rps", out var rpsObj) && rpsObj.TryGetProperty("mean", out var rpsMean))
            rps = rpsMean.GetDouble();

        var p50Ms = 0.0;
        var p99Ms = 0.0;
        var maxMs = 0.0;
        if (result.TryGetProperty("latency", out var latency))
        {
            if (latency.TryGetProperty("max", out var maxNs))
                maxMs = NsToMs(maxNs.GetDouble());
            if (latency.TryGetProperty("percentiles", out var pct))
            {
                p50Ms = NsToMs(GetPercentile(pct, "50"));
                p99Ms = NsToMs(GetPercentile(pct, "99"));
            }
        }

        var errorRate = total == 0 ? 100.0 : errors * 100.0 / total;
        return new LoadResult(
            Generator: GeneratorName,
            Concurrency: concurrency,
            DurationSeconds: durationSeconds,
            Ok: ok,
            Errors: errors,
            Rps: rps,
            ErrorRatePercent: errorRate,
            P50Ms: p50Ms,
            P99Ms: p99Ms,
            MaxMs: maxMs,
            NegotiatedVersionHint: "1.1");
    }

    private static double GetPercentile(JsonElement percentiles, string key)
    {
        if (percentiles.TryGetProperty(key, out var value))
            return value.GetDouble();
        return 0;
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.GetInt64() : 0;

    private static double NsToMs(double nanoseconds) => nanoseconds / 1_000_000.0;
}
