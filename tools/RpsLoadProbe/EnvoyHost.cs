using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Starts open-source Envoy (Apache 2.0) as a native reverse peer against the shared managed origin.
/// Uses a temp prefix so the user's install tree is never modified.
/// Not available on Windows — <see cref="ResolveEnvoyExecutable"/> returns null.
/// </summary>
internal sealed class EnvoyHost : IDisposable
{
    private readonly Process process;
    private readonly string prefixDir;

    public int Port { get; }
    public string ListenUrl { get; }
    public string Version { get; }

    private EnvoyHost(Process process, string prefixDir, int port, string listenUrl, string version)
    {
        this.process = process;
        this.prefixDir = prefixDir;
        Port = port;
        ListenUrl = listenUrl;
        Version = version;
    }

    public static Task<EnvoyHost?> TryStartHttp1Async(int originHttpPort, string? envoyPath) =>
        TryStartAsync(BuildHttp1Conf(originHttpPort), listenScheme: "http", envoyPath);

    public static async Task<EnvoyHost?> TryStartHttp1TlsAsync(int originHttpPort, string? envoyPath)
    {
        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp1TlsConf(originHttpPort, certPem, keyPem), listenScheme: "https",
                envoyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<EnvoyHost?> TryStartHttp2Async(int originHttpPort, string? envoyPath)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        if (exe == null)
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp2Conf(originHttpPort, certPem, keyPem), listenScheme: "https",
                envoyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    /// <summary>
    /// Client QUIC/h3 → cleartext HTTP/1. Returns null when Envoy lacks QUIC/HTTP/3 support.
    /// </summary>
    public static async Task<EnvoyHost?> TryStartHttp3CleartextAsync(int originHttpPort, string? envoyPath)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        if (exe == null || !SupportsHttp3(exe))
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp3CleartextConf(originHttpPort, certPem, keyPem),
                listenScheme: "https", envoyPath, listenHost: "localhost", requireUdp: true);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static Task<EnvoyHost?> TryStartHttp1ToHttpsAsync(int originHttpsPort, string? envoyPath) =>
        TryStartAsync(BuildHttp1ToHttpsConf(originHttpsPort), listenScheme: "http", envoyPath);

    public static async Task<EnvoyHost?> TryStartHttp1TlsToHttpsAsync(int originHttpsPort, string? envoyPath)
    {
        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp1TlsToHttpsConf(originHttpsPort, certPem, keyPem),
                listenScheme: "https", envoyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<EnvoyHost?> TryStartHttp2ToHttpsHttp1Async(int originHttpsPort, string? envoyPath)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        if (exe == null)
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp2ToHttpsHttp1Conf(originHttpsPort, certPem, keyPem),
                listenScheme: "https", envoyPath);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static async Task<EnvoyHost?> TryStartHttp3ToHttpsHttp1Async(int originHttpsPort, string? envoyPath)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        if (exe == null || !SupportsHttp3(exe))
            return null;

        var prefixProbe = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixProbe);
        try
        {
            var (certPem, keyPem) = await ExportLoopbackPemAsync(prefixProbe);
            return await TryStartAsync(BuildHttp3ToHttpsHttp1Conf(originHttpsPort, certPem, keyPem),
                listenScheme: "https", envoyPath, listenHost: "localhost", requireUdp: true);
        }
        finally
        {
            TryDeleteDir(prefixProbe);
        }
    }

    public static bool IsHttp3Capable(string? envoyPath)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        return exe != null && SupportsHttp3(exe);
    }

    private static async Task<EnvoyHost?> TryStartAsync(Func<string, int, string> confBuilder, string listenScheme,
        string? envoyPath, string listenHost = "127.0.0.1", bool requireUdp = false)
    {
        var exe = ResolveEnvoyExecutable(envoyPath);
        if (exe == null)
            return null;

        var version = ReadVersion(exe);
        var port = requireUdp ? GetFreeDualStackPort() : GetFreeTcpPort();
        var prefixDir = Path.Combine(Path.GetTempPath(), "twp-rps-envoy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixDir);
        Directory.CreateDirectory(Path.Combine(prefixDir, "certs"));

        var confPath = Path.GetFullPath(Path.Combine(prefixDir, "config.yaml"));
        var conf = confBuilder(prefixDir, port);
        if (!conf.EndsWith('\n'))
            conf += "\n";
        await File.WriteAllTextAsync(confPath, conf, Encoding.ASCII);

        var baseId = Random.Shared.Next(1, 1000);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments =
                $"-c {QuotePath(confPath)} --concurrency {Environment.ProcessorCount} --disable-hot-restart --base-id {baseId}",
            WorkingDirectory = prefixDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start envoy.");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen(port))
                return new EnvoyHost(process, prefixDir, port, $"{listenScheme}://{listenHost}:{port}/", version);
            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                TryDeleteDir(prefixDir);
                throw new InvalidOperationException(
                    $"envoy exited early (code {process.ExitCode}, config: {confPath}). stderr: {err} stdout: {stdout}");
            }

            Thread.Sleep(50);
        }

        TryStop(process);
        TryDeleteDir(prefixDir);
        throw new TimeoutException($"envoy did not open port {port} in time.");
    }

    private static string QuotePath(string path) => OperatingSystem.IsWindows() ? $"\"{path}\"" : path;

    /// <summary>Emits Envoy v3 bootstrap YAML with explicit indent levels (no nested raw-string concat).</summary>
    private sealed class YamlEmitter
    {
        private readonly StringBuilder sb = new();

        private void W(int col, string line) => sb.Append(' ', col).AppendLine(line);

        public void Admin(int adminPort)
        {
            W(0, "admin:");
            W(2, "address:");
            W(4, "socket_address:");
            W(6, "address: 127.0.0.1");
            W(6, $"port_value: {adminPort}");
        }

        public void StaticResourcesHeader() => W(0, "static_resources:");

        public void TcpListener(string name, int port, string statPrefix, string codecType, string? altSvc,
            string? certPath, string? keyPath, string[]? alpnProtocols)
        {
            W(2, "listeners:");
            W(2, $"- name: {name}");
            W(4, "address:");
            W(6, "socket_address:");
            W(8, "address: 127.0.0.1");
            W(8, $"port_value: {port}");
            W(4, "filter_chains:");
            if (certPath != null && keyPath != null && alpnProtocols != null)
            {
                // Under `- key:`, nested map fields must indent past the key (not sit as FilterChain siblings).
                W(4, "- transport_socket:");
                W(8, "name: envoy.transport_sockets.tls");
                W(8, "typed_config:");
                W(10,
                    "\"@type\": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.DownstreamTlsContext");
                W(10, "common_tls_context:");
                W(12, "tls_certificates:");
                W(12, "- certificate_chain:");
                W(16, $"filename: \"{certPath}\"");
                W(14, "private_key:");
                W(16, $"filename: \"{keyPath}\"");
                W(12, $"alpn_protocols: [{string.Join(", ", alpnProtocols.Select(a => $"\"{a}\""))}]");
                // Sibling of transport_socket inside the filter_chain list item.
                W(6, "filters:");
                AppendHttpConnectionManager(8, statPrefix, codecType, altSvc);
            }
            else
            {
                W(4, "- filters:");
                AppendHttpConnectionManager(6, statPrefix, codecType, altSvc);
            }
        }

        public void QuicListener(string name, int port, string address, string statPrefix, string certPath,
            string keyPath)
        {
            // Official downstream HTTP/3 shape: UDP + udp_listener_config.quic_options (not a
            // listener filter). See configs/envoyproxy_io_proxy_http3_downstream.yaml.
            W(2, $"- name: {name}");
            W(4, "address:");
            W(6, "socket_address:");
            W(8, "protocol: UDP");
            W(8, $"address: {address}");
            W(8, $"port_value: {port}");
            W(4, "udp_listener_config:");
            W(6, "quic_options: {}");
            W(6, "downstream_socket_config:");
            W(8, "prefer_gro: true");
            W(4, "filter_chains:");
            W(4, "- transport_socket:");
            W(8, "name: envoy.transport_sockets.quic");
            W(8, "typed_config:");
            W(10, "\"@type\": type.googleapis.com/envoy.extensions.transport_sockets.quic.v3.QuicDownstreamTransport");
            W(10, "downstream_tls_context:");
            W(12, "common_tls_context:");
            W(14, "alpn_protocols: [\"h3\"]");
            W(14, "tls_certificates:");
            W(14, "- certificate_chain:");
            W(18, $"filename: \"{certPath}\"");
            W(16, "private_key:");
            W(18, $"filename: \"{keyPath}\"");
            W(6, "filters:");
            AppendHttpConnectionManager(8, statPrefix, "HTTP3", altSvc: null);
        }

        private void AppendHttpConnectionManager(int col, string statPrefix, string codecType, string? altSvc)
        {
            W(col, "- name: envoy.filters.network.http_connection_manager");
            W(col + 2, "typed_config:");
            W(col + 4,
                "\"@type\": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager");
            W(col + 4, $"stat_prefix: {statPrefix}");
            W(col + 4, $"codec_type: {codecType}");
            if (string.Equals(codecType, "HTTP3", StringComparison.Ordinal))
                W(col + 4, "http3_protocol_options: {}");
            W(col + 4, "access_log: []");
            W(col + 4, "stream_idle_timeout: 65s");
            W(col + 4, "request_timeout: 65s");
            W(col + 4, "common_http_protocol_options:");
            W(col + 6, "idle_timeout: 65s");
            W(col + 4, "route_config:");
            W(col + 6, "name: local_route");
            W(col + 6, "virtual_hosts:");
            // List item is a map: name + domains + routes are siblings (same indent as "name").
            W(col + 6, "- name: local");
            W(col + 8, "domains: [\"*\"]");
            W(col + 8, "routes:");
            W(col + 8, "- match:");
            W(col + 12, "prefix: \"/\"");
            W(col + 10, "route:");
            W(col + 12, "cluster: origin");
            W(col + 12, "timeout: 65s");
            if (altSvc != null)
            {
                W(col + 12, "response_headers_to_add:");
                W(col + 12, "- header:");
                W(col + 16, "key: alt-svc");
                W(col + 16, $"value: \"{altSvc}\"");
            }

            W(col + 4, "http_filters:");
            W(col + 4, "- name: envoy.filters.http.router");
            W(col + 6, "typed_config:");
            W(col + 8, "\"@type\": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router");
        }

        public void Cluster(int originPort, bool upstreamTls)
        {
            W(2, "clusters:");
            W(2, "- name: origin");
            W(4, "connect_timeout: 5s");
            W(4, "type: STATIC");
            W(4, "lb_policy: ROUND_ROBIN");
            W(4, "circuit_breakers:");
            W(6, "thresholds:");
            W(6, "- priority: DEFAULT");
            W(8, "max_connections: 256");
            W(8, "max_pending_requests: 256");
            W(4, "load_assignment:");
            W(6, "cluster_name: origin");
            W(6, "endpoints:");
            W(6, "- lb_endpoints:");
            W(8, "- endpoint:");
            W(12, "address:");
            W(14, "socket_address:");
            W(16, "address: 127.0.0.1");
            W(16, $"port_value: {originPort}");
            if (upstreamTls)
            {
                W(4, "transport_socket:");
                W(6, "name: envoy.transport_sockets.tls");
                W(6, "typed_config:");
                W(8, "\"@type\": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.UpstreamTlsContext");
                W(8, "sni: localhost");
                W(8, "common_tls_context:");
                W(10, "validation_context:");
                W(12, "trust_chain_verification: ACCEPT_UNTRUSTED");
                W(4, "typed_extension_protocol_options:");
                W(6, "envoy.extensions.upstreams.http.v3.HttpProtocolOptions:");
                W(8, "\"@type\": type.googleapis.com/envoy.extensions.upstreams.http.v3.HttpProtocolOptions");
                W(8, "explicit_http_config:");
                W(10, "http_protocol_options: {}");
            }
        }

        public override string ToString() => sb.ToString();
    }

    private static string EmitBootstrap(Action<YamlEmitter> configure)
    {
        var yaml = new YamlEmitter();
        configure(yaml);
        return yaml.ToString();
    }

    private static (string CertDest, string KeyDest) CopyPemFiles(string prefixDir, string certPem, string keyPem)
    {
        var certDest = Path.Combine(prefixDir, "certs", "server.crt");
        var keyDest = Path.Combine(prefixDir, "certs", "server.key");
        File.Copy(certPem, certDest, overwrite: true);
        File.Copy(keyPem, keyDest, overwrite: true);
        return (certDest.Replace('\\', '/'), keyDest.Replace('\\', '/'));
    }

    private static Func<string, int, string> BuildHttp1Conf(int originHttpPort) => (_, port) =>
        EmitBootstrap(y =>
        {
            y.Admin(GetFreeTcpPort());
            y.StaticResourcesHeader();
            y.TcpListener("listener_http1", port, "ingress_http1", "HTTP1", altSvc: null,
                certPath: null, keyPath: null, alpnProtocols: null);
            y.Cluster(originHttpPort, upstreamTls: false);
        });

    private static Func<string, int, string> BuildHttp1TlsConf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_http1_tls", port, "ingress_http1_tls", "HTTP1", altSvc: null,
                    certDest, keyDest, ["http/1.1"]);
                y.Cluster(originHttpPort, upstreamTls: false);
            });
        };

    private static Func<string, int, string> BuildHttp2Conf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_http2", port, "ingress_http2", "AUTO", altSvc: null,
                    certDest, keyDest, ["h2", "http/1.1"]);
                y.Cluster(originHttpPort, upstreamTls: false);
            });
        };

    private static Func<string, int, string> BuildHttp3CleartextConf(int originHttpPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var altSvc = $$"""h3=":{{port}}"; ma=86400""";
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_tcp", port, "ingress_tcp", "AUTO", altSvc,
                    certDest, keyDest, ["h2", "http/1.1"]);
                y.QuicListener("listener_quic_v4", port, "127.0.0.1", "ingress_quic_v4", certDest, keyDest);
                y.QuicListener("listener_quic_v6", port, "::1", "ingress_quic_v6", certDest, keyDest);
                y.Cluster(originHttpPort, upstreamTls: false);
            });
        };

    private static Func<string, int, string> BuildHttp1ToHttpsConf(int originHttpsPort) => (_, port) =>
        EmitBootstrap(y =>
        {
            y.Admin(GetFreeTcpPort());
            y.StaticResourcesHeader();
            y.TcpListener("listener_http1", port, "ingress_http1", "HTTP1", altSvc: null,
                certPath: null, keyPath: null, alpnProtocols: null);
            y.Cluster(originHttpsPort, upstreamTls: true);
        });

    private static Func<string, int, string> BuildHttp1TlsToHttpsConf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_http1_tls", port, "ingress_http1_tls", "HTTP1", altSvc: null,
                    certDest, keyDest, ["http/1.1"]);
                y.Cluster(originHttpsPort, upstreamTls: true);
            });
        };

    private static Func<string, int, string> BuildHttp2ToHttpsHttp1Conf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_http2", port, "ingress_http2", "AUTO", altSvc: null,
                    certDest, keyDest, ["h2", "http/1.1"]);
                y.Cluster(originHttpsPort, upstreamTls: true);
            });
        };

    private static Func<string, int, string> BuildHttp3ToHttpsHttp1Conf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var altSvc = $$"""h3=":{{port}}"; ma=86400""";
            return EmitBootstrap(y =>
            {
                y.Admin(GetFreeTcpPort());
                y.StaticResourcesHeader();
                y.TcpListener("listener_tcp", port, "ingress_tcp", "AUTO", altSvc,
                    certDest, keyDest, ["h2", "http/1.1"]);
                y.QuicListener("listener_quic_v4", port, "127.0.0.1", "ingress_quic_v4", certDest, keyDest);
                y.QuicListener("listener_quic_v6", port, "::1", "ingress_quic_v6", certDest, keyDest);
                y.Cluster(originHttpsPort, upstreamTls: true);
            });
        };

    /// <summary>
    /// True for modern Envoy (HTTP/3 is compiled in on official GitHub/Homebrew builds).
    /// <c>envoy --version</c> is typically <c>…/1.36.7/Clean/RELEASE/BoringSSL</c> with no "quic" token,
    /// so grepping the version string used to skip H3 arms that the binary can run.
    /// </summary>
    internal static bool SupportsHttp3(string exe)
    {
        var version = ReadEnvoyOutput(exe, "--version");
        if (ContainsHttp3Hint(version))
            return true;
        if (TryParseEnvoyVersion(version, out var major, out var minor) &&
            (major > 1 || (major == 1 && minor >= 20)))
            return true;
        var help = ReadEnvoyOutput(exe, "--help");
        if (ContainsHttp3Hint(help))
            return true;
        // Prefer running the arm over a silent skip when we resolved a binary.
        return !string.IsNullOrWhiteSpace(version) &&
               !string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseEnvoyVersion(string text, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // GitHub release: "envoy  version: <sha>/1.36.7/Clean/RELEASE/BoringSSL"
        var slash = Regex.Match(text, @"/(\d+)\.(\d+)\.(\d+)");
        if (slash.Success)
        {
            major = int.Parse(slash.Groups[1].Value, CultureInfo.InvariantCulture);
            minor = int.Parse(slash.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        var dotted = Regex.Match(text, @"\b(\d+)\.(\d+)\.(\d+)\b");
        if (!dotted.Success)
            return false;
        major = int.Parse(dotted.Groups[1].Value, CultureInfo.InvariantCulture);
        minor = int.Parse(dotted.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool ContainsHttp3Hint(string text) =>
        text.Contains("quic", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http3", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http/3", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string CertPem, string KeyPem)> ExportLoopbackPemAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        using var cert = LoopbackCertificateAuthority.ServerCertificate;
        var certPath = Path.Combine(dir, "server.crt");
        var keyPath = Path.Combine(dir, "server.key");

        var certPem = PemEncoding.Write("CERTIFICATE", cert.RawData);
        await File.WriteAllTextAsync(certPath, new string(certPem));

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
        TryStop(process);
        TryDeleteDir(prefixDir);
    }

    /// <summary>
    /// Resolves Envoy on PATH. Always returns null on Windows (no supported Windows binary in this harness).
    /// </summary>
    public static string? ResolveEnvoyExecutable(string? envoyPath)
    {
        if (OperatingSystem.IsWindows())
            return null;

        if (!string.IsNullOrWhiteSpace(envoyPath))
        {
            if (File.Exists(envoyPath))
                return Path.GetFullPath(envoyPath);
            return null;
        }

        return FindOnPath("envoy");
    }

    public static string EnvoyMissingMessage() =>
        """
        envoy was not found on PATH (and no --envoy-path was given), or this OS is Windows.
        TWP arms will still run. To enable the same-machine Envoy control arm:
          Linux:   install envoy from https://github.com/envoyproxy/envoy/releases or your distro package
          macOS:   brew install envoy
          Windows: not supported in this harness — cells are Not possible.
        Then re-run with envoy on PATH, or pass --envoy-path <path-to-envoy>.
        HTTP/3 terminate is enabled on official Envoy binaries (GitHub release / Homebrew).
        """;

    private static string ReadVersion(string exe)
    {
        var text = ReadEnvoyOutput(exe, "--version");
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;
        return line.Trim();
    }

    private static string ReadEnvoyOutput(string exe, string arguments)
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
