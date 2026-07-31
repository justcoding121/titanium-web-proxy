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
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
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

#pragma warning disable TWP001 // HTTP/3 is experimental — example intentionally exercises this API
#nullable enable
        private TransparentQuicProxyEndPoint? quicEndPoint;
#nullable restore
#pragma warning restore TWP001

        public ProxyTestController()
        {
            Task.Run(() => ListenToConsole());

            proxyServer = new ProxyServer();
            var certificateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Titanium.Web.Proxy");
            Directory.CreateDirectory(certificateDirectory);
            proxyServer.CertificateManager.PfxFilePath = Path.Combine(certificateDirectory, "rootCert.pfx");

            //proxyServer.EnableHttp2 = false;

            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

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

            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.ConnectionTimeOutSeconds = 15;
            proxyServer.ReuseSocket = false;
            // Pooling reuses origin TCP/TLS sockets and sharply reduces CONNECT/cert stampede when
            // the example is installed as the system proxy (browser + OS services share the endpoint).
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            //proxyServer.ProxyBasicAuthenticateFunc = async (args, userName, password) =>
            //{
            //    return true;
            //};

            // this is just to show the functionality, provided implementations use junk value
            //proxyServer.GetCustomUpStreamProxyFunc = onGetCustomUpStreamProxyFunc;
            //proxyServer.CustomUpStreamProxyFailureFunc = onCustomUpStreamProxyFailureFunc;

            // optionally set the Certificate Engine
            // Under Mono or Non-Windows runtimes only BouncyCastle will be supported
            //proxyServer.CertificateManager.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            // optionally set the Root Certificate
            //proxyServer.CertificateManager.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
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
            //proxyServer.OnResponseBodyWrite += OnResponseBodyWrite;

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            //proxyServer.EnableWinAuth = true;

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
            if (QuicListener.IsSupported)
            {
                proxyServer.EnableHttp3 = true;
                // EnableHttpsSvcbDnsDiscovery inherits EnableHttp3 (cold-path H3 via HTTPS/SVCB).
                // DnsServerEndPoint defaults to 8.8.8.8:53; override if you need a corporate resolver.
                quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443)
                {
                    // Replace with IOriginalDestinationResolver for real NAT-transparent interception.
                    // ForwardHost and ForwardPort are used as a fallback when no resolver is set.
                    ForwardHost = "localhost",
                    ForwardPort = 443
                };
                quicEndPoint.BeforeQuicAuthenticate += OnBeforeQuicAuthenticate;
                proxyServer.AddEndPoint(quicEndPoint);
                Console.WriteLine("HTTP/3 QUIC endpoint started on UDP 443.");
            }
            else
            {
                Console.WriteLine("[HTTP/3] Skipped: QuicListener.IsSupported is false on this platform.");
                Console.WriteLine("  Windows: requires Windows 11 / Server 2022+.");
                Console.WriteLine("  Linux:   apt install libmsquic");
                Console.WriteLine("  macOS:   bundle libmsquic + libssl + libcrypto with @loader_path RPATH.");
            }
#pragma warning restore TWP001

            proxyServer.Start();

            // Transparent endpoint is useful for reverse proxy (client is not aware of the existence of proxy)
            // A transparent endpoint usually requires a network router port forwarding HTTP(S) packets
            // or by DNS to send data to this endPoint.
            //var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 443, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "localhost",
            //
            //    // Optionally forward all traffic on this endpoint to a fixed upstream server
            //    // (e.g. a reverse proxy pointing at a fixed backend). Only the TCP connection
            //    // target changes; the original hostname is still used for TLS SNI/certificate
            //    // validation and the HTTP Host header.
            //    ForwardHost = "198.51.100.1",
            //    ForwardPort = 443
            //};

            //proxyServer.AddEndPoint(transparentEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy("localhost", 8888);
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy("localhost", 8888);

            // SOCKS proxy
            //proxyServer.UpStreamHttpProxy = new ExternalProxy("127.0.0.1", 1080)
            //    { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy("127.0.0.1", 1080)
            //    { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };


            //var socksEndPoint = new SocksProxyEndPoint(IPAddress.Any, 1080, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(socksEndPoint);

            foreach (var endPoint in proxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name,
                    endPoint.IpAddress, endPoint.Port);

            // Only explicit proxies can be set as system proxy!
            //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            if (RunTime.IsWindows) proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);
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

            // remove the generated certificates
            //proxyServer.CertificateManager.RemoveTrustedRootCertificates();
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
                Password = "fake",
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
                Password = "fake2",
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

        private Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
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

            // store it in the UserData property
            // It can be a simple integer, Guid, or any type
            //e.UserData = new CustomUserData()
            //{
            //    RequestHeaders = e.HttpClient.Request.Headers,
            //    RequestBody = e.HttpClient.Request.HasBody ? e.HttpClient.Request.Body:null,
            //    RequestBodyString = e.HttpClient.Request.HasBody? e.HttpClient.Request.BodyString:null
            //};

            ////This sample shows how to get the multipart form data headers
            //if (e.HttpClient.Request.Host == "mail.yahoo.com" && e.HttpClient.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}

            // To cancel a request with a custom HTML content
            // Filter URL
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("yahoo.com"))
            //{ 
            //    e.Ok("<!DOCTYPE html>" +
            //          "<html><body><h1>" +
            //          "Website Blocked" +
            //          "</h1>" +
            //          "<p>Blocked by titanium web proxy.</p>" +
            //          "</body>" +
            //          "</html>");
            //} 

            ////Redirect example
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("wikipedia.org"))
            //{ 
            //   e.Redirect("https://www.paypal.com");
            //} 
        }

        // Modify response
        private async Task MultipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(MultipartRequestPartSent));

            var session = (SessionEventArgs)sender;
            WriteToConsole("Multipart form data headers:");
            foreach (var header in e.Headers) WriteToConsole(header.ToString());
        }

        private async Task OnResponse(object sender, SessionEventArgs e)
        {
            e.GetState().AppendPipeline(nameof(OnResponse));

            // access user data set in request to do something with it
            //var userData = e.HttpClient.UserData as CustomUserData;

            //var ext = Path.GetExtension(e.HttpClient.Request.RequestUri.AbsolutePath);
            //if (ext == ".gif" || ext == ".png" || ext == ".jpg")
            //{ 
            //    byte[] btBody = Encoding.UTF8.GetBytes("<!DOCTYPE html>" +
            //                                           "<html><body><h1>" +
            //                                           "Image is blocked" +
            //                                           "</h1>" +
            //                                           "<p>Blocked by Titanium</p>" +
            //                                           "</body>" +
            //                                           "</html>");

            //    var response = new OkResponse(btBody);
            //    response.HttpVersion = e.HttpClient.Request.HttpVersion;

            //    e.Respond(response);
            //    e.TerminateServerConnection();
            //} 

            //// print out process id of current session
            ////WriteToConsole($"PID: {e.HttpClient.ProcessId.Value}");

            ////if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            //if (e.HttpClient.Request.Method == "GET" || e.HttpClient.Request.Method == "POST")
            //{
            //    if (e.HttpClient.Response.StatusCode == (int)HttpStatusCode.OK)
            //    {
            //        if (e.HttpClient.Response.ContentType != null && e.HttpClient.Response.ContentType.Trim().ToLower().Contains("text/html"))
            //        {
            //            var bodyBytes = await e.GetResponseBody();
            //            e.SetResponseBody(bodyBytes);

            //            string body = await e.GetResponseBodyAsString();
            //            e.SetResponseBodyString(body);
            //        }
            //    }
            //}
        }

        // Called for each response body chunk as it streams to the client (no full-body buffering).
        // Replace e.BodyBytes to modify the body on the fly.
        private Task OnResponseBodyWrite(object sender, BeforeBodyWriteEventArgs e)
        {
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

                    case RetryableServerConnectionException:
                        return true;
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
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
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
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
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
                await Task.Delay(50);
            }
        }

        ///// <summary>
        ///// User data object as defined by user.
        ///// User data can be set to each SessionEventArgs.HttpClient.UserData property
        ///// </summary>
        //public class CustomUserData
        //{
        //    public HeaderCollection RequestHeaders { get; set; }
        //    public byte[] RequestBody { get; set; }
        //    public string RequestBodyString { get; set; }
        //}
    }
}
