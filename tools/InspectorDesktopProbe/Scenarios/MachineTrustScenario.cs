using System.Net;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Core-only machine CA trust diagnostics (formerly SystemTrustProbe). No Avalonia.
/// </summary>
public static class MachineTrustScenario
{
    public static async Task<int> RunAsync(string[] args, ProbeLog log)
    {
        var sub = args.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "help";
        var cache = Path.Combine(Path.GetTempPath(), "ti-system-trust-probe");
        Directory.CreateDirectory(cache);
        var ctx = new CacheContext(cache);

        CertificateManager.SuppressInteractiveRootStoreMutations = false;

        try
        {
            switch (sub)
            {
                case "clean":
                    Clean(ctx, log);
                    return 0;
                case "install":
                case "install-system":
                    InstallSystem(ctx, log);
                    return 0;
                case "remove":
                case "remove-system":
                    RemoveSystem(ctx, log);
                    return 0;
                case "run":
                    await RunProxyAsync(ctx, args, log).ConfigureAwait(false);
                    return 0;
                case "status":
                    PrintStatus(ctx, log);
                    log.Step("machine-trust-status", true, "dumped");
                    return 0;
                case "curl-check":
                    CurlCheck(ctx, log);
                    return 0;
                case "help":
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    log.Error($"Unknown machine-trust subcommand: {sub}");
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(cache, "exceptions.log"),
                    $"[{DateTime.Now:HH:mm:ss}] FATAL {ex}\n");
            }
            catch
            {
                /* ignore */
            }

            log.Error(ex.ToString());
            return 1;
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            machine-trust — machine (System.keychain / LocalMachine) CA trust diagnostics.

            Usage:
              dotnet run --project tools/InspectorDesktopProbe -- machine-trust <subcommand>

            Subcommands:
              status         Dump keychain / trust settings (macOS security find-certificate)
              install        TrustRootCertificateAsAdmin(machine=true) — admin password
              remove         RemoveTrustedRootCertificateAsAdmin(machine=true)
              run            Decrypt proxy + optional system proxy (create stop file to exit)
              curl-check     curl via probe port without -k (expects System CA trusted)
              clean          Remove system + user Titanium roots

            Aliases: install-system, remove-system
            Flags:   run --no-system-proxy
            """);
    }

    private sealed class CacheContext(string cache)
    {
        public string Cache { get; } = cache;
        public string PortFile => Path.Combine(Cache, "port");
        public string StatusFile => Path.Combine(Cache, "status");
        public string CaptureFile => Path.Combine(Cache, "captures.log");
        public string PfxPath => Path.Combine(Cache, "rootCert.pfx");
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    private static ProxyServer CreateProxy(CacheContext ctx)
    {
        var proxy = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        proxy.CertificateManager.PfxFilePath = ctx.PfxPath;
        proxy.CertificateManager.CertificateStorage = new DefaultCertificateDiskCache();
        return proxy;
    }

    private static void EnsureRoot(ProxyServer proxy, CacheContext ctx)
    {
        if (File.Exists(ctx.PfxPath))
            proxy.CertificateManager.LoadRootCertificate(ctx.PfxPath, "", overwritePfXFile: false);
        if (proxy.CertificateManager.RootCertificate is null)
            proxy.CertificateManager.CreateRootCertificate(persistToFile: true);

        var cert = proxy.CertificateManager.RootCertificate
                   ?? throw new InvalidOperationException("No root certificate");
        File.WriteAllText(Path.Combine(ctx.Cache, "sha1"), cert.GetCertHashString());
        File.WriteAllBytes(Path.Combine(ctx.Cache, "root.cer"), cert.Export(X509ContentType.Cert));
        Log($"Root CN={cert.GetNameInfo(X509NameType.SimpleName, false)} SHA1={cert.GetCertHashString()}");
    }

    private static void Clean(CacheContext ctx, ProbeLog log)
    {
        Log("Removing Titanium roots (login + System). Approve admin password if prompted…");
        using var proxy = CreateProxy(ctx);
        EnsureRoot(proxy, ctx);
        var ok = proxy.CertificateManager.RemoveTrustedRootCertificateAsAdmin(machineTrusted: true);
        Log($"RemoveTrustedRootCertificateAsAdmin(machine=true): {ok}");
        proxy.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
        PrintStatus(ctx, log);
        File.WriteAllText(ctx.StatusFile, "cleaned");
        log.Step("machine-trust-clean", ok, ok ? "cleaned" : "remove returned false");
    }

    private static void InstallSystem(CacheContext ctx, ProbeLog log)
    {
        Log("Installing root into System.keychain via TrustRootCertificateAsAdmin(machine=true).");
        Log("Approve the admin password prompt…");
        using var proxy = CreateProxy(ctx);
        EnsureRoot(proxy, ctx);
        var ok = proxy.CertificateManager.TrustRootCertificateAsAdmin(machineTrusted: true);
        Log($"TrustRootCertificateAsAdmin(machine=true): {ok}");
        Log($"LastOsTrust: {proxy.CertificateManager.LastOsTrustResult}");
        File.WriteAllText(ctx.StatusFile, ok ? "installed-system" : "install-failed");
        PrintStatus(ctx, log);
        log.Step("machine-trust-install", ok, ok ? "installed" : "install-failed");
    }

    private static void RemoveSystem(CacheContext ctx, ProbeLog log)
    {
        Log("Removing system + user trust. Approve admin password if prompted…");
        using var proxy = CreateProxy(ctx);
        EnsureRoot(proxy, ctx);
        var ok = proxy.CertificateManager.RemoveTrustedRootCertificateAsAdmin(machineTrusted: true);
        Log($"RemoveTrustedRootCertificateAsAdmin(machine=true): {ok}");
        proxy.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
        File.WriteAllText(ctx.StatusFile, "removed");
        PrintStatus(ctx, log);
        log.Step("machine-trust-remove", ok, ok ? "removed" : "remove returned false");
    }

    private static void PrintStatus(CacheContext ctx, ProbeLog log)
    {
        Log("=== security find-certificate Titanium ===");
        RunProcess("security", "find-certificate -a -c Titanium -Z");
        Log("=== System.keychain ===");
        RunProcess("security", "find-certificate -a -c Titanium -Z /Library/Keychains/System.keychain");
        Log("=== dump-trust-settings -d ===");
        RunProcess("security", "dump-trust-settings -d");
        log.Info($"Cache: {ctx.Cache}");
        if (File.Exists(ctx.StatusFile))
            log.Info($"status file: {File.ReadAllText(ctx.StatusFile).Trim()}");
    }

    private static void RunProcess(string file, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var text = (stdout + stderr).Trim();
            Console.WriteLine(string.IsNullOrEmpty(text) ? "(empty)" : text);
        }
        catch (Exception ex)
        {
            Log("run failed: " + ex.Message);
        }
    }

    private static void CurlCheck(CacheContext ctx, ProbeLog log)
    {
        if (!File.Exists(ctx.PortFile))
        {
            Log("No port file — start with: machine-trust run");
            log.Step("machine-trust-curl-check", false, "no port file");
            return;
        }

        var port = File.ReadAllText(ctx.PortFile).Trim();
        Log($"curl via proxy 127.0.0.1:{port} (no -k) → expects success only if System CA trusted");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "curl",
            Arguments =
                $"-sS -o /dev/null -w \"%{{http_code}} cert:%{{ssl_verify_result}}\\n\" --proxy http://127.0.0.1:{port} --max-time 20 https://example.com/",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        Console.WriteLine(p.StandardOutput.ReadToEnd());
        Console.WriteLine(p.StandardError.ReadToEnd());
        p.WaitForExit();
        Log($"curl exit={p.ExitCode}");
        log.Step("machine-trust-curl-check", p.ExitCode == 0, $"exit={p.ExitCode}");
    }

    private static async Task RunProxyAsync(CacheContext ctx, string[] args, ProbeLog log)
    {
        var setSystemProxy = !args.Contains("--no-system-proxy", StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(ctx.CaptureFile, "");
        using var proxy = CreateProxy(ctx);
        EnsureRoot(proxy, ctx);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(ctx.Cache, "exceptions.log"),
                    $"[{DateTime.Now:HH:mm:ss}] UNHANDLED {e.ExceptionObject}\n");
            }
            catch
            {
                /* ignore */
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(ctx.Cache, "exceptions.log"),
                    $"[{DateTime.Now:HH:mm:ss}] UNOBSERVED {e.Exception}\n");
                e.SetObserved();
            }
            catch
            {
                /* ignore */
            }
        };

        proxy.Logging = new Titanium.Web.Proxy.Logging.ProxyLoggingOptions
        {
            Enabled = true,
            EnableConsole = true,
            EnableFile = true,
            MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Information,
            FilePath = Path.Combine(ctx.Cache, "titanium-proxy.log"),
        };
        proxy.ApplyLoggingConfiguration();

        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
        proxy.AddEndPoint(ep);
        proxy.BeforeRequest += async (_, e) =>
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss} {e.HttpClient.Request.Method} {e.HttpClient.Request.Url}";
                await File.AppendAllTextAsync(ctx.CaptureFile, line + Environment.NewLine).ConfigureAwait(false);
                Log("CAPTURE " + line);
            }
            catch (Exception ex)
            {
                Log("CAPTURE_ERR " + ex.Message);
            }
        };

        proxy.Start(changeSystemProxySettings: false);
        File.WriteAllText(ctx.PortFile, ep.Port.ToString());
        Log($"Proxy 127.0.0.1:{ep.Port} decrypt=true");

        if (setSystemProxy)
        {
            try
            {
                proxy.SetAsSystemProxy(ep, ProxyProtocolType.AllHttp);
                Log("SetAsSystemProxy: ok");
            }
            catch (Exception ex)
            {
                Log("SetAsSystemProxy failed: " + ex.Message);
            }
        }
        else
        {
            Log("Skipping SetAsSystemProxy (--no-system-proxy)");
        }

        File.WriteAllText(ctx.StatusFile, $"running:{ep.Port}");
        var stopFile = Path.Combine(ctx.Cache, "stop");
        TryDelete(stopFile);
        Log($"Running. Create {stopFile} to stop…");
        log.Step("machine-trust-run-start", true, $"port={ep.Port}");

        while (!File.Exists(stopFile))
            await Task.Delay(250).ConfigureAwait(false);

        if (setSystemProxy)
        {
            try
            {
                proxy.RestoreOriginalProxySettings();
            }
            catch (Exception ex)
            {
                Log("Restore proxy: " + ex.Message);
            }
        }

        proxy.Stop();
        File.WriteAllText(ctx.StatusFile, "stopped");
        Log("Stopped.");
        log.Step("machine-trust-run-stop", true, "stopped");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }
}
