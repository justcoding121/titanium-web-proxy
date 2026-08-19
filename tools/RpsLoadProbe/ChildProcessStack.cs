using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Spawns origin and proxy as separate OS processes so the load generator (parent)
/// does not steal CPU from the proxy under test.
/// </summary>
internal sealed class ChildProcessStack : IAsyncDisposable
{
    private readonly Process? originProcess;
    private readonly Process proxyProcess;
    private readonly StreamReader originStdout;
    private readonly StreamReader proxyStdout;

    public Uri TargetUri { get; }
    public string TargetUrl => TargetUri.ToString();
    public IReadOnlyList<Uri> TargetUris { get; }
    public string? ExplicitProxyUrl { get; }
    public string? NginxVersion { get; }
    public string? YarpVersion { get; }
    public Version RequestHttpVersion { get; }
    public HttpVersionPolicy VersionPolicy { get; }
    public string? LoadGenerator { get; }
    public int? QuicPort { get; }
    public int? OriginQuicPort { get; }

    /// <summary>
    ///     PID of the proxy (or combined --serve) child. Use for <c>dotnet-dump</c> / <c>dotnet-trace</c>.
    /// </summary>
    public int ProxyProcessId { get; }

    /// <summary>
    ///     PID of the origin child when process-split; null when origin+proxy share one combined --serve process.
    /// </summary>
    public int? OriginProcessId { get; }

    /// <summary>True when origin and proxy share one OS process (TLS/QUIC CA must be shared).</summary>
    public bool IsCombinedServe { get; }

    private ChildProcessStack(Process? originProcess, StreamReader originStdout, Process proxyProcess,
        StreamReader proxyStdout, Uri targetUri, IReadOnlyList<Uri> targetUris, string? explicitProxyUrl,
        string? nginxVersion, Version requestHttpVersion, HttpVersionPolicy versionPolicy,
        string? loadGenerator = null, int? quicPort = null, int? originQuicPort = null,
        string? yarpVersion = null)
    {
        this.originProcess = originProcess;
        this.originStdout = originStdout;
        this.proxyProcess = proxyProcess;
        this.proxyStdout = proxyStdout;
        TargetUri = targetUri;
        TargetUris = targetUris;
        ExplicitProxyUrl = explicitProxyUrl;
        NginxVersion = nginxVersion;
        YarpVersion = yarpVersion;
        RequestHttpVersion = requestHttpVersion;
        VersionPolicy = versionPolicy;
        LoadGenerator = loadGenerator;
        QuicPort = quicPort;
        OriginQuicPort = originQuicPort;
        ProxyProcessId = proxyProcess.Id;
        IsCombinedServe = originProcess == null || ReferenceEquals(originProcess, proxyProcess);
        OriginProcessId = IsCombinedServe ? null : originProcess!.Id;
    }

    public static async Task<ChildProcessStack> StartAsync(ProbeMode mode, string? nginxPath,
        int? maxCachedConnections, CancellationToken cancellationToken, WorkloadOptions? workload = null)
    {
        workload ??= WorkloadOptions.TinyGet;
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot locate current process path for child spawn.");

        // Combined --serve only when origin and proxy must share the in-process test CA
        // (HTTPS / QUIC origin). Cleartext-origin terminate arms run split so TWP is not
        // contending with the origin server for CPU/GC the way a separate native reverse peer process does not.
        if (RequiresCombinedServe(mode))
            return await StartCombinedServeAsync(exe, mode, nginxPath, maxCachedConnections, workload,
                cancellationToken);

        var originArgs = mode is ProbeMode.ReverseHttp2ToH2c or ProbeMode.ReverseH2cToH2c
            or ProbeMode.YarpReverseHttp2ToH2c or ProbeMode.YarpReverseH2cToH2c
            ? $"--serve-origin --h2c --response-bytes {workload.ResponseBytes}"
            : $"--serve-origin --response-bytes {workload.ResponseBytes}";
        var origin = StartChild(exe, originArgs);
        var originLines = await ReadUntilReadyAsync(origin, cancellationToken);
        var originHttp = Require(originLines, "origin_http");
        var originHttpPort = new Uri(originHttp).Port;

        var modeName = ServeProxyHost.ModeName(mode);
        var proxyArgs = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"--serve-proxy --mode {modeName} --origin-http-port {originHttpPort}");
        if (!string.IsNullOrWhiteSpace(nginxPath))
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --nginx-path \"{nginxPath}\"");
        if (maxCachedConnections is { } m)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --max-cached-connections {m}");

        var proxy = StartChild(exe, proxyArgs.ToString(),
            workload.CaptureTlsTiming ? new Dictionary<string, string> { ["TWP_RPS_CAPTURE_TLS"] = "1" } : null);
        Dictionary<string, string> proxyLines;
        try
        {
            proxyLines = await ReadUntilReadyAsync(proxy, cancellationToken);
        }
        catch
        {
            TryKill(proxy);
            TryKill(origin);
            throw;
        }

        var target = Require(proxyLines, "target_for_client");
        proxyLines.TryGetValue("explicit_proxy", out var explicitProxy);
        proxyLines.TryGetValue("nginx", out var nginxVersion);
        proxyLines.TryGetValue("yarp", out var yarpVersion);
        proxyLines.TryGetValue("http_version", out var httpVersionText);
        proxyLines.TryGetValue("load_generator", out var loadGenerator);
        var (httpVersion, policy) = ParseHttpVersion(httpVersionText);
        int? quicPort = null;
        if (proxyLines.TryGetValue("quic_port", out var quicPortText) &&
            int.TryParse(quicPortText, out var qp))
            quicPort = qp;

        return new ChildProcessStack(origin, origin.StandardOutput, proxy, proxy.StandardOutput,
            new Uri(target), [new Uri(target)],
            string.IsNullOrWhiteSpace(explicitProxy) ? null : explicitProxy, nginxVersion,
            httpVersion, policy, loadGenerator, quicPort, yarpVersion: yarpVersion);
    }

    /// <summary>
    /// True when the origin speaks TLS/QUIC and must share the probe's in-process test CA with the proxy.
    /// Cleartext-origin terminate arms (H1 TLS / H2→H1 / H2→h2c / native reverse H2 / H3→H1) stay process-split.
    /// </summary>
    private static bool RequiresCombinedServe(ProbeMode mode) => mode is
        ProbeMode.ReverseHttp2 or ProbeMode.ReverseHttp3
        or ProbeMode.ReverseHttp11ToHttp2 or ProbeMode.YarpReverseHttp11ToHttp2
        or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
        or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
        or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
        or ProbeMode.YarpReverseHttp3ToHttp3
        or ProbeMode.ReverseH2c or ProbeMode.YarpReverseH2c
        or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2cToH3
        or ProbeMode.MitmHttp2ToHttp1 or ProbeMode.MitmHttp3ToHttp1
        or ProbeMode.HttpsMitm or ProbeMode.ReverseHttp1Mitm
        or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi;

    private static async Task<ChildProcessStack> StartCombinedServeAsync(string exe, ProbeMode mode,
        string? nginxPath, int? maxCachedConnections, WorkloadOptions workload, CancellationToken cancellationToken)
    {
        var modeName = ServeProxyHost.ModeName(mode);
        var args = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"--serve --mode {modeName} --response-bytes {workload.ResponseBytes}");
        if (!string.IsNullOrWhiteSpace(nginxPath))
            args.Append(CultureInfo.InvariantCulture, $" --nginx-path \"{nginxPath}\"");
        if (maxCachedConnections is { } m)
            args.Append(CultureInfo.InvariantCulture, $" --max-cached-connections {m}");

        var serve = StartChild(exe, args.ToString(),
            workload.CaptureTlsTiming ? new Dictionary<string, string> { ["TWP_RPS_CAPTURE_TLS"] = "1" } : null);
        Dictionary<string, string> lines;
        try
        {
            lines = await ReadUntilReadyAsync(serve, cancellationToken);
        }
        catch
        {
            TryKill(serve);
            throw;
        }

        var target = Require(lines, "target_for_client");
        lines.TryGetValue("explicit_proxy", out var explicitProxy);
        lines.TryGetValue("nginx", out var nginxVersion);
        lines.TryGetValue("yarp", out var yarpVersion);
        lines.TryGetValue("http_version", out var httpVersionText);

        var targets = new List<Uri> { new(target) };
        foreach (var kv in lines)
        {
            if (kv.Key.StartsWith("target_for_client_extra", StringComparison.OrdinalIgnoreCase))
                targets.Add(new Uri(kv.Value));
        }

        var (httpVersion, policy) = ParseHttpVersion(httpVersionText);
        lines.TryGetValue("load_generator", out var loadGenerator);
        int? quicPort = null;
        if (lines.TryGetValue("quic_port", out var quicPortText) &&
            int.TryParse(quicPortText, out var qp))
            quicPort = qp;
        int? originQuicPort = null;
        if (lines.TryGetValue("origin_quic_port", out var originQuicText) &&
            int.TryParse(originQuicText, out var oqp))
            originQuicPort = oqp;

        return new ChildProcessStack(null, serve.StandardOutput, serve, serve.StandardOutput,
            new Uri(target), targets,
            string.IsNullOrWhiteSpace(explicitProxy) ? null : explicitProxy, nginxVersion,
            httpVersion, policy, loadGenerator, quicPort, originQuicPort, yarpVersion);
    }

    private static (Version Version, HttpVersionPolicy Policy) ParseHttpVersion(string? text) => text switch
    {
        "2.0" => (System.Net.HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact),
        "3.0" => (System.Net.HttpVersion.Version30, HttpVersionPolicy.RequestVersionExact),
        _ => (System.Net.HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower)
    };

    public async ValueTask DisposeAsync()
    {
        TryKill(proxyProcess);
        if (!ReferenceEquals(originProcess, proxyProcess))
            TryKill(originProcess);
        await Task.CompletedTask;
        if (!ReferenceEquals(originStdout, proxyStdout))
            originStdout.Dispose();
        proxyStdout.Dispose();
        proxyProcess.Dispose();
        if (originProcess != null && !ReferenceEquals(originProcess, proxyProcess))
            originProcess.Dispose();
    }

    private static Process StartChild(string exe, string args, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var part in SplitArgs(args))
            psi.ArgumentList.Add(part);
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
        if (extraEnv != null)
        {
            foreach (var kv in extraEnv)
                psi.Environment[kv.Key] = kv.Value;
        }

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start child: {exe} {args}");
        return process;
    }

    private static IEnumerable<string> SplitArgs(string args)
    {
        var list = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    list.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            list.Add(current.ToString());
        return list;
    }

    private static async Task<Dictionary<string, string>> ReadUntilReadyAsync(Process process,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var extraIndex = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Child exited early (code {process.ExitCode}). stderr: {err}");
            }

            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (key.Equals("target_for_client_extra", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("origin_https_extra", StringComparison.OrdinalIgnoreCase))
                {
                    map[$"{key}_{extraIndex++}"] = value;
                }
                else
                {
                    map[key] = value;
                }
            }

            if (line.Equals("READY", StringComparison.Ordinal))
                return map;
        }

        TryKill(process);
        ProbeLog.Error("Timed out waiting for child READY marker.");
        throw new TimeoutException("Timed out waiting for child READY marker.");
    }

    private static string Require(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Child did not report {key}=...");
        return value;
    }

    private static void TryKill(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best effort
        }
    }
}
