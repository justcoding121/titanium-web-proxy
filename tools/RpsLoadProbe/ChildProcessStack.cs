using System.Diagnostics;
using System.Globalization;
using System.Text;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Spawns origin and proxy as separate OS processes so the load generator (parent)
/// does not steal CPU from the proxy under test.
/// </summary>
internal sealed class ChildProcessStack : IAsyncDisposable
{
    private readonly Process? originProcess;
    private readonly Process? proxyProcess;
    private readonly StreamReader originStdout;
    private readonly StreamReader? proxyStdout;

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
    public string? ControlPlaneUrl { get; }
    public string? ControlPlaneSecret { get; }
    public string? DashboardUrl { get; }
    public string? AuthorizationBearer { get; }
    public string? DiscoveryFilePath { get; }
    public int? OriginHttpPort { get; }

    /// <summary>
    ///     PID of the proxy child. Null for origin-direct arms. Use for <c>dotnet-dump</c> / <c>dotnet-trace</c>.
    /// </summary>
    public int? ProxyProcessId { get; }

    /// <summary>PID of the origin child. Ramp always process-splits; never null after <see cref="StartAsync"/>.</summary>
    public int? OriginProcessId { get; }

    /// <summary>True when origin and proxy share one OS process (leftover combined <c>--serve</c> only).</summary>
    public bool IsCombinedServe { get; }

    /// <summary>True when the arm hits the origin child with no proxy process.</summary>
    public bool IsOriginDirect => proxyProcess is null && originProcess is not null;

    private ChildProcessStack(Process? originProcess, StreamReader originStdout, Process? proxyProcess,
        StreamReader? proxyStdout, Uri targetUri, IReadOnlyList<Uri> targetUris, string? explicitProxyUrl,
        string? nginxVersion, Version requestHttpVersion, HttpVersionPolicy versionPolicy,
        string? loadGenerator = null, int? quicPort = null, int? originQuicPort = null,
        string? yarpVersion = null, string? controlPlaneUrl = null, string? controlPlaneSecret = null,
        string? dashboardUrl = null, string? authorizationBearer = null, string? discoveryFilePath = null,
        int? originHttpPort = null)
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
        ControlPlaneUrl = controlPlaneUrl;
        ControlPlaneSecret = controlPlaneSecret;
        DashboardUrl = dashboardUrl;
        AuthorizationBearer = authorizationBearer;
        DiscoveryFilePath = discoveryFilePath;
        OriginHttpPort = originHttpPort;
        ProxyProcessId = proxyProcess?.Id;
        IsCombinedServe = originProcess == null ||
                          (proxyProcess != null && ReferenceEquals(originProcess, proxyProcess));
        OriginProcessId = IsCombinedServe ? null : originProcess?.Id;
    }

    public static async Task<ChildProcessStack> StartAsync(ProbeMode mode, string? nginxPath,
        int? maxCachedConnections, CancellationToken cancellationToken, WorkloadOptions? workload = null,
        bool enableHttpInterception = false, bool mutateHttpInterception = false)
    {
        workload ??= WorkloadOptions.TinyGet;
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot locate current process path for child spawn.");
        var certDir = LoopbackCertificateAuthority.SeedDirectory();
        var childEnv = BuildChildEnv(workload, certDir, enableHttpInterception, mutateHttpInterception, mode);

        var origin = StartChild(exe, FormatOriginSpawnArgs(mode, workload), childEnv);
        Dictionary<string, string> originLines;
        try
        {
            originLines = await ReadUntilReadyAsync(origin, cancellationToken);
        }
        catch
        {
            TryKill(origin);
            throw;
        }

        if (mode == ProbeMode.OriginDirect)
        {
            var originHttp = Require(originLines, "origin_http");
            var originOnlyTarget = new Uri(originHttp);
            var originOnly = new ChildProcessStack(origin, origin.StandardOutput, proxyProcess: null,
                proxyStdout: null, originOnlyTarget, [originOnlyTarget], explicitProxyUrl: null,
                nginxVersion: null, System.Net.HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower,
                originQuicPort: TryParseInt(originLines, "origin_quic_port"));
            if (originOnly.OriginProcessId is null)
                throw new InvalidOperationException("Origin-direct requires an origin child.");
            return originOnly;
        }

        var originHttpPort = TryParseUrlPort(originLines, "origin_http");
        var originHttpsPort = TryParseUrlPort(originLines, "origin_https");
        var originQuicPort = TryParseInt(originLines, "origin_quic_port");
        var extraHttpsPorts = originLines
            .Where(kv => kv.Key.StartsWith("origin_https_extra", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new Uri(kv.Value).Port)
            .Where(p => p > 0)
            .ToList();

        var modeName = ServeProxyHost.ModeName(mode);
        var proxyArgs = new StringBuilder().Append(CultureInfo.InvariantCulture, $"--serve-proxy --mode {modeName}");
        if (originHttpPort is > 0)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --origin-http-port {originHttpPort}");
        if (originHttpsPort is > 0)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --origin-https-port {originHttpsPort}");
        if (originQuicPort is > 0)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --origin-quic-port {originQuicPort}");
        foreach (var extraPort in extraHttpsPorts)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --origin-https-extra-port {extraPort}");
        if (!string.IsNullOrWhiteSpace(nginxPath))
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --nginx-path \"{nginxPath}\"");
        if (maxCachedConnections is { } m)
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --max-cached-connections {m}");
        proxyArgs.Append(FormatOriginWorkloadArgs(workload));

        var proxy = StartChild(exe, proxyArgs.ToString(), childEnv);
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
        int? quicPort = TryParseInt(proxyLines, "quic_port");
        originQuicPort ??= TryParseInt(proxyLines, "origin_quic_port");

        var targets = new List<Uri> { new(target) };
        foreach (var kv in proxyLines)
        {
            if (kv.Key.StartsWith("target_for_client_extra", StringComparison.OrdinalIgnoreCase))
                targets.Add(new Uri(kv.Value));
        }

        var stack = new ChildProcessStack(origin, origin.StandardOutput, proxy, proxy.StandardOutput,
            new Uri(target), targets,
            string.IsNullOrWhiteSpace(explicitProxy) ? null : explicitProxy, nginxVersion,
            httpVersion, policy, loadGenerator, quicPort, originQuicPort, yarpVersion,
            controlPlaneUrl: TryGet(proxyLines, "control_plane_url"),
            controlPlaneSecret: TryGet(proxyLines, "control_plane_secret"),
            dashboardUrl: TryGet(proxyLines, "dashboard_url"),
            authorizationBearer: TryGet(proxyLines, "authorization_bearer"),
            discoveryFilePath: TryGet(proxyLines, "discovery_file"),
            originHttpPort: originHttpPort);
        if (stack.OriginProcessId is null)
            throw new InvalidOperationException("Ramp requires a split origin child; combined --serve is not used.");
        return stack;
    }

    private static string? TryGet(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string FormatOriginSpawnArgs(ProbeMode mode, WorkloadOptions workload)
    {
        var extra = FormatOriginWorkloadArgs(workload);
        return OriginRecipeFor(mode) switch
        {
            OriginRecipe.H2c => $"--serve-origin --h2c{extra}",
            OriginRecipe.Https => $"--serve-origin --https{extra}",
            OriginRecipe.HttpsOnly => $"--serve-origin --https --https-only{extra}",
            OriginRecipe.HttpsHttp1Only => $"--serve-origin --https --https-only --https-protocols http1{extra}",
            OriginRecipe.HttpsMulti => $"--serve-origin --https --https-only --extra-https-origins 15{extra}",
            OriginRecipe.Quic => $"--serve-origin --quic{extra}",
            _ => $"--serve-origin{extra}"
        };
    }

    private static OriginRecipe OriginRecipeFor(ProbeMode mode) => mode switch
    {
        ProbeMode.ReverseHttp2ToH2c or ProbeMode.ReverseH2cToH2c
            or ProbeMode.YarpReverseHttp2ToH2c or ProbeMode.YarpReverseH2cToH2c
            or ProbeMode.ReverseHttp3ToH2c or ProbeMode.YarpReverseHttp3ToH2c
            or ProbeMode.ReverseHttp1ToH2c or ProbeMode.YarpReverseHttp1ToH2c
            or ProbeMode.ReverseHttp1PlainToH2c or ProbeMode.YarpReverseHttp1PlainToH2c => OriginRecipe.H2c,
        ProbeMode.HttpsMitm or ProbeMode.ReverseHttp1Mitm
            or ProbeMode.ReverseHttp1ToHttps or ProbeMode.YarpReverseHttp1ToHttps
            or ProbeMode.YarpReverseHttp1TlsToHttps => OriginRecipe.Https,
        ProbeMode.MitmHttp2ToHttp1 or ProbeMode.MitmHttp3ToHttp1
            or ProbeMode.ReverseH2cToHttps or ProbeMode.YarpReverseH2cToHttps
            or ProbeMode.YarpReverseHttp2ToHttpsHttp1 or ProbeMode.YarpReverseHttp3ToHttpsHttp1 =>
            OriginRecipe.HttpsHttp1Only,
        ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi => OriginRecipe.HttpsMulti,
        ProbeMode.ReverseHttp2 or ProbeMode.ReverseH2c or ProbeMode.YarpReverseH2c
            or ProbeMode.ReverseHttp11ToHttp2 or ProbeMode.YarpReverseHttp11ToHttp2
            or ProbeMode.ReverseHttp1PlainToHttp2 or ProbeMode.YarpReverseHttp1PlainToHttp2
            or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
            or ProbeMode.YarpReverseHttp2ToHttps => OriginRecipe.HttpsOnly,
        ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
            or ProbeMode.ReverseHttp1PlainToHttp3 or ProbeMode.YarpReverseHttp1PlainToHttp3
            or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
            or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2cToH3
            or ProbeMode.YarpReverseHttp3ToHttp3 => OriginRecipe.Quic,
        _ => OriginRecipe.CleartextH1
    };

    private enum OriginRecipe
    {
        CleartextH1,
        H2c,
        Https,
        HttpsOnly,
        HttpsHttp1Only,
        HttpsMulti,
        Quic
    }

    private static Dictionary<string, string> BuildChildEnv(WorkloadOptions workload, string certDir,
        bool enableHttpInterception = false, bool mutateHttpInterception = false, ProbeMode? mode = null)
    {
        var env = new Dictionary<string, string>
        {
            [LoopbackCertificateAuthority.CertDirEnvironmentVariable] = certDir
        };
        if (enableHttpInterception || mutateHttpInterception
            || string.Equals(Environment.GetEnvironmentVariable("TWP_RPS_HTTP_INTERCEPTION"), "1",
                StringComparison.Ordinal))
            env["TWP_RPS_HTTP_INTERCEPTION"] = "1";
        if (mutateHttpInterception
            || string.Equals(Environment.GetEnvironmentVariable("TWP_RPS_HTTP_INTERCEPTION_MUTATE"), "1",
                StringComparison.Ordinal))
            env["TWP_RPS_HTTP_INTERCEPTION_MUTATE"] = "1";
        if (workload.CaptureTlsTiming)
            env["TWP_RPS_CAPTURE_TLS"] = "1";
        if (mode is ProbeMode.TwpCliPlusCacheHitHttp1)
            env["TWP_RPS_ORIGIN_CACHE_CONTROL"] = "public, max-age=60";
        // Forward Mac parity pool-pick digs into the proxy child (ProcessStartInfo env is a copy;
        // explicit forward avoids surprises when the parent only exported the vars briefly).
        foreach (var key in new[] { "TWP_DIAG_POOL_PICK", "TWP_DIAG_POOL_PICK_OUT", "TWP_RPS_STAGE_TIMING" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
                env[key] = value;
        }

        return env;
    }

    private static int? TryParseUrlPort(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;
    }

    private static int? TryParseInt(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
            return null;
        return value;
    }

    private static string FormatOriginWorkloadArgs(WorkloadOptions workload)
    {
        var sb = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $" --response-bytes {workload.ResponseBytes}");
        if (workload.EarlyResponseAfterBytes > 0)
            sb.Append(CultureInfo.InvariantCulture, $" --early-response-after {workload.EarlyResponseAfterBytes}");
        if (workload.IsWebSocket)
            sb.Append(" --websocket");
        // Lossy serve children need IsLossy so H2→H1 hosts can set MaxConcurrentStreams=8.
        if (workload.DelayMs > 0)
            sb.Append(CultureInfo.InvariantCulture, $" --delay-ms {workload.DelayMs}");
        if (workload.LossPercent > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $" --loss-percent {workload.LossPercent.ToString("0.##", CultureInfo.InvariantCulture)}");
        return sb.ToString();
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
        if (originProcess != null && !ReferenceEquals(originProcess, proxyProcess))
            TryKill(originProcess);
        await Task.CompletedTask;
        if (proxyStdout == null || !ReferenceEquals(originStdout, proxyStdout))
            originStdout.Dispose();
        proxyStdout?.Dispose();
        proxyProcess?.Dispose();
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

        // When the parent is launched as `dotnet RpsLoadProbe.dll`, ProcessPath is the host
        // (`dotnet`) and children need the same DLL path prepended or they fail with
        // "specified command or file was not found" (Linux Docker mem-profile path).
        if (NeedsDotnetEntryAssemblyPrefix(exe))
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location
                        ?? Path.Combine(AppContext.BaseDirectory, "RpsLoadProbe.dll");
            psi.ArgumentList.Add(entry);
        }

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

    private static bool NeedsDotnetEntryAssemblyPrefix(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
               || name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
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
                var leftover = string.Join('\n', map.Select(kv => $"{kv.Key}={kv.Value}"));
                throw new InvalidOperationException(
                    $"Child exited early (code {process.ExitCode}). stderr: {err} stdout-keys: {leftover}");
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
