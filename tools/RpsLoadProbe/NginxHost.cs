using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Starts a stock-ish native reverse peer against the shared managed origin server.
/// Uses a temp prefix directory so the user's install tree is never modified.
/// </summary>
internal sealed class NginxHost : IDisposable
{
    private readonly Process process;
    private readonly string prefixDir;

    public int Port { get; }
    public string ListenUrl { get; }
    public string Version { get; }

    private NginxHost(Process process, string prefixDir, int port, string listenUrl, string version)
    {
        this.process = process;
        this.prefixDir = prefixDir;
        Port = port;
        ListenUrl = listenUrl;
        Version = version;
    }

    public static Task<NginxHost?> TryStartHttp1Async(int originHttpPort, string? nginxPath) =>
        TryStartAsync(BuildHttp1Conf(originHttpPort), listenScheme: "http", nginxPath);

    public static async Task<NginxHost?> TryStartHttp1TlsAsync(int originHttpPort, string? nginxPath)
    {
        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-nginx-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp1TlsConf(originHttpPort, certPem, keyPem), listenScheme: "https",
                nginxPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<NginxHost?> TryStartHttp2Async(int originHttpPort, string? nginxPath)
    {
        // Windows build has http_v2 + ssl but no QUIC/UDP. Client TLS+h2 → cleartext HTTP origin.
        var exe = ResolveNginxExecutable(nginxPath);
        if (exe == null)
            return null;

        var version = ReadVersion(exe);
        // 1.25.1+ uses `http2 on;`; Ubuntu 24.04 ships 1.24 which needs `listen ... ssl http2`.
        var useHttp2OnDirective = SupportsHttp2OnDirective(version);

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-nginx-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp2Conf(originHttpPort, certPem, keyPem, useHttp2OnDirective),
                listenScheme: "https", nginxPath);
        }
        finally
        {
            // conf generator copies paths into the real prefix; probe dir only needed for export helpers
            TryDeleteDir(prefixProbe);
        }
    }

    /// <summary>
    /// Client QUIC/h3 (plus TCP TLS for readiness) → cleartext HTTP/1 origin.
    /// Returns <see langword="null"/> when nginx is missing or was not built with <c>http_v3_module</c>
    /// (Ubuntu 24.04 distro nginx 1.24; nginx/Windows). Official nginx.org mainline packages include it.
    /// </summary>
    public static async Task<NginxHost?> TryStartHttp3CleartextAsync(int originHttpPort, string? nginxPath)
    {
        var exe = ResolveNginxExecutable(nginxPath);
        if (exe == null)
            return null;
        if (!SupportsHttp3Module(ReadConfigureArguments(exe)))
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-nginx-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp3CleartextConf(originHttpPort, certPem, keyPem),
                listenScheme: "https", nginxPath, listenHost: "localhost", requireUdp: true);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    /// <summary>True when <paramref name="nginxPath"/> (or PATH) resolves to a binary built with HTTP/3.</summary>
    public static bool IsHttp3Capable(string? nginxPath)
    {
        var exe = ResolveNginxExecutable(nginxPath);
        return exe != null && SupportsHttp3Module(ReadConfigureArguments(exe));
    }

    private static async Task<NginxHost?> TryStartAsync(Func<string, int, string> confBuilder, string listenScheme,
        string? nginxPath, string listenHost = "127.0.0.1", bool requireUdp = false)
    {
        var exe = ResolveNginxExecutable(nginxPath);
        if (exe == null)
            return null;

        var version = ReadVersion(exe);
        var port = requireUdp ? GetFreeDualStackPort() : GetFreeTcpPort();
        var prefixDir = Path.Combine(Path.GetTempPath(), "twp-rps-nginx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixDir);
        Directory.CreateDirectory(Path.Combine(prefixDir, "logs"));
        Directory.CreateDirectory(Path.Combine(prefixDir, "temp"));
        Directory.CreateDirectory(Path.Combine(prefixDir, "conf"));
        Directory.CreateDirectory(Path.Combine(prefixDir, "certs"));

        var confPath = Path.Combine(prefixDir, "conf", "nginx.conf");
        var conf = confBuilder(prefixDir, port);
        await File.WriteAllTextAsync(confPath, conf, Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-p \"{prefixDir}\" -c conf/nginx.conf",
            WorkingDirectory = prefixDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start nginx.");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen(port))
                return new NginxHost(process, prefixDir, port, $"{listenScheme}://{listenHost}:{port}/", version);
            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync();
                TryDeleteDir(prefixDir);
                throw new InvalidOperationException(
                    $"nginx exited early (code {process.ExitCode}). stderr: {err}");
            }

            Thread.Sleep(50);
        }

        TryStop(process, exe, prefixDir);
        TryDeleteDir(prefixDir);
        throw new TimeoutException($"nginx did not open port {port} in time.");
    }

    private static Func<string, int, string> BuildHttp1Conf(int originHttpPort) => (_, port) => $$"""
        worker_processes auto;
        daemon off;
        error_log logs/error.log error;
        pid nginx.pid;
        events {
            worker_connections 4096;
        }
        http {
            access_log off;
            sendfile on;
            keepalive_timeout 65;
            client_max_body_size 10m;
            upstream origin {
                server 127.0.0.1:{{originHttpPort}};
                keepalive 32;
            }
            server {
                listen 127.0.0.1:{{port}};
                location / {
                    proxy_http_version 1.1;
                    proxy_set_header Connection "";
                    proxy_set_header Host $host;
                    proxy_pass http://origin;
                }
            }
        }
        """;

    private static Func<string, int, string> BuildHttp1TlsConf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var certDest = Path.Combine(prefixDir, "certs", "server.crt");
            var keyDest = Path.Combine(prefixDir, "certs", "server.key");
            File.Copy(certPem, certDest, overwrite: true);
            File.Copy(keyPem, keyDest, overwrite: true);
            certDest = certDest.Replace('\\', '/');
            keyDest = keyDest.Replace('\\', '/');
            return $$"""
                worker_processes auto;
                daemon off;
                error_log logs/error.log error;
                pid nginx.pid;
                events {
                    worker_connections 4096;
                }
                http {
                    access_log off;
                    sendfile on;
                    keepalive_timeout 65;
                    client_max_body_size 10m;
                    upstream origin {
                        server 127.0.0.1:{{originHttpPort}};
                        keepalive 32;
                    }
                    server {
                        listen 127.0.0.1:{{port}} ssl;
                        ssl_certificate {{certDest}};
                        ssl_certificate_key {{keyDest}};
                        ssl_protocols TLSv1.2 TLSv1.3;
                        location / {
                            proxy_http_version 1.1;
                            proxy_set_header Connection "";
                            proxy_set_header Host $host;
                            proxy_pass http://origin;
                        }
                    }
                }
                """;
        };

    private static Func<string, int, string> BuildHttp2Conf(int originHttpPort, string certPem, string keyPem,
        bool useHttp2OnDirective) =>
        (prefixDir, port) =>
        {
            var certDest = Path.Combine(prefixDir, "certs", "server.crt");
            var keyDest = Path.Combine(prefixDir, "certs", "server.key");
            File.Copy(certPem, certDest, overwrite: true);
            File.Copy(keyPem, keyDest, overwrite: true);
            // Normalize paths for the Windows build (forward slashes).
            certDest = certDest.Replace('\\', '/');
            keyDest = keyDest.Replace('\\', '/');
            // Prefer `http2 on` when available; fall back to listen-parameter form for builds < 1.25.1.
            var listenAndHttp2 = useHttp2OnDirective
                ? $"listen 127.0.0.1:{port} ssl;\n                        http2 on;"
                : $"listen 127.0.0.1:{port} ssl http2;";
            return $$"""
                worker_processes auto;
                daemon off;
                error_log logs/error.log error;
                pid nginx.pid;
                events {
                    worker_connections 4096;
                }
                http {
                    access_log off;
                    sendfile on;
                    keepalive_timeout 65;
                    client_max_body_size 10m;
                    upstream origin {
                        server 127.0.0.1:{{originHttpPort}};
                        keepalive 32;
                    }
                    server {
                        {{listenAndHttp2}}
                        ssl_certificate {{certDest}};
                        ssl_certificate_key {{keyDest}};
                        ssl_protocols TLSv1.2 TLSv1.3;
                        location / {
                            proxy_http_version 1.1;
                            proxy_set_header Connection "";
                            proxy_set_header Host $host;
                            proxy_pass http://origin;
                        }
                    }
                }
                """;
        };

    private static Func<string, int, string> BuildHttp3CleartextConf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var certDest = Path.Combine(prefixDir, "certs", "server.crt");
            var keyDest = Path.Combine(prefixDir, "certs", "server.key");
            File.Copy(certPem, certDest, overwrite: true);
            File.Copy(keyPem, keyDest, overwrite: true);
            certDest = certDest.Replace('\\', '/');
            keyDest = keyDest.Replace('\\', '/');
            // Bind all interfaces so HttpClient "localhost" (::1 first) reaches QUIC, matching YARP ListenAnyIP.
            // Dual listen: TCP SSL for readiness; UDP QUIC for the HTTP/3 client.
            return $$"""
                worker_processes auto;
                daemon off;
                error_log logs/error.log error;
                pid nginx.pid;
                events {
                    worker_connections 4096;
                }
                http {
                    access_log off;
                    sendfile on;
                    keepalive_timeout 65;
                    client_max_body_size 10m;
                    upstream origin {
                        server 127.0.0.1:{{originHttpPort}};
                        keepalive 32;
                    }
                    server {
                        listen {{port}} ssl;
                        listen {{port}} quic reuseport;
                        http3 on;
                        ssl_certificate {{certDest}};
                        ssl_certificate_key {{keyDest}};
                        ssl_protocols TLSv1.3;
                        add_header Alt-Svc 'h3=":{{port}}"; ma=86400';
                        location / {
                            proxy_http_version 1.1;
                            proxy_set_header Connection "";
                            proxy_set_header Host $host;
                            proxy_pass http://origin;
                        }
                    }
                }
                """;
        };

    /// <summary>True when <c>nginx -V</c> configure args include <c>http_v3_module</c>.</summary>
    internal static bool SupportsHttp3Module(string configureOrVersionText) =>
        configureOrVersionText.Contains("http_v3_module", StringComparison.OrdinalIgnoreCase)
        || configureOrVersionText.Contains("http_quic_module", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the binary supports the <c>http2 on;</c> directive (1.25.1+).</summary>
    internal static bool SupportsHttp2OnDirective(string versionText)
    {
        var m = System.Text.RegularExpressions.Regex.Match(versionText, @"nginx/(\d+)\.(\d+)\.(\d+)");
        if (!m.Success)
            return true; // unknown — prefer modern syntax (Windows zips are usually current)

        var major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var patch = int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return major > 1
               || (major == 1 && minor > 25)
               || (major == 1 && minor == 25 && patch >= 1);
    }

    private static async Task<(string CertPem, string KeyPem)> ExportLoopbackPemAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        using var cert = LoopbackCertificateAuthority.ServerCertificate;
        var certPath = Path.Combine(dir, "server.crt");
        var keyPath = Path.Combine(dir, "server.key");

        var certPem = PemEncoding.Write("CERTIFICATE", cert.RawData);
        await File.WriteAllTextAsync(certPath, new string(certPem));

        // Export private key — certificate was created with exportable key.
        using var rsa = cert.GetRSAPrivateKey();
        using var ecdsa = cert.GetECDsaPrivateKey();
        if (rsa != null)
        {
            var keyPem = PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey());
            await File.WriteAllTextAsync(keyPath, new string(keyPem));
        }
        else if (ecdsa != null)
        {
            var keyPem = PemEncoding.Write("PRIVATE KEY", ecdsa.ExportPkcs8PrivateKey());
            await File.WriteAllTextAsync(keyPath, new string(keyPem));
        }
        else
        {
            throw new InvalidOperationException("Loopback server certificate has no exportable private key.");
        }

        return (certPath, keyPath);
    }

    public void Dispose()
    {
        var exe = process.StartInfo.FileName;
        TryStop(process, exe, prefixDir);
        TryDeleteDir(prefixDir);
    }

    public static string? ResolveNginxExecutable(string? nginxPath)
    {
        if (!string.IsNullOrWhiteSpace(nginxPath))
        {
            if (File.Exists(nginxPath))
                return Path.GetFullPath(nginxPath);
            return null;
        }

        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "nginx.exe", "nginx" }
            : new[] { "nginx" };

        foreach (var name in names)
        {
            var fromPath = FindOnPath(name);
            if (fromPath != null)
                return fromPath;
        }

        return null;
    }

    public static string NginxMissingMessage() =>
        """
        nginx was not found on PATH (and no --nginx-path was given).
        TWP arms will still run. To enable the same-machine nginx control arm:
          Windows: download the official Windows zip from https://nginx.org/en/docs/windows.html
                   or run: scoop install nginx   /   choco install nginx
          Linux:   install nginx.org mainline (includes --with-http_v3_module), or
                   sudo apt-get install -y nginx  (Ubuntu 24.04 is 1.24, HTTP/2 only)
        Then re-run with nginx on PATH, or pass --nginx-path <path-to-nginx[.exe]>.
        Note: nginx/Windows has HTTP/2 (ssl) but not HTTP/3/QUIC (UDP unsupported).
        HTTP/3 terminate needs a build with --with-http_v3_module (nginx.org mainline on Linux).
        """;

    private static string ReadVersion(string exe) => ReadNginxOutput(exe, "-v");

    /// <summary><c>nginx -V</c> includes configure arguments (used to detect <c>http_v3_module</c>).</summary>
    internal static string ReadConfigureArguments(string exe) => ReadNginxOutput(exe, "-V");

    private static string ReadNginxOutput(string exe, string arguments)
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
            var text = string.IsNullOrWhiteSpace(err) ? stdout : err;
            if (!string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(err))
                text = err + " " + stdout;
            return text.Trim().Replace('\n', ' ');
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryStop(Process process, string exe, string prefixDir)
    {
        try
        {
            using var quit = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-p \"{prefixDir}\" -c conf/nginx.conf -s quit",
                WorkingDirectory = prefixDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            quit?.WaitForExit(5000);
        }
        catch
        {
            // fall through to kill
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
