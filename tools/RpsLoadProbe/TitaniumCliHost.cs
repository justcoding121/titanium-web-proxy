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
    public string? AuthorizationBearer { get; }
    public string? DiscoveryFilePath { get; }
    public int? SecondOriginHttpPort { get; }

    private TitaniumCliHost(
        Process process,
        string workDir,
        int port,
        string listenUrl,
        string? controlPlaneUrl,
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
        PlusResilience,
        PlusDiscoveryFile,
        PlusMetricsScrape,
        PlusCacheHit,
        StaticFiles,
        Logging,
        LbLeastTime,
        DialectTwp
    }

    public static bool NeedsPlusDll(CliArmKind kind) => kind is
        CliArmKind.PlusBase or CliArmKind.PlusCache or CliArmKind.PlusWaf or CliArmKind.PlusCidr
        or CliArmKind.PlusJwt or CliArmKind.PlusRateLimit or CliArmKind.PlusResilience
        or CliArmKind.PlusDiscoveryFile or CliArmKind.PlusMetricsScrape or CliArmKind.PlusCacheHit;

    public static bool NeedsControlPlane(CliArmKind kind) => NeedsPlusDll(kind);

    public static bool NeedsJsonConfig(CliArmKind kind) => kind is
        CliArmKind.SingleRoute or CliArmKind.InterceptTransform or CliArmKind.PlusResilience
        or CliArmKind.PlusDiscoveryFile or CliArmKind.LbLeastTime;

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
            var jwksPort = GetFreeTcpPort();
            var authority = $"{JwtAuthorityHost}:{jwksPort}";
            var jwksUrl = $"{authority}/jwks.json";
            var kid = "rps-editions";
            // Mint + JWKS JSON before ServeJwksLoopAsync — Windows BCrypt RSA is not safe for
            // concurrent SignData/ExportParameters (SafeBCryptKeyHandle disposed / bad signatures).
            bearer = MintRs256Jwt(jwtRsa, authority, kid);
            var jwksJson = BuildJwksJson(jwtRsa, kid);
            jwksListener = StartJwksListener(jwksPort);
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
                BuildJson(kind, port, originHttpPort, controlPlanePort, secondOriginPort, discoveryFile),
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
                BuildYaml(kind, port, originHttpPort, controlPlanePort, jwtAuthority, jwksUrl, staticRoot, logFile),
                Encoding.UTF8, cancellationToken);
        }

        var listenScheme = kind == CliArmKind.ForwardHostTls ? "https" : "http";
        var listenUrl = $"{listenScheme}://127.0.0.1:{port}/";
        var controlPlaneUrl = controlPlanePort > 0 ? $"http://127.0.0.1:{controlPlanePort}/" : null;

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

        var host = new TitaniumCliHost(process, workDir, port, listenUrl, controlPlaneUrl, bearer, discoveryFile,
            jwksListener, jwksCts, jwtRsa, secondOrigin, secondOriginPort);
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

            if (outText.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                errText.Contains("running", StringComparison.OrdinalIgnoreCase))
                return;

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

    internal static string BuildSiteFile(int listenPort, int originPort) =>
        // forward before listen so SiteFileReader applies pending ForwardHost on listen create.
        $"forward 127.0.0.1:{originPort}\nlisten 127.0.0.1:{listenPort}\n";

    internal static string BuildJson(CliArmKind kind, int listenPort, int originPort, int controlPlanePort = 0,
        int? secondOriginPort = null, string? discoveryFile = null)
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

        var routeBody = kind == CliArmKind.InterceptTransform
            ? """
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "/", "pathKind": "Prefix" },
                  "transforms": [
                    { "kind": "RequestHeaderSet", "parameters": { "name": "x-twp-rps-probe", "value": "1" } }
                  ]
                """
            : """
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "/", "pathKind": "Prefix" }
                """;

        var plusBlock = "";
        if (kind is CliArmKind.PlusResilience or CliArmKind.PlusDiscoveryFile)
        {
            var options = kind == CliArmKind.PlusResilience
                ? """
                      "resilience.activeHealth": "true",
                      "resilience.intervalMs": "500",
                      "resilience.path": "/",
                      "resilience.unhealthyThreshold": "10"
                    """
                : $$"""
                      "discovery.mode": "file",
                      "discovery.file": "{{(discoveryFile ?? "").Replace("\\", "/", StringComparison.Ordinal)}}"
                    """;
            plusBlock = $$"""
              ,
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{controlPlanePort}},
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
                {
            {{routeBody}}
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "{{algorithm}}",
                  "destinations": [
            {{destinations}}
                  ]
                }
              ]
            {{plusBlock}}
            }
            """;
    }

    internal static string BuildYaml(CliArmKind kind, int listenPort, int originPort, int controlPlanePort,
        string? jwtAuthority = null, string? jwksUrl = null, string? staticRoot = null, string? logFile = null)
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
                AppendPlus(sb, controlPlanePort, null);
                break;
            case CliArmKind.PlusCache:
            case CliArmKind.PlusCacheHit:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["cache.enable"] = "true"
                });
                break;
            case CliArmKind.PlusWaf:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["waf.enabled"] = "true",
                    ["waf.denyPaths"] = "^/admin"
                });
                break;
            case CliArmKind.PlusCidr:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["security.allowCidrs"] = "127.0.0.0/8"
                });
                break;
            case CliArmKind.PlusJwt:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["security.jwtAuthority"] = jwtAuthority ?? "http://127.0.0.1",
                    ["security.jwksUrl"] = jwksUrl ?? "http://127.0.0.1/jwks.json"
                });
                break;
            case CliArmKind.PlusRateLimit:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, new Dictionary<string, string>
                {
                    ["state.mode"] = "memory",
                    ["state.rateLimitPerMinute"] = "10000000"
                });
                break;
            case CliArmKind.SingleRoute:
            case CliArmKind.InterceptTransform:
            case CliArmKind.PlusResilience:
            case CliArmKind.PlusDiscoveryFile:
            case CliArmKind.LbLeastTime:
            case CliArmKind.DialectTwp:
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

    private static void AppendPlus(StringBuilder sb, int controlPlanePort, Dictionary<string, string>? options)
    {
        sb.AppendLine("plus:");
        sb.AppendLine("  enabled: true");
        sb.AppendLine("  controlPlane:");
        sb.AppendLine("    host: \"127.0.0.1\"");
        sb.AppendLine($"    port: {controlPlanePort}");
        sb.AppendLine($"    sharedSecret: \"{ControlPlaneSharedSecret}\"");
        if (options is { Count: > 0 })
        {
            sb.AppendLine("  options:");
            foreach (var (key, value) in options)
                sb.AppendLine($"    {key}: \"{value}\"");
        }
    }

    private static HttpListener StartJwksListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
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
