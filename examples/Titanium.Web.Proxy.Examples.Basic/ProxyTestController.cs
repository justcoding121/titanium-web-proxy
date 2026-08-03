using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Basic.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class ProxyTestController : IDisposable
    {
        private const int MaxUrlLength = 100;
        private const int MaxWebSocketTextLength = 120;

        private readonly ProxyServer proxyServer;
#if !DEBUG
        private readonly ILoggerFactory compactLoggerFactory;
#endif

        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        private readonly ConcurrentQueue<Tuple<ConsoleColor?, string>> consoleMessageQueue
            = new ConcurrentQueue<Tuple<ConsoleColor?, string>>();

        private ExplicitProxyEndPoint explicitEndPoint;

        private readonly bool trustRootCertificate;

#pragma warning disable TWP001 // HTTP/3 is experimental — example intentionally exercises this API
#nullable enable
        private TransparentQuicProxyEndPoint? quicEndPoint;
#nullable restore
#pragma warning restore TWP001

        public ProxyTestController()
        {
            Task.Run(() => ListenToConsole(), cancellationTokenSource.Token);

            proxyServer = new ProxyServer();
            var certificateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Titanium.Web.Proxy");
            Directory.CreateDirectory(certificateDirectory);
            proxyServer.CertificateManager.PfxFilePath = Path.Combine(certificateDirectory, "rootCert.pfx");

            // Installing a MITM root into the user's trust store is opt-in: set TWP_TRUST_ROOT=1 for
            // browser-driven runs. Nothing is added to any certificate store without it, and
            // RemoveTrustedRootCertificate() in Stop() takes it back out again.
            trustRootCertificate = Environment.GetEnvironmentVariable("TWP_TRUST_ROOT") is "1" or "true" or "TRUE";
            if (trustRootCertificate)
            {
                proxyServer.CertificateManager.EnsureRootCertificate();
                proxyServer.CertificateManager.TrustRootCertificate();
            }

            // Library diagnostics stay quiet on the traffic tape: one-line errors (no stacks) in Release.
            // DEBUG keeps the built-in Trace console (full stacks) for deep diagnosis.
#if DEBUG
            proxyServer.Logging.MinimumLevel = LogLevel.Trace;
            proxyServer.Logging.EnableFile = true;
            proxyServer.Logging.FilePath = Path.Combine(
                AppContext.BaseDirectory, "logs", "basic-proxy.log");
#else
            proxyServer.Logging.MinimumLevel = LogLevel.Warning;
            proxyServer.Logging.EnableConsole = false;
            compactLoggerFactory = new CompactConsoleLoggerFactory((level, line) =>
                WriteToConsole(line, level >= LogLevel.Error ? ConsoleColor.Red : ConsoleColor.Yellow));
            proxyServer.Logging.LoggerFactory = compactLoggerFactory;
#endif

            // Keep the library defaults for TcpTimeWaitSeconds (0 — abortive close, so a high-churn
            // proxy does not accumulate TIME_WAIT) and ConnectionTimeOutSeconds (60 — a short idle
            // pool lifetime forces full TCP/TLS reconnects after normal interactive think time).
            // Pooling reuses origin TCP/TLS sockets and sharply reduces CONNECT/cert stampede when
            // the example is installed as the system proxy (browser + OS services share the endpoint).
            //
            // ColdStartProbe (tools/ColdStartProbe) can override the knobs below via optional environment
            // variables; when unset each keeps the default on the next line. Names: TWP_ENABLE_CONNECTION_POOL,
            // TWP_FORWARD_UPSTREAM, TWP_PREFETCH, TWP_ENABLE_HTTP2, TWP_SAVE_FAKE_CERTS, TWP_LEAF_KEY (ec or rsa),
            // TWP_CAPTURE_TIMING, TWP_SET_SYSTEM_PROXY, TWP_ENABLE_HTTP3, TWP_ENABLE_SVCB_DNS (last two in StartProxy).
            proxyServer.EnableConnectionPool = ReadEnvBool("TWP_ENABLE_CONNECTION_POOL", defaultValue: true);
            // Per-request timing marks are measurement scaffolding; opt in for latency runs.
            proxyServer.EnableRequestTimingCapture =
                Environment.GetEnvironmentVariable("TWP_CAPTURE_TIMING") is "1" or "true" or "TRUE";
            // Resolves the Windows system/PAC upstream gateway per destination. Left on so the example
            // still works behind a corporate proxy. On a machine with no PAC or WPAD configured the
            // lookup is a no-op, so it is not a cold-start cost there; disable upstream forwarding to
            // measure its cost where a PAC script is actually deployed.
            // Direct destinations still get HTTP/3 (Alt-Svc + background QUIC warm); only destinations
            // that actually resolve to an upstream proxy stay on TCP, since QUIC cannot be tunnelled.
            proxyServer.ForwardToUpstreamGateway = ReadEnvBool("TWP_FORWARD_UPSTREAM", defaultValue: true);
            // Prefetch overlaps origin connect with client TLS on cache hits / HTTP/1.1 clients.
            // Cold HTTP/2 still awaits one origin probe for ALPN (library behavior).
            proxyServer.EnableTcpServerConnectionPrefetch = ReadEnvBool("TWP_PREFETCH", defaultValue: true);
            proxyServer.EnableHttp2 = ReadEnvBool("TWP_ENABLE_HTTP2", defaultValue: true);
            proxyServer.CertificateManager.SaveFakeCertificates =
                ReadEnvBool("TWP_SAVE_FAKE_CERTS", defaultValue: true);

            // Generating an RSA-2048 leaf is expensive; LeafRsaKeyPairBufferSize (default 8) pre-generates
            // pairs so many first visits avoid paying that on CONNECT. A P-256 leaf is cheap inline and
            // still gives every host its own key. Browsers all accept ECDSA server certificates; set
            // TWP_LEAF_KEY=rsa when intercepting an older client that does not. The root stays RSA.
            proxyServer.CertificateManager.LeafCertificateKeyAlgorithm =
                Environment.GetEnvironmentVariable("TWP_LEAF_KEY") is "rsa" or "RSA"
                    ? Network.CertificateKeyAlgorithm.Rsa2048
                    : Network.CertificateKeyAlgorithm.EcdsaP256;

            // ProxyResourceLimits.Default already bounds the in-memory certificate cache at 1024
            // entries (see its doc comment for why an unbounded cache was a defect, not a feature).
            // Shown explicitly here so this example documents a realistic desktop/dev configuration:
            // a slightly larger in-memory bound for a browsing-heavy manual test session, and an
            // unbounded on-disk cache (independent knob) so repeated runs against the same hosts
            // reuse previously generated certificates instead of regenerating them.
            proxyServer.ResourceLimits = ProxyResourceLimits.Default.WithCertificateCacheBounds(
                maxCertificateCacheEntries: 2048, maxCertificateDiskCacheEntries: null);
        }

        private CancellationToken CancellationToken => cancellationTokenSource.Token;

        public void Dispose()
        {
            cancellationTokenSource.Dispose();
            proxyServer.Dispose();
#if !DEBUG
            compactLoggerFactory?.Dispose();
#endif
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            proxyServer.AfterResponse += OnAfterResponse;

            // Inspect/modify the response body chunk-by-chunk as it streams, without buffering it in memory.
            // Do not combine with SessionEventArgs.GetResponseBody (which buffers the whole body).

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000);

            // Fired when a CONNECT request is received
            explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponse;

            // An explicit endpoint is where the client knows about the existence of a proxy
            // So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);

            // HTTP/3 transparent QUIC endpoint (experimental — suppress TWP001 to opt in).
            // Requires MsQuic native library and a supported OS:
            //   Windows: Windows 11 / Server 2022+
            //   Linux:   apt install libmsquic
            //   macOS:   bundle libmsquic + libssl + libcrypto with @loader_path RPATH
            // Traffic must be redirected here (e.g. via iptables/nftables UDP REDIRECT on Linux,
            // WFP on Windows, or pf rdr on macOS). See wiki/HTTP-3.md for setup details.
#pragma warning disable TWP001
            // Measurement matrix overrides (optional):
            //   TWP_ENABLE_HTTP3=1|0
            //   TWP_ENABLE_SVCB_DNS=1|0
            var enableHttp3Env = Environment.GetEnvironmentVariable("TWP_ENABLE_HTTP3");
            var enableSvcbEnv = Environment.GetEnvironmentVariable("TWP_ENABLE_SVCB_DNS");
            var enableHttp3 = enableHttp3Env switch
            {
                "1" or "true" or "TRUE" => true,
                "0" or "false" or "FALSE" => false,
                _ => QuicListener.IsSupported
            };

            if (enableHttp3 && QuicListener.IsSupported)
            {
                proxyServer.EnableHttp3 = true;
                // Interactive system-proxy browsing: learn H3 from Alt-Svc on the first response.
                // Proactive SVCB discovery is opt-in via TWP_ENABLE_SVCB_DNS so measurement runs can
                // re-enable the matrix without editing this file.
                proxyServer.EnableHttpsSvcbDnsDiscovery = enableSvcbEnv is "1" or "true" or "TRUE";
                quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443)
                {
                    // Replace with IOriginalDestinationResolver for real NAT-transparent interception.
                    // ForwardHost and ForwardPort are used as a fallback when no resolver is set.
                    ForwardHost = "localhost",
                    ForwardPort = 443
                };
                quicEndPoint.BeforeQuicAuthenticate += OnBeforeQuicAuthenticate;
                proxyServer.AddEndPoint(quicEndPoint);
                Console.WriteLine(
                    $"HTTP/3 QUIC endpoint started on UDP 443 (SVCB discovery={(proxyServer.EnableHttpsSvcbDnsDiscovery ? "on" : "off")}).");
            }
            else
            {
                Console.WriteLine("[HTTP/3] Skipped: QuicListener.IsSupported is false on this platform, or TWP_ENABLE_HTTP3 disabled it.");
                Console.WriteLine("  Windows: requires Windows 11 / Server 2022+.");
                Console.WriteLine("  Linux:   apt install libmsquic");
                Console.WriteLine("  macOS:   bundle libmsquic + libssl + libcrypto with @loader_path RPATH.");
            }
#pragma warning restore TWP001

            proxyServer.Start();

            foreach (var endPoint in proxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name,
                    endPoint.IpAddress, endPoint.Port);

            // tools/ColdStartProbe connects to the endpoint directly and sets TWP_SET_SYSTEM_PROXY=0,
            // so a measurement run never rewrites the machine's WinINet proxy configuration.
            if (OperatingSystem.IsWindows() && ReadEnvBool("TWP_SET_SYSTEM_PROXY", defaultValue: true))
                proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);

            Console.WriteLine(
                $"Knobs: pool={proxyServer.EnableConnectionPool} prefetch={proxyServer.EnableTcpServerConnectionPrefetch} " +
                $"h2={proxyServer.EnableHttp2} forwardUpstream={proxyServer.ForwardToUpstreamGateway} " +
                $"saveCerts={proxyServer.CertificateManager.SaveFakeCertificates} " +
                $"leafKey={proxyServer.CertificateManager.LeafCertificateKeyAlgorithm} " +
                $"timing={proxyServer.EnableRequestTimingCapture}");
        }

        private static bool ReadEnvBool(string name, bool defaultValue)
        {
            return Environment.GetEnvironmentVariable(name) switch
            {
                "1" or "true" or "TRUE" => true,
                "0" or "false" or "FALSE" => false,
                _ => defaultValue
            };
        }

        public void Stop()
        {
            WriteToConsole("Stopping proxy...");

            explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse -= OnBeforeTunnelConnectResponse;

#pragma warning disable TWP001
            if (quicEndPoint != null)
                quicEndPoint.BeforeQuicAuthenticate -= OnBeforeQuicAuthenticate;
#pragma warning restore TWP001

            proxyServer.BeforeRequest -= OnRequest;
            proxyServer.BeforeResponse -= OnResponse;
            proxyServer.AfterResponse -= OnAfterResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();

            // Only undo what this run installed (see TWP_TRUST_ROOT above).
            if (trustRootCertificate)
                proxyServer.CertificateManager.RemoveTrustedRootCertificate();
        }

        private async Task<IExternalProxy> OnGetCustomUpStreamProxyFunc(SessionEventArgsBase arg)
        {
            arg.GetState().AppendPipeline(nameof(OnGetCustomUpStreamProxyFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = false,
                HostName = "127.0.0.9",
                Port = 9090,
                Password = "fake", // NOSONAR S2068 - demo ExternalProxy sample credentials
                UserName = "fake",
                UseDefaultCredentials = false
            };
        }

        private async Task<IExternalProxy> OnCustomUpStreamProxyFailureFunc(SessionEventArgsBase arg)
        {
            arg.GetState().AppendPipeline(nameof(OnCustomUpStreamProxyFailureFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = false,
                HostName = "127.0.0.10",
                Port = 9191,
                Password = "fake2", // NOSONAR S2068 - demo ExternalProxy sample credentials
                UserName = "fake2",
                UseDefaultCredentials = false
            };
        }

        private async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            var hostname = e.HttpClient.Request.RequestUri.Host;
            e.GetState().AppendPipeline(nameof(OnBeforeTunnelConnectRequest) + ":" + hostname);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);

            if (hostname.Contains("dropbox.com"))
                // Exclude Https addresses you don't want to proxy
                // Useful for clients that use certificate pinning
                // for example dropbox.com
                e.DecryptSsl = false;

            // Opaque tunnels (no decrypt) get a single line; decrypted hosts show up as request lines later.
            // DEBUG also logs every CONNECT for diagnosis.
            if (!e.DecryptSsl)
                WriteToConsole($"TUNNEL {hostname} (ssl passthrough)");
#if DEBUG
            else
                WriteToConsole("Tunnel to: " + hostname);
#endif
        }

        private void WebSocket_DataSent(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, true);
        }

        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, false);
        }

        private void WebSocketDataSentReceived(SessionEventArgs args, DataEventArgs e, bool sent)
        {
            var color = sent ? ConsoleColor.Green : ConsoleColor.Blue;
            var arrow = sent ? "→" : "←";
            var decoder = sent ? args.WebSocketDecoderSend : args.WebSocketDecoderReceive;

            foreach (var frame in decoder.Decode(e.Buffer, e.Offset, e.Count))
            {
                if (frame.OpCode == WebsocketOpCode.Binary)
                {
                    WriteToConsole($"WS {arrow} binary {frame.Data.Length}B", color);
                    continue;
                }

                if (frame.OpCode == WebsocketOpCode.Text)
                    WriteToConsole($"WS {arrow} {Truncate(frame.GetText(), MaxWebSocketTextLength)}", color);
            }
        }

        private static Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            e.GetState().AppendPipeline(
                nameof(OnBeforeTunnelConnectResponse) + ":" + e.HttpClient.Request.RequestUri);

            return Task.CompletedTask;
        }

#pragma warning disable TWP001
        private Task OnBeforeQuicAuthenticate(object sender, BeforeQuicAuthenticateEventArgs e)
        {
            WriteToConsole($"[QUIC] Connection from {e.RemoteEndPoint} (SNI: {e.SniHostName})");
            return Task.CompletedTask;
        }
#pragma warning restore TWP001

        // intercept & cancel redirect or update requests
        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            var state = e.GetState();
            state.RequestStartedUtc = DateTime.UtcNow;
            state.AppendPipeline(nameof(OnRequest) + ":" + e.HttpClient.Request.RequestUri);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);

            // Subscribed here (rather than in OnResponse, once TunnelType.Websocket is known) so the
            // proxy sees a raw-byte tap is wanted before it forwards the upgrade request - only then can
            // it strip Sec-WebSocket-Extensions and keep frames uncompressed for WebSocketDecoder below
            // (see WebSocketHandler.HasWebSocketDataTapHandler remarks).
            if (e.HttpClient.Request.UpgradeToWebSocket)
            {
                e.DataSent += WebSocket_DataSent;
                e.DataReceived += WebSocket_DataReceived;
            }

            if (e.HttpClient.Request.Url.Contains("yahoo.com"))
                e.CustomUpStreamProxy = new ExternalProxy("localhost", 8888);
        }

        // Modify response
        private async Task MultipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(MultipartRequestPartSent));

            var session = (SessionEventArgs)sender;
            WriteToConsole("Multipart form data headers:");
            foreach (var header in e.Headers) WriteToConsole(header.ToString());
        }

        private static Task OnResponse(object sender, SessionEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(OnResponse));
            return Task.CompletedTask;
        }

        // Called for each response body chunk as it streams to the client (no full-body buffering).
        // Replace e.BodyBytes to modify the body on the fly.
        private Task OnResponseBodyWrite(object sender, BeforeBodyWriteEventArgs e)
        {
            _ = sender; // Required by the event-handler signature.
            WriteToConsole($"Response body chunk: {e.BodyBytes.Length} bytes (last: {e.IsLastChunk})");
            return Task.CompletedTask;
        }

        private async Task OnAfterResponse(object sender, SessionEventArgs e)
        {
            var state = e.GetState();
            var request = e.HttpClient.Request;
            var response = e.HttpClient.Response;
            var statusCode = response?.StatusCode ?? 0;
            var elapsedMs = state.RequestStartedUtc == default
                ? 0
                : (long)(DateTime.UtcNow - state.RequestStartedUtc).TotalMilliseconds;

            // A status-0 response with no HttpVersion means the origin round trip never produced a
            // response at all. Classify expected teardown / origin reachability separately from
            // genuine proxy defects so the traffic tape does not paint ad-beacon DNS misses red.
            var tapeKind = ClassifyIncompleteSession(statusCode, e.Exception);

            // Compact traffic tape: METHOD host/path → status  H2↔H2  187ms
            string line;
            ConsoleColor color;
            switch (tapeKind)
            {
                case IncompleteSessionKind.ClientCancelled:
                    line =
                        $"{request.Method,-7} {FormatUrlForConsole(request.Url)} ⇢ cancelled by client  {elapsedMs}ms";
                    color = ConsoleColor.DarkGray;
                    break;
                case IncompleteSessionKind.ConnectFailed:
                    line =
                        $"{request.Method,-7} {FormatUrlForConsole(request.Url)} ⇢ connect failed  {FormatHttpProtocolShort(request.HttpVersion)}↔?  {elapsedMs}ms";
                    color = ConsoleColor.DarkYellow;
                    break;
                default:
                    line =
                        $"{request.Method,-7} {FormatUrlForConsole(request.Url)} → {statusCode,3}  {FormatHttpProtocolShort(request.HttpVersion)}↔{FormatHttpProtocolShort(response?.HttpVersion)}  {elapsedMs}ms";
                    color = ColorForStatusCode(statusCode);
                    break;
            }

            WriteToConsole(line, color);

#if DEBUG
            try
            {
                WriteToConsole($"Pipelineinfo: {state.GetPipelineInfo()}", ConsoleColor.Yellow);
            }
            catch
            {
                // PipelineInfo is diagnostic-only; ignore races/teardown.
            }
#endif
        }

        private enum IncompleteSessionKind
        {
            None,
            ClientCancelled,
            ConnectFailed
        }

        /// <summary>
        ///     Maps status-0 sessions to traffic-tape presentation. Client aborts and origin
        ///     connect/DNS/TLS failures are expected under heavy browsing; only unclassified
        ///     status-0 (and real 5xx responses) stay red.
        /// </summary>
        private static IncompleteSessionKind ClassifyIncompleteSession(int statusCode, Exception exception)
        {
            if (statusCode != 0 || exception == null)
                return IncompleteSessionKind.None;

            if (exception is OperationCanceledException ||
                GetInnermostException(exception) is OperationCanceledException)
                return IncompleteSessionKind.ClientCancelled;

            if (IsOriginConnectFailure(exception))
                return IncompleteSessionKind.ConnectFailed;

            return IncompleteSessionKind.None;
        }

        private static bool IsOriginConnectFailure(Exception exception)
        {
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                switch (ex)
                {
                    case SocketException socketEx:
                        switch (socketEx.SocketErrorCode)
                        {
                            case SocketError.HostNotFound:
                            case SocketError.NoData:
                            case SocketError.TryAgain:
                            case SocketError.NetworkUnreachable:
                            case SocketError.HostUnreachable:
                            case SocketError.ConnectionRefused:
                            case SocketError.TimedOut:
                            case SocketError.NetworkDown:
                            case SocketError.ConnectionReset:
                            case SocketError.ConnectionAborted:
                                return true;
                        }

                        break;

                    case IOException ioEx:
                        // TLS/TCP peer closed before headers (common for flaky ad sync endpoints).
                        if (ioEx.InnerException is SocketException)
                            return true;
                        if (ioEx.Message.IndexOf("unexpected EOF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ioEx.Message.IndexOf("0 bytes from the transport stream", StringComparison.OrdinalIgnoreCase) >=
                            0)
                            return true;
                        break;
                }
            }

            return false;
        }

        private static Exception GetInnermostException(Exception exception)
        {
            var current = exception;
            while (current.InnerException != null)
                current = current.InnerException;
            return current;
        }

        private static ConsoleColor ColorForStatusCode(int statusCode)
        {
            if (statusCode >= 500 || statusCode == 0)
                return ConsoleColor.Red;
            if (statusCode >= 400)
                return ConsoleColor.Yellow;
            return ConsoleColor.Cyan;
        }

        /// <summary>
        ///     Host + path for the console; drops long query strings and truncates.
        /// </summary>
        private static string FormatUrlForConsole(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            string compact;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                compact = uri.IsDefaultPort
                    ? uri.Host + uri.AbsolutePath
                    : uri.Host + ":" + uri.Port + uri.AbsolutePath;
                if (uri.Query.Length > 1)
                    compact += "?…";
            }
            else
            {
                compact = url;
            }

            return Truncate(compact, MaxUrlLength);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength - 1) + "…";
        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public static Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(OnCertificateValidation));

            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None) e.IsValid = true;

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public static Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(OnCertificateSelection));

            // set e.clientCertificate to override

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Formats an HTTP version for brief console logs (e.g. H1.1, H2, H3).
        /// </summary>
        private static string FormatHttpProtocolShort(Version version)
        {
            if (version == null || version.Major == 0)
                return "?";

            if (version.Major >= 2)
                return "H" + version.Major;

            return "H" + version.Major + "." + version.Minor;
        }

        private void WriteToConsole(string message, ConsoleColor? consoleColor = null)
        {
            consoleMessageQueue.Enqueue(new Tuple<ConsoleColor?, string>(consoleColor, message));
        }

        private async Task ListenToConsole()
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                while (consoleMessageQueue.TryDequeue(out var item))
                {
                    var consoleColor = item.Item1;
                    var message = item.Item2;

                    if (consoleColor.HasValue)
                    {
                        var existing = Console.ForegroundColor;
                        Console.ForegroundColor = consoleColor.Value;
                        Console.WriteLine(message);
                        Console.ForegroundColor = existing;
                    }
                    else
                    {
                        Console.WriteLine(message);
                    }
                }

                //reduce CPU usage
                await Task.Delay(50, cancellationTokenSource.Token);
            }
        }
    }
}
