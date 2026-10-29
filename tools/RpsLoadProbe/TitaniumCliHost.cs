using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Spawns the shipped <c>titanium run -c twp.yaml</c> daemon as an external process
/// (same shape as <see cref="NginxHost"/>) for product-defaults edition arms.
/// </summary>
internal sealed class TitaniumCliHost : IDisposable
{
    public const string ControlPlaneSharedSecret = "changeme";
    public const string JwtAuthorityHost = "http://127.0.0.1";

    private readonly Process process;
    private readonly string workDir;
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly object gate = new();
    private readonly HttpListener? jwksListener;
    private readonly CancellationTokenSource? jwksCts;
    private readonly IAsyncDisposable? secondOrigin;
    private readonly RSA? jwtRsa;

    public int Port { get; }
    public string ListenUrl { get; }
    public int? ProcessId => process.HasExited ? null : process.Id;
    public string? ControlPlaneUrl { get; }
    public string? DashboardUrl { get; }
    public string? AuthorizationBearer { get; }
    public string? DiscoveryFilePath { get; }
    public int? SecondOriginHttpPort { get; }

    private TitaniumCliHost(
        Process process,
        string workDir,
        int port,
        string listenUrl,
        string? controlPlaneUrl,
        string? dashboardUrl,
        string? authorizationBearer,
        string? discoveryFilePath,
        HttpListener? jwksListener,
        CancellationTokenSource? jwksCts,
        RSA? jwtRsa,
        IAsyncDisposable? secondOrigin,
        int? secondOriginHttpPort)
    {
        this.process = process;
        this.workDir = workDir;
        Port = port;
        ListenUrl = listenUrl;
        ControlPlaneUrl = controlPlaneUrl;
        DashboardUrl = dashboardUrl;
        AuthorizationBearer = authorizationBearer;
        DiscoveryFilePath = discoveryFilePath;
        this.jwksListener = jwksListener;
        this.jwksCts = jwksCts;
        this.jwtRsa = jwtRsa;
        this.secondOrigin = secondOrigin;
        SecondOriginHttpPort = secondOriginHttpPort;
    }

    public enum CliArmKind
    {
        ForwardHost,
        ForwardHostTls,
        SingleRoute,
        PlusBase,
        PlusCache,
        InterceptTransform,
        PlusWaf,
        PlusCidr,
        PlusJwt,
        PlusRateLimit,
        PlusFleetRateLimit,
        PlusResilience,
        PlusDiscoveryFile,
        PlusMetricsScrape,
        PlusCacheHit,
        PlusCors,
        PlusCircuit,
        PlusRetry,
        StaticFiles,
        Logging,
        LbLeastTime,
        DialectTwp,
        Policies,
        Mirror,
        ClusterClient,
        StatusRetry,
        ConnCap,
        ExtAuth,

        /// <summary>Route middleware: a Lua plugin whose request-headers phase does nothing (the call cost alone).</summary>
        ScriptLuaNoop,

        /// <summary>Route middleware: a Lua plugin that sets one request header.</summary>
        ScriptLuaHeader,

        /// <summary>Route middleware: a proxy-wasm module that continues every request.</summary>
        ScriptWasmNoop,

        /// <summary>Baseline for <see cref="ScriptLuaSlowUpstream"/>: the slow origin, no script.</summary>
        SlowUpstream,

        /// <summary>A no-op Lua plugin in front of the slow origin; latency must stay flat (N6).</summary>
        ScriptLuaSlowUpstream,

        /// <summary>One route of fifty carries a Lua plugin; the load hits a plugin-free route (B3 cliff).</summary>
        ScriptOneInFifty
    }

    /// <summary>Per-request delay of the slow origin used by the slow-upstream arms.</summary>
    public const int SlowUpstreamDelayMs = 20;

    /// <summary>Routes in the <see cref="CliArmKind.ScriptOneInFifty"/> table.</summary>
    internal const int OneInFiftyRouteCount = 50;

    public static bool IsScriptArm(CliArmKind kind) => kind is
        CliArmKind.ScriptLuaNoop or CliArmKind.ScriptLuaHeader or CliArmKind.ScriptWasmNoop
        or CliArmKind.ScriptLuaSlowUpstream or CliArmKind.ScriptOneInFifty;

    public static bool UsesSlowOrigin(CliArmKind kind) => kind is CliArmKind.SlowUpstream or CliArmKind.ScriptLuaSlowUpstream;

    public static bool NeedsPlusDll(CliArmKind kind) => kind is
        CliArmKind.PlusBase or CliArmKind.PlusCache or CliArmKind.PlusWaf or CliArmKind.PlusCidr
        or CliArmKind.PlusJwt or CliArmKind.PlusRateLimit or CliArmKind.PlusFleetRateLimit
        or CliArmKind.PlusResilience
        or CliArmKind.PlusDiscoveryFile or CliArmKind.PlusMetricsScrape or CliArmKind.PlusCacheHit
        or CliArmKind.PlusCors or CliArmKind.PlusCircuit or CliArmKind.PlusRetry;

    public static bool NeedsControlPlane(CliArmKind kind) => NeedsPlusDll(kind);

    public static bool NeedsJsonConfig(CliArmKind kind) => kind is
        CliArmKind.SingleRoute or CliArmKind.InterceptTransform or CliArmKind.PlusResilience
        or CliArmKind.PlusDiscoveryFile or CliArmKind.LbLeastTime
        or CliArmKind.Policies or CliArmKind.PlusFleetRateLimit or CliArmKind.Mirror or CliArmKind.ClusterClient
        or CliArmKind.StatusRetry or CliArmKind.ConnCap or CliArmKind.ExtAuth
        or CliArmKind.ScriptLuaNoop or CliArmKind.ScriptLuaHeader or CliArmKind.ScriptWasmNoop
        or CliArmKind.SlowUpstream or CliArmKind.ScriptLuaSlowUpstream or CliArmKind.ScriptOneInFifty;

    public static async Task<TitaniumCliHost> StartAsync(int originHttpPort, CliArmKind kind,
        CancellationToken cancellationToken = default)
    {
        var cliDir = LocateCliDirectory();
        var cliDll = Path.Combine(cliDir, "titanium.dll");
        if (!File.Exists(cliDll))
        {
            throw new FileNotFoundException(
                "titanium.dll not found. Build/publish Titanium.Cli (Release) before edition arms.",
                cliDll);
        }

        if (NeedsPlusDll(kind))
            EnsurePlusDllBesideCli(cliDir);

        var port = GetFreeTcpPort();
        var controlPlanePort = NeedsControlPlane(kind) ? GetFreeTcpPort() : 0;
        var dashboardPort = NeedsControlPlane(kind) ? GetFreeTcpPort() : 0;
        var workDir = Path.Combine(Path.GetTempPath(), "twp-rps-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        HttpListener? jwksListener = null;
        CancellationTokenSource? jwksCts = null;
        RSA? jwtRsa = null;
        string? bearer = null;
        string? discoveryFile = null;
        IAsyncDisposable? secondOrigin = null;
        int? secondOriginPort = null;

        if (kind == CliArmKind.PlusJwt)
        {
            jwtRsa = RSA.Create(2048);
            (jwksListener, var jwksPort) = StartJwksListenerOrRetry();
            var authority = $"{JwtAuthorityHost}:{jwksPort}";
            var jwksUrl = $"{authority}/jwks.json";
            var kid = "rps-editions";
            // Mint + JWKS JSON before ServeJwksLoopAsync — Windows BCrypt RSA is not safe for
            // concurrent SignData/ExportParameters (SafeBCryptKeyHandle disposed / bad signatures).
            bearer = MintRs256Jwt(jwtRsa, authority, kid);
            var jwksJson = BuildJwksJson(jwtRsa, kid);
            jwksCts = new CancellationTokenSource();
            _ = Task.Run(() => ServeJwksLoopAsync(jwksListener, jwksJson, jwksCts.Token), jwksCts.Token);
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "jwt-meta.txt"),
                $"{authority}\n{jwksUrl}",
                cancellationToken);
        }

        if (kind == CliArmKind.LbLeastTime)
        {
            var origin2 = await OriginServer.StartAsync(new OriginListenOptions
            {
                EnableHttp = true,
                EnableHttps = false,
                ResponseBytes = WorkloadOptions.TinyJsonBytes
            }, cancellationToken);
            secondOrigin = origin2;
            secondOriginPort = origin2.HttpPort;
        }

        // The slow arms front their own origin so the shared origin stays fast for every other arm.
        var upstreamPort = originHttpPort;
        if (UsesSlowOrigin(kind))
        {
            var slow = await OriginServer.StartAsync(new OriginListenOptions
            {
                EnableHttp = true,
                EnableHttps = false,
                ResponseBytes = WorkloadOptions.TinyJsonBytes,
                ResponseDelayMs = SlowUpstreamDelayMs
            }, cancellationToken);
            secondOrigin = slow;
            upstreamPort = slow.HttpPort;
        }

        string? scriptPath = null;
        if (IsScriptArm(kind))
            scriptPath = await WriteScriptFileAsync(workDir, kind, cancellationToken);

        if (kind == CliArmKind.PlusDiscoveryFile)
        {
            discoveryFile = Path.Combine(workDir, "discovery-clusters.json");
            await File.WriteAllTextAsync(discoveryFile, BuildDiscoveryClustersJson(originHttpPort, "d1"),
                cancellationToken);
        }

        string configPath;
        if (kind == CliArmKind.DialectTwp)
        {
            configPath = Path.Combine(workDir, "sites.twp");
            await File.WriteAllTextAsync(configPath, BuildSiteFile(port, originHttpPort), Encoding.UTF8,
                cancellationToken);
        }
        else if (NeedsJsonConfig(kind))
        {
            configPath = Path.Combine(workDir, "twp.json");
            await File.WriteAllTextAsync(configPath,
                BuildJson(kind, port, upstreamPort, controlPlanePort, secondOriginPort, discoveryFile,
                    dashboardPort, scriptPath),
                Encoding.UTF8, cancellationToken);
        }
        else
        {
            configPath = Path.Combine(workDir, "twp.yaml");
            string? jwtAuthority = null;
            string? jwksUrl = null;
            if (kind == CliArmKind.PlusJwt)
            {
                var meta = (await File.ReadAllTextAsync(Path.Combine(workDir, "jwt-meta.txt"), cancellationToken))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                jwtAuthority = meta[0];
                jwksUrl = meta[1];
            }

            string? staticRoot = null;
            if (kind == CliArmKind.StaticFiles)
            {
                staticRoot = Path.Combine(workDir, "www");
                Directory.CreateDirectory(staticRoot);
                await File.WriteAllTextAsync(Path.Combine(staticRoot, "index.html"),
                    "<html><body>ok</body></html>", cancellationToken);
            }

            string? logFile = null;
            if (kind == CliArmKind.Logging)
            {
                logFile = Path.Combine(workDir, "twp-rps.log");
            }

            await File.WriteAllTextAsync(configPath,
                BuildYaml(kind, port, originHttpPort, controlPlanePort, jwtAuthority, jwksUrl, staticRoot, logFile,
                    dashboardPort),
                Encoding.UTF8, cancellationToken);
        }

        var listenScheme = kind == CliArmKind.ForwardHostTls ? "https" : "http";
        var listenUrl = $"{listenScheme}://127.0.0.1:{port}/";
        var controlPlaneUrl = controlPlanePort > 0 ? $"http://127.0.0.1:{controlPlanePort}/" : null;
        var dashboardUrl = dashboardPort > 0 ? $"http://127.0.0.1:{dashboardPort}/" : null;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = cliDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        if (NeedsPlusDll(kind))
            psi.Environment["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1";

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start titanium CLI.");

        var host = new TitaniumCliHost(process, workDir, port, listenUrl, controlPlaneUrl, dashboardUrl, bearer,
            discoveryFile, jwksListener, jwksCts, jwtRsa, secondOrigin, secondOriginPort);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (host.gate) host.stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (host.gate) host.stderr.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await host.WaitForReadyAsync(TimeSpan.FromSeconds(45), cancellationToken);
        }
        catch
        {
            string boot;
            lock (host.gate) boot = host.stdout.ToString() + host.stderr.ToString();
            ProbeLog.Error($"  titanium CLI failed. output:\n{boot}");
            host.Dispose();
            throw;
        }

        return host;
    }

    /// <summary>Rewrite discovery file mid-ramp with an equivalent healthy destination (same origin).</summary>
    public static void RewriteDiscoveryFile(string path, int originHttpPort)
    {
        File.WriteAllText(path, BuildDiscoveryClustersJson(originHttpPort, "d1-rewrite"));
    }

    public static string BuildDiscoveryClustersJson(int originHttpPort, string destinationId) =>
        $$"""
        {"clusters":[{"id":"c1","algorithm":"RoundRobin","destinations":[{"id":"{{destinationId}}","address":"127.0.0.1","port":{{originHttpPort}}}]}]}
        """;

    private async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var sawRunning = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outText;
            string errText;
            lock (gate)
            {
                outText = stdout.ToString();
                errText = stderr.ToString();
            }

            if (!sawRunning &&
                (outText.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                 errText.Contains("running", StringComparison.OrdinalIgnoreCase)))
                sawRunning = true;

            if (sawRunning && await TryListenProbeAsync(cancellationToken))
            {
                // Short settle so Plus/dashboard JIT is not billed to the first measure step
                // (macos-15-intel editions were failing validate as "missing arm" when c=64 p99
                // missed the 50ms SLO after a cold start).
                await SettleListenAsync(cancellationToken);
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"titanium CLI exited early ({process.ExitCode}). stdout={outText} stderr={errText}");
            }

            await Task.Delay(100, cancellationToken);
        }

        string finalOut;
        string finalErr;
        lock (gate)
        {
            finalOut = stdout.ToString();
            finalErr = stderr.ToString();
        }

        throw new TimeoutException(
            $"Timed out waiting for titanium CLI ready. stdout={finalOut} stderr={finalErr}");
    }

    private async Task<bool> TryListenProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(ListenUrl, cts.Token);
            return (int)response.StatusCode is >= 200 and < 500;
        }
        catch
        {
            return false;
        }
    }

    private async Task SettleListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var response = await client.GetAsync(ListenUrl, cancellationToken);
                    _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                }
                catch
                {
                    // best-effort warm
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // settle is best-effort
        }
    }

    /// <summary>Lua source of the script arms. A function that returns nothing lets the request through.</summary>
    internal const string LuaNoopSource = "function on_request_headers(ctx) end\n";

    internal const string LuaHeaderSource =
        "function on_request_headers(ctx)\n  ctx.set_header(\"x-twp-rps-script\", \"1\")\nend\n";

    /// <summary>
    /// proxy-wasm 0.2.1 module with no imports that continues every request: the cost of entering a Wasmtime VM and
    /// leaving it, with no host call in between.
    /// </summary>
    internal const string WasmNoopSource = """
        (module
          (memory (export "memory") 2)
          (global $heap (mut i32) (i32.const 8192))
          (func (export "proxy_abi_version_0_2_1"))
          (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
            (local $p i32)
            (local.set $p (global.get $heap))
            (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
            (local.get $p))
          (func (export "proxy_on_context_create") (param i32 i32))
          (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
          (func (export "proxy_on_configure") (param i32 i32) (result i32) (i32.const 1))
          (func (export "proxy_on_request_headers") (param i32 i32 i32) (result i32) (i32.const 0))
          (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
          (func (export "proxy_on_delete") (param i32))
        )
        """;

    private static async Task<string> WriteScriptFileAsync(string workDir, CliArmKind kind,
        CancellationToken cancellationToken)
    {
        var (name, source) = kind switch
        {
            CliArmKind.ScriptLuaHeader => ("script.lua", LuaHeaderSource),
            CliArmKind.ScriptWasmNoop => ("script.wat", WasmNoopSource),
            _ => ("script.lua", LuaNoopSource)
        };
        var path = Path.Combine(workDir, name);
        // No BOM: Lua and the WAT parser both reject a leading U+FEFF.
        await File.WriteAllTextAsync(path, source, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static string PluginsJson(CliArmKind kind, string? scriptPath)
    {
        if (!IsScriptArm(kind) || scriptPath is null)
            return "";
        var engine = kind == CliArmKind.ScriptWasmNoop ? "wasm" : "lua";
        var file = scriptPath.Replace("\\", "/", StringComparison.Ordinal);
        return ", \"plugins\": [ { \"name\": \"script\", \"kind\": \"" + engine + "\", \"file\": \"" + file +
               "\", \"failMode\": \"closed\", \"timeoutMs\": 1000 } ]";
    }

    /// <summary>
    /// Route table of the one-in-fifty arm: route 1 carries the plugin on <c>/n/1</c>, routes 2..49 are plain prefixes,
    /// and the last route is the catch-all the load hits, so the plugin route is never the one that serves traffic.
    /// </summary>
    private static string OneInFiftyRoutes()
    {
        var sb = new StringBuilder();
        for (var i = 1; i < OneInFiftyRouteCount; i++)
        {
            if (sb.Length > 0) sb.Append(",\n");
            sb.Append($"{{ \"id\": \"n{i}\", \"clusterId\": \"c1\", \"order\": {i}, ");
            if (i == 1) sb.Append("\"middleware\": [ \"script\" ], ");
            sb.Append($"\"match\": {{ \"path\": \"/n/{i}\", \"pathKind\": \"Prefix\" }} }}");
        }

        sb.Append(",\n{ \"id\": \"r1\", \"clusterId\": \"c1\", \"order\": ").Append(OneInFiftyRouteCount)
            .Append(", \"match\": { \"path\": \"/\", \"pathKind\": \"Prefix\" } }");
        return sb.ToString();
    }

    internal static string BuildSiteFile(int listenPort, int originPort) =>
        // forward before listen so SiteFileReader applies pending ForwardHost on listen create.
        $"forward 127.0.0.1:{originPort}\nlisten 127.0.0.1:{listenPort}\n";

    internal static string BuildJson(CliArmKind kind, int listenPort, int originPort, int controlPlanePort = 0,
        int? secondOriginPort = null, string? discoveryFile = null, int dashboardPort = 0, string? scriptPath = null)
    {
        var algorithm = kind == CliArmKind.LbLeastTime ? "LeastTime" : "RoundRobin";
        var destinations = kind == CliArmKind.LbLeastTime && secondOriginPort is int p2
            ? $$"""
                      { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} },
                      { "id": "d2", "address": "127.0.0.1", "port": {{p2}} }
                """
            : $$"""
                      { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} }
                """;

        var routeExtras = kind switch
        {
            CliArmKind.InterceptTransform =>
                "\"transforms\": [ { \"kind\": \"RequestHeaderSet\", \"parameters\": { \"name\": \"x-twp-rps-probe\", \"value\": \"1\" } } ],",
            CliArmKind.Policies or CliArmKind.PlusFleetRateLimit => "\"policies\": [ \"limit\" ],",
            CliArmKind.ExtAuth => "\"policies\": [ \"auth\" ],",
            CliArmKind.Mirror => "\"mirror\": { \"clusterId\": \"mirror\", \"samplePercent\": 100 },",
            CliArmKind.StatusRetry => "\"retry\": { \"attempts\": 1 },",
            CliArmKind.ScriptLuaNoop or CliArmKind.ScriptLuaHeader or CliArmKind.ScriptWasmNoop
                or CliArmKind.ScriptLuaSlowUpstream => "\"middleware\": [ \"script\" ],",
            _ => "",
        };
        var routeBody =
            "\"id\": \"r1\",\n" +
            "\"clusterId\": \"c1\",\n" +
            "\"order\": 1,\n" +
            (routeExtras.Length == 0 ? "" : routeExtras + "\n") +
            "\"match\": { \"path\": \"/\", \"pathKind\": \"Prefix\" }";
        var routesJson = kind == CliArmKind.ScriptOneInFifty ? OneInFiftyRoutes() : "{\n" + routeBody + "\n}";
        var pluginsBlockJson = PluginsJson(kind, scriptPath);
        var httpClient = kind switch
        {
            CliArmKind.ClusterClient =>
                "\"httpClient\": { \"maxConnectionsPerServer\": 64, \"connectTimeout\": \"00:00:02\" },",
            CliArmKind.ConnCap =>
                "\"httpClient\": { \"maxConnectionsPerServer\": 32 },",
            _ => "",
        };
        var mirrorCluster = kind == CliArmKind.Mirror
            ? ", { \"id\": \"mirror\", \"destinations\": [ { \"id\": \"m1\", \"address\": \"127.0.0.1\", \"port\": " + originPort + " } ] }"
            : "";
        // extAuth reuses the origin as the authorization service: every GET answers 200, so each request allows.
        var policiesBlock = kind switch
        {
            CliArmKind.Policies =>
                ", \"policies\": [ { \"name\": \"limit\", \"kind\": \"rateLimit\", \"limit\": 10000000, \"windowSeconds\": 60 } ]",
            // Fleet path with no Redis: fleetFailMode local measures lease-path overhead without a store.
            CliArmKind.PlusFleetRateLimit =>
                ", \"policies\": [ { \"name\": \"limit\", \"kind\": \"rateLimit\", \"limit\": 10000000, \"windowSeconds\": 60, \"scope\": \"fleet\", \"fleetFailMode\": \"local\", \"algorithm\": \"tokenBucket\", \"burst\": 10000000 } ]",
            CliArmKind.ExtAuth =>
                ", \"policies\": [ { \"name\": \"auth\", \"kind\": \"extAuth\", \"url\": \"http://127.0.0.1:" + originPort + "/auth\" } ]",
            _ => "",
        };

        var plusBlock = "";
        if (kind is CliArmKind.PlusResilience or CliArmKind.PlusDiscoveryFile or CliArmKind.PlusFleetRateLimit)
        {
            var options = kind switch
            {
                CliArmKind.PlusResilience => """
                      "resilience.activeHealth": "true",
                      "resilience.intervalMs": "500",
                      "resilience.path": "/",
                      "resilience.unhealthyThreshold": "10"
                    """,
                CliArmKind.PlusDiscoveryFile => $$"""
                      "discovery.mode": "file",
                      "discovery.file": "{{(discoveryFile ?? "").Replace("\\", "/", StringComparison.Ordinal)}}"
                    """,
                // No state.redis: fleet store stays off; Core applies local fail-mode at the high ceiling.
                _ => """
                      "state.mode": "memory",
                      "state.rateLimitPerMinute": "10000000"
                    """,
            };
            var dashJson = dashboardPort > 0
                ? $",\n                  \"dashboardPort\": {dashboardPort}"
                : "";
            plusBlock = $$"""
              ,
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{controlPlanePort}}{{dashJson}},
                  "sharedSecret": "{{ControlPlaneSharedSecret}}"
                },
                "options": {
            {{options}}
                }
              }
            """;
        }

        return $$"""
            {
              "schemaVersion": "7.0",
              "logging": { "enabled": false, "enableConsole": false },
              "listeners": [
                {
                  "host": "127.0.0.1",
                  "port": {{listenPort}},
                  "decryptSsl": false,
                  "enableHttp2": false,
                  "forwardHost": "127.0.0.1",
                  "forwardPort": {{originPort}}
                }
              ],
              "routes": [
            {{routesJson}}
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "{{algorithm}}",
            {{httpClient}}      "destinations": [
            {{destinations}}
                  ]
                }
            {{mirrorCluster}}
              ]
            {{policiesBlock}}
            {{pluginsBlockJson}}
            {{plusBlock}}
            }
            """;
    }

    internal static string BuildYaml(CliArmKind kind, int listenPort, int originPort, int controlPlanePort,
        string? jwtAuthority = null, string? jwksUrl = null, string? staticRoot = null, string? logFile = null,
        int dashboardPort = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("schemaVersion: \"7.0\"");

        if (kind == CliArmKind.Logging)
        {
            sb.AppendLine("logging:");
            sb.AppendLine("  enabled: true");
            sb.AppendLine("  minimumLevel: Information");
            sb.AppendLine("  enableConsole: false");
            sb.AppendLine("  enableFile: true");
            sb.AppendLine($"  filePath: \"{(logFile ?? "twp.log").Replace("\\", "/", StringComparison.Ordinal)}\"");
        }
        else
        {
            sb.AppendLine("logging:");
            sb.AppendLine("  enabled: false");
            sb.AppendLine("  enableConsole: false");
        }

        switch (kind)
        {
            case CliArmKind.ForwardHost:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                break;
            case CliArmKind.ForwardHostTls:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: true);
                break;
            case CliArmKind.StaticFiles:
                // Transparent + ForwardHost (unused when static handles /) so the listen path
                // matches other reverse arms; session path is forced by staticFiles.root.
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                sb.AppendLine("staticFiles:");
                sb.AppendLine($"  root: \"{(staticRoot ?? ".").Replace("\\", "/", StringComparison.Ordinal)}\"");
                sb.AppendLine("  enableGzip: false");
                break;
            case CliArmKind.Logging:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                break;
            case CliArmKind.PlusBase:
            case CliArmKind.PlusMetricsScrape:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, null, dashboardPort);
                break;
            case CliArmKind.PlusCache:
            case CliArmKind.PlusCacheHit:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["cache.enable"] = "true"
                }, dashboardPort);
                break;
            case CliArmKind.PlusCors:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["cors.enabled"] = "true",
                    ["cors.allowOrigin"] = "*"
                }, dashboardPort);
                break;
            case CliArmKind.PlusCircuit:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["resilience.circuit.enabled"] = "true"
                }, dashboardPort);
                break;
            case CliArmKind.PlusRetry:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["resilience.retry.idempotentAttempts"] = "1"
                }, dashboardPort);
                break;
            case CliArmKind.PlusWaf:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["waf.enabled"] = "true",
                    ["waf.denyPaths"] = "^/admin"
                }, dashboardPort);
                break;
            case CliArmKind.PlusCidr:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["security.allowCidrs"] = "127.0.0.0/8"
                }, dashboardPort);
                break;
            case CliArmKind.PlusJwt:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["security.jwtAuthority"] = jwtAuthority ?? "http://127.0.0.1",
                    ["security.jwksUrl"] = jwksUrl ?? "http://127.0.0.1/jwks.json"
                }, dashboardPort);
                break;
            case CliArmKind.PlusRateLimit:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["state.mode"] = "memory",
                    ["state.rateLimitPerMinute"] = "10000000"
                }, dashboardPort);
                break;
            case CliArmKind.SingleRoute:
            case CliArmKind.InterceptTransform:
            case CliArmKind.PlusResilience:
            case CliArmKind.PlusDiscoveryFile:
            case CliArmKind.LbLeastTime:
            case CliArmKind.DialectTwp:
            case CliArmKind.ScriptLuaNoop:
            case CliArmKind.ScriptLuaHeader:
            case CliArmKind.ScriptWasmNoop:
            case CliArmKind.SlowUpstream:
            case CliArmKind.ScriptLuaSlowUpstream:
            case CliArmKind.ScriptOneInFifty:
                throw new InvalidOperationException($"{kind} must use BuildJson / BuildSiteFile, not BuildYaml.");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return sb.ToString();
    }

    private static void AppendListenerOnly(StringBuilder sb, int listenPort, bool decryptSsl)
    {
        sb.AppendLine("listeners:");
        sb.AppendLine("  - host: \"127.0.0.1\"");
        sb.AppendLine($"    port: {listenPort}");
        sb.AppendLine($"    decryptSsl: {(decryptSsl ? "true" : "false")}");
        sb.AppendLine("    enableHttp2: false");
    }

    private static void AppendForwardHostListener(StringBuilder sb, int listenPort, int originPort, bool decryptSsl)
    {
        sb.AppendLine("listeners:");
        sb.AppendLine("  - host: \"127.0.0.1\"");
        sb.AppendLine($"    port: {listenPort}");
        sb.AppendLine($"    decryptSsl: {(decryptSsl ? "true" : "false")}");
        sb.AppendLine("    forwardHost: \"127.0.0.1\"");
        sb.AppendLine($"    forwardPort: {originPort}");
        sb.AppendLine("    enableHttp2: false");
    }

    private static void AppendPlus(StringBuilder sb, int controlPlanePort, Dictionary<string, string>? options,
        int dashboardPort = 0)
    {
        sb.AppendLine("plus:");
        sb.AppendLine("  enabled: true");
        sb.AppendLine("  controlPlane:");
        sb.AppendLine("    host: \"127.0.0.1\"");
        sb.AppendLine($"    port: {controlPlanePort}");
        if (dashboardPort > 0)
            sb.AppendLine($"    dashboardPort: {dashboardPort}");
        sb.AppendLine($"    sharedSecret: \"{ControlPlaneSharedSecret}\"");
        if (options is { Count: > 0 })
        {
            sb.AppendLine("  options:");
            foreach (var (key, value) in options)
                sb.AppendLine($"    {key}: \"{value}\"");
        }
    }

    /// <summary>
    /// Bind JWKS <see cref="HttpListener"/> with retries against Windows TOCTOU / excluded-port races
    /// after <see cref="GetFreeTcpPort"/>.
    /// </summary>
    private static (HttpListener Listener, int Port) StartJwksListenerOrRetry(int maxAttempts = 8)
    {
        Exception? last = null;
        for (var i = 0; i < maxAttempts; i++)
        {
            var port = GetFreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try
                {
                    listener.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to bind JWKS HttpListener after {maxAttempts} attempts.", last);
    }

    private static async Task ServeJwksLoopAsync(HttpListener listener, string jwksJson,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(jwksJson);
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch
            {
                return;
            }

            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                ctx.Response.Close();
            }
            catch
            {
                // best-effort
            }
        }
    }

    internal static string BuildJwksJson(RSA rsa, string kid)
    {
        var parms = rsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid,
                    n = Base64Url(parms.Modulus!),
                    e = Base64Url(parms.Exponent!)
                }
            }
        });
    }

    internal static string MintRs256Jwt(RSA rsa, string issuer, string kid)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"alg":"RS256","typ":"JWT","kid":"{{kid}}"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{issuer}}","iat":{{now}},"nbf":{{now - 60}},"exp":{{now + 7200}}}"""));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        var sig = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(sig);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string LocateCliDirectory()
    {
        var env = Environment.GetEnvironmentVariable("TWP_RPS_CLI_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "titanium.dll")))
            return Path.GetFullPath(env);

        var probeBase = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(probeBase, "..", "..", "..", "..", "Titanium.Cli", "bin", "Release", "net10.0")),
            Path.GetFullPath(Path.Combine(probeBase, "..", "..", "..", "..", "Titanium.Cli", "bin", "Debug", "net10.0")),
            Path.GetFullPath(Path.Combine(probeBase, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", "net10.0")),
            Path.GetFullPath(Path.Combine(probeBase, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Debug", "net10.0")),
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "titanium.dll")))
                return dir;
        }

        var dirInfo = new DirectoryInfo(probeBase);
        while (dirInfo != null)
        {
            var release = Path.Combine(dirInfo.FullName, "src", "Titanium.Cli", "bin", "Release", "net10.0");
            if (File.Exists(Path.Combine(release, "titanium.dll")))
                return release;
            var debug = Path.Combine(dirInfo.FullName, "src", "Titanium.Cli", "bin", "Debug", "net10.0");
            if (File.Exists(Path.Combine(debug, "titanium.dll")))
                return debug;
            dirInfo = dirInfo.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate titanium.dll. Build src/Titanium.Cli or set TWP_RPS_CLI_DIR.");
    }

    private static void EnsurePlusDllBesideCli(string cliDir)
    {
        var dest = Path.Combine(cliDir, "Titanium.Plus.dll");
        var plusDir = LocatePlusOutputDirectory();
        foreach (var file in Directory.EnumerateFiles(plusDir, "*.dll"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Titanium.Web.Proxy", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Titanium.Plus.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(cliDir, name);
            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Destination locked by a concurrent titanium host — keep existing copy.
            }
        }

        if (!File.Exists(dest))
        {
            throw new FileNotFoundException(
                "Titanium.Plus.dll not found beside CLI after copy. Build src/Titanium.Plus before Plus edition arms.",
                dest);
        }
    }

    private static string LocatePlusOutputDirectory()
    {
        var dirInfo = new DirectoryInfo(AppContext.BaseDirectory);
        while (dirInfo != null)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var plus = Path.Combine(dirInfo.FullName, "src", "Titanium.Plus", "bin", config, "net10.0",
                    "Titanium.Plus.dll");
                if (File.Exists(plus))
                    return Path.GetDirectoryName(plus)!;
            }

            dirInfo = dirInfo.Parent;
        }

        throw new FileNotFoundException(
            "Titanium.Plus.dll not found. Build src/Titanium.Plus before Plus edition arms.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            jwksCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            jwksListener?.Stop();
            jwksListener?.Close();
        }
        catch
        {
            // ignore
        }

        jwksCts?.Dispose();
        jwtRsa?.Dispose();

        try
        {
            secondOrigin?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

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
            // best-effort teardown
        }

        try
        {
            process.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch
        {
            // temp cleanup best-effort
        }
    }
}
