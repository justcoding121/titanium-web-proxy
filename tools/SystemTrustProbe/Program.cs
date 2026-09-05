using System.Net;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

var cmd = args.ElementAtOrDefault(0) ?? "help";
var cache = Path.Combine(Path.GetTempPath(), "ti-system-trust-probe");
Directory.CreateDirectory(cache);
var portFile = Path.Combine(cache, "port");
var statusFile = Path.Combine(cache, "status");
var captureFile = Path.Combine(cache, "captures.log");
var pfxPath = Path.Combine(cache, "rootCert.pfx");

CertificateManager.SuppressInteractiveRootStoreMutations = false;

switch (cmd)
{
    case "clean":
        Clean();
        break;
    case "install-system":
        InstallSystem();
        break;
    case "remove-system":
        RemoveSystem();
        break;
    case "run":
        try
        {
            await RunProxyAsync();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(cache, "exceptions.log"),
                    $"[{DateTime.Now:HH:mm:ss}] RUN_FATAL {ex}\n");
            }
            catch { /* ignore */ }
            Log("RUN_FATAL " + ex);
            Environment.ExitCode = 1;
        }
        break;
    case "status":
        PrintStatus();
        break;
    case "curl-check":
        CurlCheck();
        break;
    default:
        Console.WriteLine(
            "Usage: clean | install-system | remove-system | run | status | curl-check");
        break;
}

ProxyServer CreateProxy()
{
    var proxy = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false);
    proxy.CertificateManager.PfxFilePath = pfxPath;
    proxy.CertificateManager.CertificateStorage = new DefaultCertificateDiskCache();
    return proxy;
}

void EnsureRoot(ProxyServer proxy)
{
    if (File.Exists(pfxPath))
        proxy.CertificateManager.LoadRootCertificate(pfxPath, "", overwritePfXFile: false);
    if (proxy.CertificateManager.RootCertificate is null)
        proxy.CertificateManager.CreateRootCertificate(persistToFile: true);

    var cert = proxy.CertificateManager.RootCertificate
               ?? throw new InvalidOperationException("No root certificate");
    File.WriteAllText(Path.Combine(cache, "sha1"), cert.GetCertHashString());
    File.WriteAllBytes(Path.Combine(cache, "root.cer"), cert.Export(X509ContentType.Cert));
    Log($"Root CN={cert.GetNameInfo(X509NameType.SimpleName, false)} SHA1={cert.GetCertHashString()}");
}

void Clean()
{
    Log("Removing Titanium roots (login + System). Approve admin password if prompted…");
    using var proxy = CreateProxy();
    EnsureRoot(proxy);
    var ok = proxy.CertificateManager.RemoveTrustedRootCertificateAsAdmin(machineTrusted: true);
    Log($"RemoveTrustedRootCertificateAsAdmin(machine=true): {ok}");
    // Also thorough user path (covers orphans / System leftovers)
    proxy.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
    PrintStatus();
    File.WriteAllText(statusFile, "cleaned");
}

void InstallSystem()
{
    Log("Installing root into System.keychain via TrustRootCertificateAsAdmin(machine=true).");
    Log("Approve the admin password prompt…");
    using var proxy = CreateProxy();
    EnsureRoot(proxy);
    var ok = proxy.CertificateManager.TrustRootCertificateAsAdmin(machineTrusted: true);
    Log($"TrustRootCertificateAsAdmin(machine=true): {ok}");
    Log($"LastOsTrust: {proxy.CertificateManager.LastOsTrustResult}");
    File.WriteAllText(statusFile, ok ? "installed-system" : "install-failed");
    PrintStatus();
}

void RemoveSystem()
{
    Log("Removing system + user trust. Approve admin password if prompted…");
    using var proxy = CreateProxy();
    EnsureRoot(proxy);
    var ok = proxy.CertificateManager.RemoveTrustedRootCertificateAsAdmin(machineTrusted: true);
    Log($"RemoveTrustedRootCertificateAsAdmin(machine=true): {ok}");
    proxy.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
    File.WriteAllText(statusFile, "removed");
    PrintStatus();
}

void PrintStatus()
{
    Log("=== security find-certificate Titanium ===");
    Run("security", "find-certificate -a -c Titanium -Z");
    Log("=== System.keychain ===");
    Run("security", "find-certificate -a -c Titanium -Z /Library/Keychains/System.keychain");
    Log("=== dump-trust-settings -d ===");
    Run("security", "dump-trust-settings -d");
}

void Run(string file, string args)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
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

void CurlCheck()
{
    if (!File.Exists(portFile))
    {
        Log("No port file — start with: run");
        return;
    }

    var port = File.ReadAllText(portFile).Trim();
    Log($"curl via proxy 127.0.0.1:{port} (no -k) → expects success only if System CA trusted");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "curl",
        Arguments = $"-sS -o /dev/null -w \"%{{http_code}} cert:%{{ssl_verify_result}}\\n\" --proxy http://127.0.0.1:{port} --max-time 20 https://example.com/",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    Console.WriteLine(p.StandardOutput.ReadToEnd());
    Console.WriteLine(p.StandardError.ReadToEnd());
    p.WaitForExit();
    Log($"curl exit={p.ExitCode}");
}

async Task RunProxyAsync()
{
    var setSystemProxy = !args.Contains("--no-system-proxy", StringComparer.OrdinalIgnoreCase);
    File.WriteAllText(captureFile, "");
    using var proxy = CreateProxy();
    EnsureRoot(proxy);

    // Surface crashes into exceptions.log (LoggingOptions API varies by build — keep simple).
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        try
        {
            File.AppendAllText(Path.Combine(cache, "exceptions.log"),
                $"[{DateTime.Now:HH:mm:ss}] UNHANDLED {e.ExceptionObject}\n");
        }
        catch { /* ignore */ }
    };
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        try
        {
            File.AppendAllText(Path.Combine(cache, "exceptions.log"),
                $"[{DateTime.Now:HH:mm:ss}] UNOBSERVED {e.Exception}\n");
            e.SetObserved();
        }
        catch { /* ignore */ }
    };

    proxy.Logging = new Titanium.Web.Proxy.Logging.ProxyLoggingOptions
    {
        Enabled = true,
        EnableConsole = true,
        EnableFile = true,
        MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Information,
        FilePath = Path.Combine(cache, "titanium-proxy.log"),
    };
    proxy.ApplyLoggingConfiguration();

    var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
    proxy.AddEndPoint(ep);
    proxy.BeforeRequest += async (_, e) =>
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss} {e.HttpClient.Request.Method} {e.HttpClient.Request.Url}";
            await File.AppendAllTextAsync(captureFile, line + Environment.NewLine);
            Log("CAPTURE " + line);
        }
        catch (Exception ex)
        {
            Log("CAPTURE_ERR " + ex.Message);
        }
    };

    proxy.Start(changeSystemProxySettings: false);
    File.WriteAllText(portFile, ep.Port.ToString());
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

    File.WriteAllText(statusFile, $"running:{ep.Port}");
    var stopFile = Path.Combine(cache, "stop");
    TryDelete(stopFile);
    Log($"Running. Create {stopFile} to stop…");
    while (!File.Exists(stopFile))
        await Task.Delay(250);

    if (setSystemProxy)
    {
        try { proxy.RestoreOriginalProxySettings(); }
        catch (Exception ex) { Log("Restore proxy: " + ex.Message); }
    }

    proxy.Stop();
    File.WriteAllText(statusFile, "stopped");
    Log("Stopped.");
}

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
}
