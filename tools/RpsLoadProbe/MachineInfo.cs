using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal static class MachineInfo
{
    public static string FormatReport(string? nginxVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Machine info ===");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Processors (logical): {Environment.ProcessorCount}");
        sb.AppendLine($"CPU: {TryGetCpuName()}");
        sb.AppendLine($"RAM: {TryGetRam()}");
        sb.AppendLine($".NET: {Environment.Version}");
        if (!string.IsNullOrWhiteSpace(nginxVersion))
            sb.AppendLine($"nginx: {nginxVersion}");
        else
            sb.AppendLine("nginx: (not detected)");
        return sb.ToString();
    }

    private static string TryGetCpuName()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unknown";
            }

            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = line.IndexOf(':');
                        if (idx >= 0)
                            return line[(idx + 1)..].Trim();
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return "unknown";
    }

    private static string TryGetRam()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var gcInfo = GC.GetGCMemoryInfo();
                if (gcInfo.TotalAvailableMemoryBytes > 0)
                    return FormatBytes(gcInfo.TotalAvailableMemoryBytes);
            }

            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                            return FormatBytes(kb * 1024);
                    }
                }
            }

            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
                return FormatBytes(info.TotalAvailableMemoryBytes);
        }
        catch
        {
            // ignore
        }

        return "unknown";
    }

    private static string FormatBytes(long bytes)
    {
        var gib = bytes / (1024.0 * 1024.0 * 1024.0);
        return string.Create(CultureInfo.InvariantCulture, $"{gib:F1} GiB");
    }
}

internal static class CsvWriter
{
    public static Task WriteHeaderAsync(StreamWriter writer) =>
        writer.WriteLineAsync(
            "timestamp_utc,arm,generator,concurrency,duration_s,ok,errors,rps,error_rate_pct,p50_ms,p99_ms,max_ms,meets_slo,nginx_version,yarp_version,http_versions,max_cached_connections,method,response_bytes,request_bytes,delay_ms,loss_percent,keepalive");

    public static Task WriteRowAsync(StreamWriter writer, string arm, LoadResult result, bool meetsSlo,
        string? nginxVersion, int? maxCachedConnections = null, WorkloadOptions? workload = null,
        string? yarpVersion = null)
    {
        workload ??= WorkloadOptions.TinyGet;
        return writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{arm},{result.Generator},{result.Concurrency},{result.DurationSeconds:F3},{result.Ok},{result.Errors},{result.Rps:F1},{result.ErrorRatePercent:F4},{result.P50Ms:F2},{result.P99Ms:F2},{result.MaxMs:F2},{(meetsSlo ? 1 : 0)},{Escape(nginxVersion)},{Escape(yarpVersion)},{Escape(result.NegotiatedVersionHint)},{(maxCachedConnections?.ToString(CultureInfo.InvariantCulture) ?? "")},{workload.Method},{workload.ResponseBytes},{workload.RequestBytes},{workload.DelayMs},{workload.LossPercent:F2},{(workload.KeepAlive ? 1 : 0)}"));
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
