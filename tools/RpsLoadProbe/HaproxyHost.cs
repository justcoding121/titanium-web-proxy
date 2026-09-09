using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Starts open-source HAProxy (GPL) as a native reverse peer against the shared managed origin.
/// Uses a temp prefix so the user's install tree is never modified.
/// Not available on Windows (no official port) — <see cref="ResolveHaproxyExecutable"/> returns null.
/// </summary>
internal sealed class HaproxyHost : IDisposable
{
    private readonly Process process;
    private readonly string prefixDir;

    public int Port { get; }
    public string ListenUrl { get; }
    public string Version { get; }

    private HaproxyHost(Process process, string prefixDir, int port, string listenUrl, string version)
    {
        this.process = process;
        this.prefixDir = prefixDir;
        Port = port;
        ListenUrl = listenUrl;
        Version = version;
    }

    public static Task<HaproxyHost?> TryStartHttp1Async(int originHttpPort, string? haproxyPath) =>
        TryStartAsync(BuildHttp1Conf(originHttpPort), listenScheme: "http", haproxyPath);

    public static async Task<HaproxyHost?> TryStartHttp1TlsAsync(int originHttpPort, string? haproxyPath)
    {
        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp1TlsConf(originHttpPort, pem), listenScheme: "https", haproxyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<HaproxyHost?> TryStartHttp2Async(int originHttpPort, string? haproxyPath)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        if (exe == null)
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp2Conf(originHttpPort, pem), listenScheme: "https", haproxyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    /// <summary>
    /// Client QUIC/h3 → cleartext HTTP/1. Returns null when HAProxy lacks <c>USE_QUIC</c>.
    /// </summary>
    public static async Task<HaproxyHost?> TryStartHttp3CleartextAsync(int originHttpPort, string? haproxyPath)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        if (exe == null || !SupportsQuic(exe))
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp3CleartextConf(originHttpPort, pem), listenScheme: "https",
                haproxyPath, listenHost: "localhost", requireUdp: true);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static Task<HaproxyHost?> TryStartHttp1ToHttpsAsync(int originHttpsPort, string? haproxyPath) =>
        TryStartAsync(BuildHttp1ToHttpsConf(originHttpsPort), listenScheme: "http", haproxyPath);

    public static async Task<HaproxyHost?> TryStartHttp1TlsToHttpsAsync(int originHttpsPort, string? haproxyPath)
    {
        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp1TlsToHttpsConf(originHttpsPort, pem), listenScheme: "https",
                haproxyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<HaproxyHost?> TryStartHttp2ToHttpsHttp1Async(int originHttpsPort, string? haproxyPath)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        if (exe == null)
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp2ToHttpsHttp1Conf(originHttpsPort, pem), listenScheme: "https",
                haproxyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<HaproxyHost?> TryStartHttp3ToHttpsHttp1Async(int originHttpsPort, string? haproxyPath)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        if (exe == null || !SupportsQuic(exe))
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var pem = await ExportLoopbackCombinedPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp3ToHttpsHttp1Conf(originHttpsPort, pem), listenScheme: "https",
                haproxyPath, listenHost: "localhost", requireUdp: true);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static bool IsQuicCapable(string? haproxyPath)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        return exe != null && SupportsQuic(exe);
    }

    private static async Task<HaproxyHost?> TryStartAsync(Func<string, int, string> confBuilder, string listenScheme,
        string? haproxyPath, string listenHost = "127.0.0.1", bool requireUdp = false)
    {
        var exe = ResolveHaproxyExecutable(haproxyPath);
        if (exe == null)
            return null;

        var version = ReadVersion(exe);
        var port = requireUdp ? GetFreeDualStackPort() : GetFreeTcpPort();
        var prefixDir = Path.Combine(Path.GetTempPath(), "twp-rps-haproxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixDir);
        Directory.CreateDirectory(Path.Combine(prefixDir, "certs"));

        var confPath = Path.GetFullPath(Path.Combine(prefixDir, "haproxy.cfg"));
        var conf = confBuilder(prefixDir, port);
        await File.WriteAllTextAsync(confPath, conf, Encoding.ASCII);

        await ValidateConfigAsync(exe, confPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            // -db: foreground, disable background mode (daemon).
            Arguments = $"-f \"{confPath}\" -db",
            WorkingDirectory = prefixDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start haproxy.");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen(port))
                return new HaproxyHost(process, prefixDir, port, $"{listenScheme}://{listenHost}:{port}/", version);
            if (process.HasExited)
            {
                await Task.Delay(100);
                var err = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                TryDeleteDir(prefixDir);
                throw new InvalidOperationException(
                    $"haproxy exited early (code {process.ExitCode}, config: {confPath}). stderr: {err} stdout: {stdout}");
            }

            Thread.Sleep(50);
        }

        TryStop(process);
        TryDeleteDir(prefixDir);
        throw new TimeoutException($"haproxy did not open port {port} in time.");
    }

    private static string GlobalDefaults() => $"""
global
    nbthread {Environment.ProcessorCount}
    maxconn 4096

defaults
    mode http
    option http-keep-alive
    timeout connect 5s
    timeout client 65s
    timeout server 65s
    timeout http-keep-alive 65s
    maxconn 4096
""";

    private static Func<string, int, string> BuildHttp1Conf(int originHttpPort) => (_, port) => $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port}
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpPort} maxconn 256
""";

    private static Func<string, int, string> BuildHttp1TlsConf(int originHttpPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn http/1.1
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpPort} maxconn 256
""";
        };

    private static Func<string, int, string> BuildHttp2Conf(int originHttpPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn h2,http/1.1
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpPort} maxconn 256
""";
        };

    private static Func<string, int, string> BuildHttp3CleartextConf(int originHttpPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            // Dual bind: TCP TLS for readiness probe + QUIC for H3 clients (matches nginx pattern).
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn h2,http/1.1
    bind quic4@127.0.0.1:{port} ssl crt "{pemDest}" alpn h3
    bind quic6@[::1]:{port} ssl crt "{pemDest}" alpn h3
    http-response set-header alt-svc 'h3=":{port}"; ma=86400'
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpPort} maxconn 256
""";
        };

    private static Func<string, int, string> BuildHttp1ToHttpsConf(int originHttpsPort) =>
        (_, port) => $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port}
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpsPort} ssl verify none sni str(localhost) maxconn 256
""";

    private static Func<string, int, string> BuildHttp1TlsToHttpsConf(int originHttpsPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn http/1.1
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpsPort} ssl verify none sni str(localhost) maxconn 256
""";
        };

    private static Func<string, int, string> BuildHttp2ToHttpsHttp1Conf(int originHttpsPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn h2,http/1.1
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpsPort} ssl verify none sni str(localhost) maxconn 256
""";
        };

    private static Func<string, int, string> BuildHttp3ToHttpsHttp1Conf(int originHttpsPort, string pemPath) =>
        (prefixDir, port) =>
        {
            var pemDest = CopyPem(prefixDir, pemPath);
            return $"""
{GlobalDefaults()}

frontend fe
    bind 127.0.0.1:{port} ssl crt "{pemDest}" alpn h2,http/1.1
    bind quic4@127.0.0.1:{port} ssl crt "{pemDest}" alpn h3
    bind quic6@[::1]:{port} ssl crt "{pemDest}" alpn h3
    http-response set-header alt-svc 'h3=":{port}"; ma=86400'
    default_backend be

backend be
    http-reuse aggressive
    server origin 127.0.0.1:{originHttpsPort} ssl verify none sni str(localhost) maxconn 256
""";
        };

    private static async Task ValidateConfigAsync(string exe, string confPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-c -f \"{confPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
                              ?? throw new InvalidOperationException("Failed to start haproxy config check.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"haproxy config check failed (exit {process.ExitCode}) for '{confPath}': stderr: {stderr} stdout: {stdout}");
        }
    }

    private static string CopyPem(string prefixDir, string pemPath)
    {
        var dest = Path.Combine(prefixDir, "certs", "server.pem");
        File.Copy(pemPath, dest, overwrite: true);
        return dest.Replace('\\', '/');
    }

    private static readonly Regex QuicConfigureFlag =
        new(@"\bUSE_QUIC\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <c>haproxy -vv</c> lists <c>USE_QUIC</c> as a configure flag.</summary>
    internal static bool SupportsQuic(string exe)
    {
        var text = ReadHaproxyOutput(exe, "-vv");
        return QuicConfigureFlag.IsMatch(text);
    }

    private static async Task<string> ExportLoopbackCombinedPemAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        using var cert = LoopbackCertificateAuthority.ServerCertificate;
        var pemPath = Path.Combine(dir, "server.pem");

        var certPem = PemEncoding.Write("CERTIFICATE", cert.RawData);
        using var rsa = cert.GetRSAPrivateKey();
        using var ecdsa = cert.GetECDsaPrivateKey();
        string keyPem;
        if (rsa != null)
            keyPem = new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
        else if (ecdsa != null)
            keyPem = new string(PemEncoding.Write("PRIVATE KEY", ecdsa.ExportPkcs8PrivateKey()));
        else
            throw new InvalidOperationException("Loopback server certificate has no exportable private key.");

        await File.WriteAllTextAsync(pemPath, new string(certPem) + "\n" + keyPem);
        return pemPath;
    }

    public void Dispose()
    {
        TryStop(process);
        TryDeleteDir(prefixDir);
    }

    /// <summary>
    /// Resolves HAProxy on PATH. Always returns null on Windows (no official port).
    /// </summary>
    public static string? ResolveHaproxyExecutable(string? haproxyPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        if (!string.IsNullOrWhiteSpace(haproxyPath))
        {
            if (File.Exists(haproxyPath))
                return Path.GetFullPath(haproxyPath);
            return null;
        }

        return FindOnPath("haproxy");
    }

    public static string HaproxyMissingMessage() =>
        """
        haproxy was not found on PATH (and no --haproxy-path was given), or this OS is Windows.
        TWP arms will still run. To enable the same-machine HAProxy control arm:
          Linux:   sudo apt-get install -y haproxy   (Community/GPL; stop the distro service first)
          macOS:   brew install haproxy
          Windows: not supported (no official HAProxy Windows port) — cells are Not possible.
        Then re-run with haproxy on PATH, or pass --haproxy-path <path-to-haproxy>.
        HTTP/3 terminate needs a build with USE_QUIC (typical distro/Homebrew builds often lack it).
        """;

    private static string ReadVersion(string exe)
    {
        var text = ReadHaproxyOutput(exe, "-v");
        // First line is usually "HAProxy version 2.x.y-... ..."
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;
        return line.Trim();
    }

    private static string ReadHaproxyOutput(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) return "unknown";
            var err = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(stdout) ? err : stdout;
            if (!string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(err))
                text = stdout + " " + err;
            return text.Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryStop(Process process)
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
            // best effort
        }

        process.Dispose();
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(IPAddress.Loopback, port, null, null);
            var ok = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            if (!ok) return false;
            client.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int GetFreeDualStackPort()
    {
        for (var i = 0; i < 20; i++)
        {
            var port = GetFreeTcpPort();
            if (IsUdpPortFree(port))
                return port;
        }

        return GetFreeTcpPort();
    }

    private static bool IsUdpPortFree(int port)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp leftovers are fine
        }
    }
}
