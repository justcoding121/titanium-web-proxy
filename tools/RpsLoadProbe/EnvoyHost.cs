using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

        var confPath = Path.Combine(prefixDir, "config.yaml");
        var conf = confBuilder(prefixDir, port);
        await File.WriteAllTextAsync(confPath, conf, Encoding.ASCII);

        var baseId = Random.Shared.Next(1, 1000);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments =
                $"-c \"{confPath}\" --concurrency {Environment.ProcessorCount} --disable-hot-restart --base-id {baseId}",
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
                    $"envoy exited early (code {process.ExitCode}). stderr: {err} stdout: {stdout}");
            }

            Thread.Sleep(50);
        }

        TryStop(process);
        TryDeleteDir(prefixDir);
        throw new TimeoutException($"envoy did not open port {port} in time.");
    }

    private static string BootstrapHeader(int adminPort) => $$"""
        admin:
          address:
            socket_address:
              address: 127.0.0.1
              port_value: {{adminPort}}
        static_resources:
        """;

    private static string HttpCluster(int originPort, bool upstreamTls) => upstreamTls
        ? $$"""
              clusters:
              - name: origin
                connect_timeout: 5s
                type: STATIC
                lb_policy: ROUND_ROBIN
                circuit_breakers:
                  thresholds:
                  - priority: DEFAULT
                    max_connections: 256
                    max_pending_requests: 256
                load_assignment:
                  cluster_name: origin
                  endpoints:
                  - lb_endpoints:
                    - endpoint:
                        address:
                          socket_address:
                            address: 127.0.0.1
                            port_value: {{originPort}}
                transport_socket:
                  name: envoy.transport_sockets.tls
                  typed_config:
                    "@type": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.UpstreamTlsContext
                    sni: localhost
                    common_tls_context:
                      validation_context:
                        trust_chain_verification: ACCEPT_UNTRUSTED
                typed_extension_protocol_options:
                  envoy.extensions.upstreams.http.v3.HttpProtocolOptions:
                    "@type": type.googleapis.com/envoy.extensions.upstreams.http.v3.HttpProtocolOptions
                    explicit_http_config:
                      http_protocol_options: {}
            """
        : $$"""
              clusters:
              - name: origin
                connect_timeout: 5s
                type: STATIC
                lb_policy: ROUND_ROBIN
                circuit_breakers:
                  thresholds:
                  - priority: DEFAULT
                    max_connections: 256
                    max_pending_requests: 256
                load_assignment:
                  cluster_name: origin
                  endpoints:
                  - lb_endpoints:
                    - endpoint:
                        address:
                          socket_address:
                            address: 127.0.0.1
                            port_value: {{originPort}}
            """;

    private static string RouteBlock(string? altSvcValue = null)
    {
        var altSvc = altSvcValue == null
            ? string.Empty
            : $$"""

                          response_headers_to_add:
                          - header:
                              key: alt-svc
                              value: '{{altSvcValue}}'
                """;
        return $$"""
                      route_config:
                        name: local_route
                        virtual_hosts:
                        - name: local
                          domains: ["*"]
                          routes:
                          - match:
                              prefix: "/"
                            route:
                              cluster: origin
                              timeout: 65s{{altSvc}}
            """;
    }

    private static string HttpConnectionManager(string statPrefix, string codecType, string routeBlock) => $$"""
                  - name: envoy.filters.network.http_connection_manager
                    typed_config:
                      "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
                      stat_prefix: {{statPrefix}}
                      codec_type: {{codecType}}
                      access_log: []
                      stream_idle_timeout: 65s
                      request_timeout: 65s
                      common_http_protocol_options:
                        idle_timeout: 65s
        {{routeBlock}}
                      http_filters:
                      - name: envoy.filters.http.router
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router
        """;

    private static string TlsFilterChain(string certPath, string keyPath, string alpnList, string hcm) => $$"""
                filter_chains:
                - transport_socket:
                    name: envoy.transport_sockets.tls
                    typed_config:
                      "@type": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.DownstreamTlsContext
                      common_tls_context:
                        tls_certificates:
                        - certificate_chain:
                            filename: {{certPath}}
                          private_key:
                            filename: {{keyPath}}
                        alpn_protocols: [{{alpnList}}]
                  filters:
        {{hcm}}
        """;

    private static (string CertDest, string KeyDest) CopyPemFiles(string prefixDir, string certPem, string keyPem)
    {
        var certDest = Path.Combine(prefixDir, "certs", "server.crt");
        var keyDest = Path.Combine(prefixDir, "certs", "server.key");
        File.Copy(certPem, certDest, overwrite: true);
        File.Copy(keyPem, keyDest, overwrite: true);
        return (certDest.Replace('\\', '/'), keyDest.Replace('\\', '/'));
    }

    private static Func<string, int, string> BuildHttp1Conf(int originHttpPort) => (prefixDir, port) =>
    {
        var adminPort = GetFreeTcpPort();
        return $$"""
            {{BootstrapHeader(adminPort)}}
              listeners:
              - name: listener_http1
                address:
                  socket_address:
                    address: 127.0.0.1
                    port_value: {{port}}
                filter_chains:
                - filters:
            {{HttpConnectionManager("ingress_http1", "HTTP1", RouteBlock())}}
            {{HttpCluster(originHttpPort, upstreamTls: false)}}
            """;
    };

    private static Func<string, int, string> BuildHttp1TlsConf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var hcm = HttpConnectionManager("ingress_http1_tls", "HTTP1", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_http1_tls
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"http/1.1\"", hcm)}}
                {{HttpCluster(originHttpPort, upstreamTls: false)}}
                """;
        };

    private static Func<string, int, string> BuildHttp2Conf(int originHttpPort, string certPem, string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var hcm = HttpConnectionManager("ingress_http2", "AUTO", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_http2
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"h2\", \"http/1.1\"", hcm)}}
                {{HttpCluster(originHttpPort, upstreamTls: false)}}
                """;
        };

    private static Func<string, int, string> BuildHttp3CleartextConf(int originHttpPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var altSvc = $$"""h3=":{{port}}"; ma=86400""";
            var tcpHcm = HttpConnectionManager("ingress_tcp", "AUTO", RouteBlock(altSvc));
            var quicHcmV4 = HttpConnectionManager("ingress_quic_v4", "HTTP3", RouteBlock());
            var quicHcmV6 = HttpConnectionManager("ingress_quic_v6", "HTTP3", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_tcp
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"h2\", \"http/1.1\"", tcpHcm)}}
                  - name: listener_quic_v4
                    address:
                      socket_address:
                        protocol: UDP
                        address: 127.0.0.1
                        port_value: {{port}}
                    listener_filters:
                    - name: envoy.filters.listener.quic
                      typed_config:
                        "@type": type.googleapis.com/envoy.extensions.filters.listener.quic.v3.QuicListenerFilter
                    filter_chains:
                    - filter_chain_match:
                        transport_protocol: quic
                      transport_socket:
                        name: envoy.transport_sockets.quic
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.transport_sockets.quic.v3.QuicDownstreamTransport
                          downstream_tls_context:
                            common_tls_context:
                              tls_certificates:
                              - certificate_chain:
                                  filename: {{certDest}}
                                private_key:
                                  filename: {{keyDest}}
                      filters:
                {{quicHcmV4}}
                  - name: listener_quic_v6
                    address:
                      socket_address:
                        protocol: UDP
                        address: ::1
                        port_value: {{port}}
                    listener_filters:
                    - name: envoy.filters.listener.quic
                      typed_config:
                        "@type": type.googleapis.com/envoy.extensions.filters.listener.quic.v3.QuicListenerFilter
                    filter_chains:
                    - filter_chain_match:
                        transport_protocol: quic
                      transport_socket:
                        name: envoy.transport_sockets.quic
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.transport_sockets.quic.v3.QuicDownstreamTransport
                          downstream_tls_context:
                            common_tls_context:
                              tls_certificates:
                              - certificate_chain:
                                  filename: {{certDest}}
                                private_key:
                                  filename: {{keyDest}}
                      filters:
                {{quicHcmV6}}
                {{HttpCluster(originHttpPort, upstreamTls: false)}}
                """;
        };

    private static Func<string, int, string> BuildHttp1ToHttpsConf(int originHttpsPort) => (prefixDir, port) =>
    {
        var adminPort = GetFreeTcpPort();
        return $$"""
            {{BootstrapHeader(adminPort)}}
              listeners:
              - name: listener_http1
                address:
                  socket_address:
                    address: 127.0.0.1
                    port_value: {{port}}
                filter_chains:
                - filters:
            {{HttpConnectionManager("ingress_http1", "HTTP1", RouteBlock())}}
            {{HttpCluster(originHttpsPort, upstreamTls: true)}}
            """;
    };

    private static Func<string, int, string> BuildHttp1TlsToHttpsConf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var hcm = HttpConnectionManager("ingress_http1_tls", "HTTP1", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_http1_tls
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"http/1.1\"", hcm)}}
                {{HttpCluster(originHttpsPort, upstreamTls: true)}}
                """;
        };

    private static Func<string, int, string> BuildHttp2ToHttpsHttp1Conf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var hcm = HttpConnectionManager("ingress_http2", "AUTO", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_http2
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"h2\", \"http/1.1\"", hcm)}}
                {{HttpCluster(originHttpsPort, upstreamTls: true)}}
                """;
        };

    private static Func<string, int, string> BuildHttp3ToHttpsHttp1Conf(int originHttpsPort, string certPem,
        string keyPem) =>
        (prefixDir, port) =>
        {
            var adminPort = GetFreeTcpPort();
            var (certDest, keyDest) = CopyPemFiles(prefixDir, certPem, keyPem);
            var altSvc = $$"""h3=":{{port}}"; ma=86400""";
            var tcpHcm = HttpConnectionManager("ingress_tcp", "AUTO", RouteBlock(altSvc));
            var quicHcmV4 = HttpConnectionManager("ingress_quic_v4", "HTTP3", RouteBlock());
            var quicHcmV6 = HttpConnectionManager("ingress_quic_v6", "HTTP3", RouteBlock());
            return $$"""
                {{BootstrapHeader(adminPort)}}
                  listeners:
                  - name: listener_tcp
                    address:
                      socket_address:
                        address: 127.0.0.1
                        port_value: {{port}}
                {{TlsFilterChain(certDest, keyDest, "\"h2\", \"http/1.1\"", tcpHcm)}}
                  - name: listener_quic_v4
                    address:
                      socket_address:
                        protocol: UDP
                        address: 127.0.0.1
                        port_value: {{port}}
                    listener_filters:
                    - name: envoy.filters.listener.quic
                      typed_config:
                        "@type": type.googleapis.com/envoy.extensions.filters.listener.quic.v3.QuicListenerFilter
                    filter_chains:
                    - filter_chain_match:
                        transport_protocol: quic
                      transport_socket:
                        name: envoy.transport_sockets.quic
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.transport_sockets.quic.v3.QuicDownstreamTransport
                          downstream_tls_context:
                            common_tls_context:
                              tls_certificates:
                              - certificate_chain:
                                  filename: {{certDest}}
                                private_key:
                                  filename: {{keyDest}}
                      filters:
                {{quicHcmV4}}
                  - name: listener_quic_v6
                    address:
                      socket_address:
                        protocol: UDP
                        address: ::1
                        port_value: {{port}}
                    listener_filters:
                    - name: envoy.filters.listener.quic
                      typed_config:
                        "@type": type.googleapis.com/envoy.extensions.filters.listener.quic.v3.QuicListenerFilter
                    filter_chains:
                    - filter_chain_match:
                        transport_protocol: quic
                      transport_socket:
                        name: envoy.transport_sockets.quic
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.transport_sockets.quic.v3.QuicDownstreamTransport
                          downstream_tls_context:
                            common_tls_context:
                              tls_certificates:
                              - certificate_chain:
                                  filename: {{certDest}}
                                private_key:
                                  filename: {{keyDest}}
                      filters:
                {{quicHcmV6}}
                {{HttpCluster(originHttpsPort, upstreamTls: true)}}
                """;
        };

    /// <summary>Best-effort: true when <c>envoy --help</c> or <c>--version</c> mentions QUIC/HTTP/3.</summary>
    internal static bool SupportsHttp3(string exe)
    {
        var version = ReadEnvoyOutput(exe, "--version");
        if (ContainsHttp3Hint(version))
            return true;
        var help = ReadEnvoyOutput(exe, "--help");
        return ContainsHttp3Hint(help);
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
        HTTP/3 terminate needs a build with QUIC/HTTP/3 enabled (check `envoy --help` for quic/http3).
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
