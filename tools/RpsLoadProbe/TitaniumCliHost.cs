using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Spawns the shipped <c>titanium run -c twp.yaml</c> daemon as an external process
/// (same shape as <see cref="NginxHost"/>) for product-defaults edition arms.
/// </summary>
internal sealed class TitaniumCliHost : IDisposable
{
    private readonly Process process;
    private readonly string workDir;
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly object gate = new();

    public int Port { get; }
    public string ListenUrl { get; }
    public int? ProcessId => process.HasExited ? null : process.Id;

    private TitaniumCliHost(Process process, string workDir, int port, string listenUrl)
    {
        this.process = process;
        this.workDir = workDir;
        Port = port;
        ListenUrl = listenUrl;
    }

    public enum CliArmKind
    {
        ForwardHost,
        ForwardHostTls,
        SingleRoute,
        PlusBase,
        PlusCache,
        InterceptTransform
    }

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

        if (kind is CliArmKind.PlusBase or CliArmKind.PlusCache)
            EnsurePlusDllBesideCli(cliDir);

        var port = GetFreeTcpPort();
        var controlPlanePort = kind is CliArmKind.PlusBase or CliArmKind.PlusCache
            ? GetFreeTcpPort()
            : 0;
        var workDir = Path.Combine(Path.GetTempPath(), "twp-rps-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        // TLS arm: omit certificates.* and let CLI CertificateManager mint a leaf.
        // Custom PFX/PEM leaves often fail Schannel SslStream (EOF on handshake) while
        // the probe client already accepts any server cert. Still exercises titanium run
        // TLS-terminate → ForwardCleartext origin — the product path under test.

        // Route/transform arms use JSON — YamlDotNet cannot materialize IReadOnlyList on
        // DestinationConfig / TransformConfig (same pattern as E2E ConfigFixtures).
        var useJson = kind is CliArmKind.SingleRoute or CliArmKind.InterceptTransform;
        var configPath = Path.Combine(workDir, useJson ? "twp.json" : "twp.yaml");
        var content = useJson
            ? BuildJson(kind, port, originHttpPort)
            : BuildYaml(kind, port, originHttpPort, controlPlanePort);
        await File.WriteAllTextAsync(configPath, content, Encoding.UTF8, cancellationToken);

        var listenScheme = kind == CliArmKind.ForwardHostTls ? "https" : "http";
        var listenUrl = $"{listenScheme}://127.0.0.1:{port}/";

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
        if (kind is CliArmKind.PlusBase or CliArmKind.PlusCache)
            psi.Environment["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1";

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start titanium CLI.");

        var host = new TitaniumCliHost(process, workDir, port, listenUrl);
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

    internal static string BuildJson(CliArmKind kind, int listenPort, int originPort)
    {
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

        // Listener keeps ForwardHost so AddListener creates TransparentProxyEndPoint.
        // SingleRoute (no transforms) is ForwardHost-equivalent → H1 terminate-lite fast path
        // (Gate 1: route ÷ ForwardHost ≥ 0.98). InterceptTransform fails equivalence and forces
        // EnableHttpInterception via ConfigNeedsSessionPath.
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
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} }
                  ]
                }
              ]
            }
            """;
    }

    internal static string BuildYaml(CliArmKind kind, int listenPort, int originPort, int controlPlanePort)
    {
        var sb = new StringBuilder();
        sb.AppendLine("schemaVersion: \"7.0\"");
        sb.AppendLine("logging:");
        sb.AppendLine("  enabled: false");
        sb.AppendLine("  enableConsole: false");

        switch (kind)
        {
            case CliArmKind.ForwardHost:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                break;
            case CliArmKind.ForwardHostTls:
                // No certificates block — CertificateManager creates a working terminate leaf.
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: true);
                break;
            case CliArmKind.PlusBase:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, cache: false);
                break;
            case CliArmKind.PlusCache:
                AppendForwardHostListener(sb, listenPort, originPort, decryptSsl: false);
                AppendPlus(sb, controlPlanePort, cache: true);
                break;
            case CliArmKind.SingleRoute:
            case CliArmKind.InterceptTransform:
                throw new InvalidOperationException($"{kind} must use BuildJson (YAML cannot deserialize IReadOnlyList destinations/transforms).");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return sb.ToString();
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

    private static void AppendPlus(StringBuilder sb, int controlPlanePort, bool cache)
    {
        sb.AppendLine("plus:");
        sb.AppendLine("  enabled: true");
        sb.AppendLine("  controlPlane:");
        sb.AppendLine("    host: \"127.0.0.1\"");
        sb.AppendLine($"    port: {controlPlanePort}");
        sb.AppendLine("    sharedSecret: \"changeme\"");
        if (cache)
        {
            sb.AppendLine("  options:");
            sb.AppendLine("    cache.enable: \"true\"");
        }
    }

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

        // Walk up from repo tools/RpsLoadProbe
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
        // Mirror E2E CliProcessHarness: Plus ALC resolves deps from AppContext.BaseDirectory,
        // so StackExchange.Redis / IdentityModel / etc. must sit beside titanium.dll.
        foreach (var file in Directory.EnumerateFiles(plusDir, "*.dll"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Titanium.Web.Proxy", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Titanium.Plus.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(cliDir, name), overwrite: true);
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
