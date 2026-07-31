using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

/// <inheritdoc />
/// <summary>
///     This class is the backbone of proxy. One can create as many instances as needed.
///     However care should be taken to avoid using the same listening ports across multiple instances.
/// </summary>
public partial class ProxyServer : IDisposable
{
    /// <summary>
    ///     HTTP &amp; HTTPS scheme shorthands.
    /// </summary>
    internal static readonly string UriSchemeHttp = Uri.UriSchemeHttp;

    internal static readonly string UriSchemeHttps = Uri.UriSchemeHttps;

    internal static ByteString UriSchemeHttp8 = (ByteString)UriSchemeHttp;
    internal static ByteString UriSchemeHttps8 = (ByteString)UriSchemeHttps;

    /// <summary>
    ///     Backing field for exposed public property.
    /// </summary>
    private int clientConnectionCount;

    /// <summary>
    ///     Backing field for <see cref="Http3ClientConnectionCount" />.
    /// </summary>
    private int http3ClientConnectionCount;

    /// <summary>
    ///     Global admission counter for the admission gate, incremented/decremented synchronously at
    ///     <see cref="HandleClient(Socket,ProxyEndPoint)" /> entry/exit. Deliberately independent of
    ///     <see cref="clientConnectionCount" />: that counter is decremented from a fire-and-forget task
    ///     behind a hardcoded one-second TIME_WAIT delay in <see cref="TcpClientConnection.Dispose" />,
    ///     so gating admission on it would reject healthy traffic for a full second after every closed
    ///     connection.
    /// </summary>
    private int admittedClientConnectionCount;

    /// <summary>
    ///     Number of client connections rejected by the global admission gate (see
    ///     <see cref="MaxConcurrentClientConnections" />). A single bounded counter, not broken down by
    ///     endpoint, so it stays cardinality-safe regardless of how many endpoints a host application adds.
    /// </summary>
    private long globalAdmissionRejectionCount;

    /// <summary>
    ///     Number of client connections rejected by a per-endpoint admission gate (see
    ///     <see cref="ProxyEndPoint.MaxConcurrentClients" />), aggregated across all endpoints into one
    ///     counter for the same cardinality-safety reason as <see cref="globalAdmissionRejectionCount" />.
    ///     Per-endpoint rejection counts remain available, without adding label cardinality, via
    ///     <see cref="ProxyEndPoint.AdmittedClientCount" /> and <see cref="ProxyEndPoint.MaxConcurrentClients" />
    ///     on each of the caller's own, already-bounded <see cref="ProxyEndPoints" /> instances.
    /// </summary>
    private long endpointAdmissionRejectionCount;

    /// <summary>
    ///     Per-session cancellation tokens for in-flight client handlers. Cancelled on Stop/StopAsync
    ///     so active relays do not outlive the listener (issues #919 / #799 / #809).
    /// </summary>
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> activeSessionCancellations = new();

    /// <summary>
    ///     Backing field for exposed public property.
    /// </summary>
    private int serverConnectionCount;

    /// <summary>
    ///     Backing field for <see cref="Http3ServerConnectionCount" />.
    /// </summary>
    private int http3ServerConnectionCount;

    /// <summary>
    ///     Upstream proxy manager.
    /// </summary>
    private WinHttpWebProxyFinder? systemProxyResolver;

    /// <summary>
    ///     Backing field for <see cref="Logging" />.
    /// </summary>
    private ProxyLoggingOptions loggingOptions = new();

    /// <summary>
    ///     The currently active logger factory, built from <see cref="loggingOptions" />. Owned (and
    ///     disposed) by this instance unless <see cref="ProxyLoggingOptions.LoggerFactory" /> was set, in
    ///     which case it is a live reference to the user-supplied factory and is never disposed here.
    /// </summary>
    private ILoggerFactory activeLoggerFactory = NullLoggerFactory.Instance;

    private bool ownsActiveLoggerFactory;

    /// <summary>
    ///     The shared logger used by every part of this proxy instance (all partial <c>ProxyServer</c>
    ///     handler files, and handed down live to <see cref="CertificateManager" />,
    ///     <see cref="TcpConnectionFactory" />, sessions, etc.). Rebuilt whenever <see cref="Logging" />
    ///     is replaced or <see cref="ApplyLoggingConfiguration" /> is called, so certificate operations
    ///     performed before <see cref="Start" /> are covered from the moment this instance is
    ///     constructed.
    /// </summary>
    private ILogger logger = NullLogger.Instance;


    /// <inheritdoc />
    /// <summary>
    ///     Initializes a new instance of ProxyServer class with provided parameters.
    /// </summary>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    public ProxyServer(bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
        bool trustRootCertificateAsAdmin = false) : this(null, null, userTrustRootCertificate,
        machineTrustRootCertificate, trustRootCertificateAsAdmin)
    {
    }

    /// <summary>
    ///     Initializes a new instance of ProxyServer class with provided parameters.
    /// </summary>
    /// <param name="rootCertificateName">Name of the root certificate.</param>
    /// <param name="rootCertificateIssuerName">Name of the root certificate issuer.</param>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    public ProxyServer(string? rootCertificateName, string? rootCertificateIssuerName,
        bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
        bool trustRootCertificateAsAdmin = false)
    {
        // Build the initial logger before creating CertificateManager/TcpConnectionFactory so that
        // certificate operations performed before Start() (e.g. EnsureRootCertificate) are covered.
        ApplyLoggingConfiguration();

        BufferPool = new DefaultBufferPool();
        ProxyEndPoints = new List<ProxyEndPoint>();
        TcpConnectionFactory = new TcpConnectionFactory(this);
        QuicConnectionPool = new Network.Quic.QuicConnectionPool(this);
        if (RunTime.IsWindows && !RunTime.IsUwpOnWindows) SystemProxySettingsManager = new SystemProxyManager();

        CertificateManager = new CertificateManager(rootCertificateName, rootCertificateIssuerName,
            userTrustRootCertificate, machineTrustRootCertificate, trustRootCertificateAsAdmin, logger,
            () => ResourceLimits.MaxCertificateCacheEntries);
    }

    /// <summary>
    ///     An factory that creates tcp connection to server.
    /// </summary>
    internal TcpConnectionFactory TcpConnectionFactory { get; }

    /// <summary>
    ///     Pool of outbound QUIC connections to HTTP/3 origin servers.
    ///     Drained on proxy stop and disposed with the proxy.
    /// </summary>
    internal Network.Quic.QuicConnectionPool QuicConnectionPool { get; }

    /// <summary>
    ///     Caches, per upstream host:port, whether the real origin negotiates HTTP/2 via TLS ALPN - so that
    ///     repeat CONNECT tunnels to the same host (very common with real browsers) do not each pay for their
    ///     own redundant probe TLS handshake. See <see cref="Http2OriginCapabilityCache" />.
    /// </summary>
    internal Http2OriginCapabilityCache Http2OriginCapabilityCache { get; } =
        new(TimeSpan.FromMinutes(5));

    /// <summary>
    ///     Caches, per upstream host:port, whether the real origin supports HTTP/3 (QUIC), as discovered via
    ///     <c>Alt-Svc</c> response headers or HTTPS/SVCB DNS records. See <see cref="Http3.Http3OriginCapabilityCache" />.
    /// </summary>
    internal Http3.Http3OriginCapabilityCache Http3OriginCapabilityCache { get; } = new();

    /// <summary>
    ///     Removes expired entries from both origin-capability caches. Called from the connection-pool
    ///     cleanup loop every few seconds so stale origin records do not accumulate indefinitely.
    /// </summary>
    internal void TrimOriginCapabilityCaches()
    {
        Http2OriginCapabilityCache.TrimExpired();
        Http3OriginCapabilityCache.TrimExpired();
    }

    /// <summary>
    ///     Manage system proxy settings.
    /// </summary>
    private SystemProxyManager? SystemProxySettingsManager { get; }

    /// <summary>
    ///     Number of times to retry upon network failures when connection pool is enabled.
    /// </summary>
    public int NetworkFailureRetryAttempts { get; set; } = 1;

    /// <summary>
    ///     Is the proxy currently running?
    /// </summary>
    public bool ProxyRunning { get; private set; }

    /// <summary>
    ///     Gets or sets a value indicating whether requests will be chained to upstream gateway.
    ///     Defaults to false.
    /// </summary>
    public bool ForwardToUpstreamGateway { get; set; }

    /// <summary>
    ///     If set, the upstream proxy will be detected by a script that will be loaded from the provided Uri
    /// </summary>
    public Uri? UpstreamProxyConfigurationScript { get; set; }

    /// <summary>
    ///     Enable disable Windows Authentication (NTLM/Kerberos).
    ///     By default SSPI uses the process identity. To authenticate as another user, set
    ///     <see cref="WinAuthCredentialsProvider" /> (issue #461). Defaults to false.
    /// </summary>
    public bool EnableWinAuth { get; set; }

    /// <summary>
    ///     Optional per-session credential provider for server 401 WinAuth (NTLM/Negotiate/Kerberos).
    ///     Return <see langword="null" /> to use the current process identity (legacy behavior).
    ///     Do not put plaintext passwords on <see cref="SessionEventArgs" /> — use this callback instead.
    ///     Windows SSPI only; ignored on non-Windows platforms.
    /// </summary>
    public Func<SessionEventArgs, Task<WinAuthCredentials?>>? WinAuthCredentialsProvider { get; set; }

    /// <summary>
    ///     Overrides upstream proxy Windows authentication token generation.
    ///     Intended for internal testing; production uses the current process identity through SSPI.
    /// </summary>
    internal Func<IExternalProxy, string, string?, InternalDataStore, string?>?
        UpstreamProxyWinAuthTokenGenerator { get; set; }

    internal string? GenerateUpstreamProxyWinAuthToken(IExternalProxy proxy, string scheme, string? challenge,
        InternalDataStore data)
    {
        if (UpstreamProxyWinAuthTokenGenerator != null)
            return UpstreamProxyWinAuthTokenGenerator(proxy, scheme, challenge, data);

        // Negotiate/Kerberos require the service principal name of the proxy, not the bare host.
        var targetName = "HTTP/" + proxy.HostName;

        return challenge == null
            ? WinAuthHandler.GetInitialProxyAuthToken(targetName, scheme, data)
            : WinAuthHandler.GetFinalProxyAuthToken(targetName, challenge, data);
    }

    /// <summary>
    ///     Enable disable HTTP/2 support.
    ///     HTTP/2 is only ever used when both the client and the server negotiate it via TLS ALPN; there is no
    ///     cleartext (h2c) upgrade, and a client/server that does not support HTTP/2 transparently falls back to
    ///     HTTP/1.1.
    ///     Request/response header and body modification in BeforeRequest/BeforeResponse, chunked trailers,
    ///     interim (1xx) responses, and the synthetic-response APIs (Ok/Respond/Redirect/GenericResponse/
    ///     RespondStreaming) are all supported over HTTP/2, the same as over HTTP/1.x.
    ///     Not supported: HTTP/2 server push (the wire frames are transcoded but there is no public API to
    ///     originate a push) and cleartext h2c upgrade.
    ///     See the protocol support matrix on the wiki for exact, up-to-date HTTP/1.x/HTTP/2 feature coverage.
    /// </summary>
    public bool EnableHttp2 { get; set; } = true;

    /// <summary>
    ///     When <see langword="true"/>, the proxy accepts WebSocket-over-HTTP/2 connections from
    ///     clients (RFC 8441 extended CONNECT with <c>:protocol = websocket</c>) and advertises
    ///     <c>SETTINGS_ENABLE_CONNECT_PROTOCOL=1</c> to h2 clients. The proxy independently
    ///     negotiates with each origin: if the origin supports RFC 8441 the DATA frames are
    ///     relayed directly; otherwise a new HTTP/1.1 WebSocket upgrade is performed.
    ///     Default: <see langword="false"/> (must opt-in; demand measurement pending).
    /// </summary>
    public bool EnableRfc8441 { get; set; } = false;

    /// <summary>
    ///     Enable HTTP/3 (QUIC) support. When <see langword="true" />:
    ///     <list type="bullet">
    ///       <item>
    ///         <description>
    ///           Any <c>TransparentQuicProxyEndPoint</c> added to <see cref="ProxyEndPoints" /> is started as
    ///           a QUIC listener that accepts inbound HTTP/3 connections.
    ///         </description>
    ///       </item>
    ///       <item>
    ///         <description>
    ///           With <see cref="UpstreamHttpProtocol.Auto" /> (default), the proxy automatically uses HTTP/3
    ///           for outbound connections to origins whose Alt-Svc or HTTPS/SVCB capability is cached, falling
    ///           back to HTTP/2 then HTTP/1.1 on failure.
    ///         </description>
    ///       </item>
    ///     </list>
    ///     Requires MsQuic native library and a supported operating-system version
    ///     (<see cref="System.Net.Quic.QuicListener.IsSupported" />). Setting to <see langword="true" /> with
    ///     no <c>TransparentQuicProxyEndPoint</c> configured emits a warning and skips QUIC initialization.
    ///     Default: <see langword="false" /> (opt-in).
    ///     <para>
    ///         <b>Experimental:</b> HTTP/3 support has not yet completed the full interop/soak/fuzz gate
    ///         process. Suppress <c>TWP001</c> to opt in; the attribute is removed when the feature
    ///         graduates to stable.
    ///     </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
    public bool EnableHttp3 { get; set; } = false;

    private bool? _enableHttpsSvcbDnsDiscovery;

    /// <summary>
    ///     When <see langword="true" />, the proxy queries the configured DNS server for an HTTPS/SVCB RR
    ///     (DNS type 65) before each first connection to an uncached origin in
    ///     <see cref="Models.UpstreamHttpProtocol.Auto" /> mode. A positive result (ALPN <c>h3</c> found)
    ///     upgrades the outbound connection to HTTP/3 and caches the result for the record's TTL.
    ///     Negative results are cached for 1 minute to avoid a DNS round-trip on every request to
    ///     HTTP/2-only origins.
    ///     <para>
    ///         Defaults to <see langword="true" /> whenever <see cref="EnableHttp3" /> is
    ///         <see langword="true" />, because proactive SVCB discovery is required to use HTTP/3 on the
    ///         first connection to an origin (before any <c>Alt-Svc</c> header has been received and cached).
    ///         Set explicitly to <see langword="false" /> to disable DNS discovery even when HTTP/3 is
    ///         enabled — for example, when the configured DNS server is untrusted or unreachable.
    ///     </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
    public bool EnableHttpsSvcbDnsDiscovery
    {
        // When the caller has not explicitly set this flag, inherit EnableHttp3 so SVCB discovery
        // is automatically active for every proxy that opts into HTTP/3.
        get => _enableHttpsSvcbDnsDiscovery ?? EnableHttp3;
        set => _enableHttpsSvcbDnsDiscovery = value;
    }

    /// <summary>
    ///     When <see langword="true" />, enables RFC 9204 QPACK dynamic table encoding and decoding for
    ///     inbound HTTP/3 connections. Each connection gets its own <see cref="Http3.Qpack.QpackContext" />
    ///     with two independent 4096-byte tables (one inbound, one outbound). Defaults to
    ///     <see langword="false" /> (static-table-only); existing deployments are unaffected.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
    public bool EnableQpackDynamicTable { get; set; } = false;

    /// <summary>
    ///     DNS server endpoint used by <see cref="Http3.Dns.UdpSvcbDnsResolver" /> for HTTPS/SVCB
    ///     queries. Defaults to Google Public DNS (<c>8.8.8.8:53</c>). Override to use a corporate
    ///     resolver, or set to <c>127.0.0.1:53</c> only when a local recursive resolver is running
    ///     (the previous default was loopback, which silently disabled cold-path H3 discovery on
    ///     typical developer machines).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
    public IPEndPoint DnsServerEndPoint { get; set; } = new(System.Net.IPAddress.Parse("8.8.8.8"), 53);

    private Http3.Dns.IHttpsSvcbResolver? _httpsSvcbResolver;

    /// <summary>
    ///     Resolver used to perform HTTPS/SVCB DNS lookups when <see cref="EnableHttpsSvcbDnsDiscovery" />
    ///     is enabled. Defaults to <see cref="Http3.Dns.UdpSvcbDnsResolver" /> using
    ///     <see cref="DnsServerEndPoint" />. Replace with a mock in tests.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
    internal Http3.Dns.IHttpsSvcbResolver HttpsSvcbResolver
    {
        get => _httpsSvcbResolver ??= new Http3.Dns.UdpSvcbDnsResolver(DnsServerEndPoint);
        set => _httpsSvcbResolver = value;
    }

    /// <summary>
    ///     Should we check for certificate revocation during SSL authentication to servers
    ///     Note: If enabled can reduce performance. Defaults to false.
    /// </summary>
    public X509RevocationMode CheckCertificateRevocation { get; set; }

    /// <summary>
    ///     Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
    ///     Broken 100 continue implementations on server/client may cause problems if enabled.
    ///     Defaults to false.
    /// </summary>
    public bool Enable100ContinueBehaviour { get; set; }

    /// <summary>
    ///     When <see langword="true" />, the proxy immediately responds with a synthetic
    ///     <c>100 Continue</c> to any client request carrying <c>Expect: 100-continue</c>,
    ///     before forwarding the headers to the origin and without waiting for the origin
    ///     to respond. This breaks the strict handshake (client → proxy 100 → client body
    ///     → origin body) but prevents the deadlock that occurs with strict clients when
    ///     <see cref="Enable100ContinueBehaviour" /> is <see langword="false" /> (the default).
    ///     Has no effect when <see cref="Enable100ContinueBehaviour" /> is <see langword="true" />.
    ///     Default: <see langword="false" />.
    /// </summary>
    public bool CompatibilityMode100Continue { get; set; } = false;

    /// <summary>
    ///     Maximum decoded HTTP/2 header list size in bytes, using RFC 7541 accounting
    ///     (name.Length + value.Length + 32 per field). Requests or responses with a decoded
    ///     header list exceeding this limit will be refused with RST_STREAM(ENHANCE_YOUR_CALM)
    ///     (code 0xb). Set to 0 to disable the limit (not recommended).
    ///     Default: 65,536 (64 KiB). Advertised via SETTINGS_MAX_HEADER_LIST_SIZE.
    /// </summary>
    public int MaxDecodedHeaderListBytes { get; set; } = 64 * 1024;

    /// <summary>
    ///     The shared, immutable resource-bound snapshot (concurrent-stream cap, CONTINUATION
    ///     frame-count/wall-clock bounds, peer-initiated incomplete-stream-reset budget, and the
    ///     other limits described in <see cref="ProxyResourceLimits" />) consulted by the HTTP/2
    ///     relay so a single proxy-owned value governs both what is enforced and what is advertised
    ///     to each peer, rather than admitting purely against whatever the origin advertised.
    ///     Assign a new <see cref="ProxyResourceLimits" /> (constructed via
    ///     <see cref="ProxyResourceLimits.Create" />) to override the <see cref="ProxyResourceLimits.Default" />
    ///     values used otherwise.
    /// </summary>
    public ProxyResourceLimits ResourceLimits { get; set; } = ProxyResourceLimits.Default;

    /// <summary>
    ///     Maximum bytes the proxy will buffer for a single request or response body when
    ///     body buffering is required (body-read hooks, authentication retry, etc.). Bodies
    ///     larger than this limit are rejected with 413 (upstream request) or connection teardown
    ///     (upstream response). Set to 0 to disable the limit (not recommended).
    ///     Default: 4,194,304 (4 MiB).
    /// </summary>
    public int MaxBufferedBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    ///     Maximum WebSocket frame payload size in bytes that the proxy will accept during
    ///     frame-level interception (i.e. when <c>BeforeWebSocketFrame</c> has at least one
    ///     subscriber). Frames whose decoded payload exceeds this limit cause the WebSocket
    ///     connection to be closed with Close code 1009 (Message Too Big).
    ///     Raw-relay sessions (no <c>BeforeWebSocketFrame</c> subscriber) bypass this check
    ///     entirely and pass all frames through unvalidated.
    ///     Default: 16,777,216 (16 MiB).
    /// </summary>
    public int MaxWebSocketFramePayloadBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    ///     Pseudonym used in Via header fields appended to forwarded requests and responses
    ///     (RFC 9110 §7.6.3). Defaults to <c>"titanium-web-proxy"</c>. Set to an empty string
    ///     to disable Via header injection entirely. Loop detection uses this value: a request
    ///     arriving with this pseudonym already present in Via is refused with 508 Loop Detected.
    /// </summary>
    public string ViaHeaderPseudonym { get; set; } = "titanium-web-proxy";

    /// <summary>
    ///     Controls which HTTP version is declared to the origin server on the request line, independently of
    ///     the version the client declared to the proxy. Defaults to
    ///     <see cref="Models.OriginHttpVersionPolicy.PreserveClientVersion" />, which matches the proxy's
    ///     historical pass-through behavior exactly. Set to
    ///     <see cref="Models.OriginHttpVersionPolicy.NormalizeToHttp11" /> to let HTTP/1.0 clients share pooled,
    ///     persistent origin connections the same way HTTP/1.1 clients already do. This only changes the wire
    ///     version written to the origin request line - it never changes the client-facing
    ///     <see cref="Http.Request.HttpVersion" /> that event handlers observe, nor the version/persistence used
    ///     to write the response back to the client.
    /// </summary>
    public OriginHttpVersionPolicy OriginHttpVersionPolicy { get; set; } = OriginHttpVersionPolicy.PreserveClientVersion;

    /// <summary>
    ///     Should we enable the server connection pool. Defaults to true.
    ///     When connection pooling is enabled, instead of creating a new TCP connection to the server for each client TCP
    ///     connection, we check if an idle server connection is available in our cached pool. If a compatible connection
    ///     (same destination, scheme, upstream proxy, credentials and negotiated protocol) created from an earlier request
    ///     is available, we reuse it. Only connections that are safe to reuse under the HTTP protocol are pooled:
    ///     the response body must be fully received and the connection must be persistent (HTTP/1.1 keep-alive, or an
    ///     HTTP/1.0 connection that explicitly opted in via "Connection: keep-alive"). Connections whose response asked to
    ///     close, that failed, or that carry connection-oriented authentication state (WinAuth NTLM/Negotiate) or a
    ///     per-session client certificate are never returned to the shared pool.
    ///     The ConnectionTimeOutSeconds parameter determines the eviction time for inactive server connections.
    ///     This reduces TCP (and TLS) connection establishment cost, both in wall clock time and CPU cycles.
    ///     Set to false to force a fresh server connection for every client connection.
    /// </summary>
    public bool EnableConnectionPool { get; set; } = true;

    /// <summary>
    ///     Should we enable tcp server connection prefetching?
    ///     When enabled, as soon as we receive a client connection we concurrently initiate
    ///     corresponding server connection process using CONNECT hostname or SNI hostname on a separate task so that after
    ///     parsing client request
    ///     we will have the server connection immediately ready or in the process of getting ready.
    ///     If a server connection is available in cache then this prefetch task will immediately return with the available
    ///     connection from cache.
    ///     Defaults to true.
    /// </summary>
    public bool EnableTcpServerConnectionPrefetch { get; set; } = true;

    /// <summary>
    ///     Gets or sets a Boolean value that specifies whether server and client stream Sockets are using the Nagle algorithm.
    ///     Defaults to true, no nagle algorithm is used.
    /// </summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>
    ///     Seconds client/server connection are to be kept alive when waiting for read/write to complete.
    ///     This will also determine the pool eviction time when connection pool is enabled.
    ///     Default value is 60 seconds.
    /// </summary>
    public int ConnectionTimeOutSeconds { get; set; } = 60;

    /// <summary>
    ///     Seconds server connection are to wait for connection to be established.
    ///     Default value is 20 seconds.
    /// </summary>
    public int ConnectTimeOutSeconds { get; set; } = 20;

    /// <summary>
    ///     Seconds to wait for a client to finish sending the request line and headers, from the moment
    ///     this proxy starts reading a new request on the connection. Enforced with a linked
    ///     <see cref="System.Threading.CancellationTokenSource" /> around the request-line and header
    ///     read, not <c>Socket.ReceiveTimeout</c>: that property only bounds a single blocking
    ///     <c>Receive</c> call, not the asynchronous reads this proxy actually issues, so without this
    ///     deadline a client that opens a connection and trickles bytes arbitrarily slowly (or stops
    ///     sending entirely) after the first byte ties up a read loop indefinitely.
    ///     Default is 0 (disabled), matching every other deadline in this class - no per-session
    ///     override exists because there is no <see cref="EventArguments.SessionEventArgs" /> for this
    ///     request yet at the point this deadline applies.
    /// </summary>
    public int ClientHeaderTimeoutSeconds { get; set; }

    /// <summary>
    ///     Seconds to wait for the origin to send the response status line and headers after the
    ///     request has been sent. Enforced with a linked <see cref="System.Threading.CancellationTokenSource" />
    ///     (not Socket receive timeout alone). When the deadline elapses a
    ///     <see cref="Exceptions.ProxyTimeoutException" /> with
    ///     <see cref="Exceptions.ProxyTimeoutKind.ResponseHeader" /> is raised (and may be converted to
    ///     HTTP 504 before any response bytes have been committed to the client).
    ///     Default is 0 (disabled). WebSocket upgrades, Server-Sent Events, raw tunnels, and sessions
    ///     that already wrote a response status to the client are exempt; those waits use
    ///     <see cref="IdleReadTimeoutSeconds" /> when configured.
    ///     Per-session override: <see cref="EventArguments.SessionEventArgs.ResponseHeaderTimeout" />.
    /// </summary>
    public int ResponseHeaderTimeoutSeconds { get; set; }

    /// <summary>
    ///     Seconds of idle time allowed while reading from the origin (stalled header/body waits).
    ///     Applied via <c>CancelAfter</c> on the active read operation. Default is 0 (disabled).
    ///     Per-session override: <see cref="EventArguments.SessionEventArgs.IdleReadTimeout" />.
    /// </summary>
    public int IdleReadTimeoutSeconds { get; set; }

    /// <summary>
    ///     Seconds of idle time allowed while writing to the origin (stalled header/body waits).
    ///     Applied via <c>CancelAfter</c> on the active write operation. Default is 0 (disabled).
    ///     Per-session override: <see cref="EventArguments.SessionEventArgs.IdleWriteTimeout" />.
    /// </summary>
    public int IdleWriteTimeoutSeconds { get; set; }

    /// <summary>
    ///     Total seconds allowed for a single request/response exchange after
    ///     <see cref="BeforeRequest" /> returns (connect, send, wait for headers, and body copy).
    ///     Default is 0 (disabled). Per-session override:
    ///     <see cref="EventArguments.SessionEventArgs.RequestTimeout" />.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>
    ///     Maximum number of concurrent connections per remote host in cache.
    ///     Only meaningful when <see cref="EnableConnectionPool" /> is <see langword="true" />; to
    ///     disable pooling, set <see cref="EnableConnectionPool" /> to <see langword="false" /> rather
    ///     than setting this to 0 - the pool eviction loop treats a value below 1 as "evict without
    ///     limit while holding the pool-wide lock", which spins indefinitely once the cache for that
    ///     host is empty and would stall every other connection acquire/release in the process.
    ///     Rejected outright at assignment so that state cannot be reached.
    ///     Default value is 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is less than 1.</exception>
    public int MaxCachedConnections
    {
        get => maxCachedConnections;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MaxCachedConnections must be at least 1. To disable connection pooling, set " +
                    nameof(EnableConnectionPool) + " to false instead.");

            maxCachedConnections = value;
        }
    }

    private int maxCachedConnections = 4;

    /// <summary>
    ///     SO_LINGER timeout in seconds applied to client and upstream sockets via
    ///     <see cref="LingerOption" /> (enabled with this timeout).
    ///     This is <b>not</b> the kernel TCP TIME_WAIT duration — TIME_WAIT is controlled by the OS.
    ///     A positive value means <c>Close</c> may block up to that many seconds flushing send buffers;
    ///     use 0 for an abortive close (RST). Default is 0 so high-churn proxies avoid TIME_WAIT
    ///     accumulation; the 1-second connection disposal delay already prefers peer-first close.
    /// </summary>
    public int TcpTimeWaitSeconds { get; set; } = 0;

    /// <summary>
    ///     Enable TCP KeepAlive on client and server sockets so NAT/firewall mappings for
    ///     long-lived CONNECT tunnels are refreshed. Default: true.
    /// </summary>
    public bool EnableTcpKeepAlive { get; set; } = true;

    /// <summary>
    ///     TCP listener accept backlog. Default: 512 for burst connection handling.
    /// </summary>
    public int ListenerBackLog { get; set; } = 512;

    /// <summary>
    ///     Should we reuse client/server tcp sockets.
    ///     Default is true (disabled for linux/macOS due to bug in .Net core).
    /// </summary>
    public bool ReuseSocket { get; set; } = true;

    /// <summary>
    ///     Total number of active TCP client connections.
    ///     Does not include inbound HTTP/3 (QUIC) clients; see <see cref="Http3ClientConnectionCount" />.
    /// </summary>
    public int ClientConnectionCount => clientConnectionCount;

    /// <summary>
    ///     Total number of active server connections (TCP plus upstream QUIC).
    ///     For HTTP/3-only upstreams see <see cref="Http3ServerConnectionCount" />.
    /// </summary>
    public int ServerConnectionCount => serverConnectionCount;

    /// <summary>
    ///     Total number of active inbound HTTP/3 (QUIC) client connections.
    /// </summary>
    public int Http3ClientConnectionCount => http3ClientConnectionCount;

    /// <summary>
    ///     Total number of active upstream HTTP/3 (QUIC) server connections.
    ///     These are also included in <see cref="ServerConnectionCount" />.
    /// </summary>
    public int Http3ServerConnectionCount => http3ServerConnectionCount;

    /// <summary>
    ///     Maximum number of client connections admitted across all TCP-based endpoints at once.
    ///     <see langword="null" /> (the default) disables the global admission gate, preserving today's
    ///     unbounded behavior. When set, a connection beyond this limit is rejected and disposed
    ///     immediately after accept, before a handler task is even started.
    ///     <para>
    ///         Enforced independently of <see cref="ClientConnectionCount" />: see
    ///         <see cref="admittedClientConnectionCount" /> for why. See also
    ///         <see cref="ProxyEndPoint.MaxConcurrentClients" /> for a per-endpoint cap layered on top of
    ///         this global one.
    ///     </para>
    /// </summary>
    public int? MaxConcurrentClientConnections { get; set; }

    /// <summary>
    ///     Number of client connections currently admitted (accepted and past the admission gate, not
    ///     yet finished being handled), across all TCP-based endpoints. Unlike
    ///     <see cref="ClientConnectionCount" />, this drops to zero as soon as the handler returns,
    ///     without the trailing TIME_WAIT delay.
    /// </summary>
    public int AdmittedClientConnectionCount => Volatile.Read(ref admittedClientConnectionCount);

    /// <summary>
    ///     Total number of client connections rejected by <see cref="MaxConcurrentClientConnections" />
    ///     since this instance was created.
    /// </summary>
    public long GlobalAdmissionRejectionCount => Interlocked.Read(ref globalAdmissionRejectionCount);

    /// <summary>
    ///     Total number of client connections rejected by any endpoint's
    ///     <see cref="ProxyEndPoint.MaxConcurrentClients" /> since this instance was created.
    /// </summary>
    public long EndpointAdmissionRejectionCount => Interlocked.Read(ref endpointAdmissionRejectionCount);

    /// <summary>
    ///     Realm used during Proxy Basic Authentication.
    /// </summary>
    public string ProxyAuthenticationRealm { get; set; } = "TitaniumProxy";

    /// <summary>
    ///     List of supported Ssl versions.
    ///     <para>
    ///         Defaults to TLS 1.2/1.3 only as of 5.0 - a breaking change from 4.x, which also enabled
    ///         SSL 3.0/TLS 1.0/1.1. Those legacy, broken-by-design protocols require an explicit opt-in
    ///         by assigning this property directly (e.g. <c>SslProtocols.Tls | SslProtocols.Tls11 |
    ///         SslProtocols.Tls12 | SslProtocols.Tls13</c>) if a legacy client/server genuinely requires
    ///         them.
    ///     </para>
    /// </summary>
    public SslProtocols SupportedSslProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    ///     List of supported Server Ssl versions.
    ///     Using SslProtocol.None means to require the same SSL protocol as the proxy client.
    /// </summary>
    public SslProtocols SupportedServerSslProtocols { get; set; } = SslProtocols.None;

    /// <summary>
    ///     Outbound destination policy hook: when <see langword="true" />, every resolved destination
    ///     IP address is checked against loopback, private (RFC 1918/4193), link-local (which subsumes
    ///     the 169.254.169.254 cloud metadata endpoint), and other non-globally-routable ranges before
    ///     connecting, and the connection attempt is rejected with an
    ///     <see cref="Exceptions.OutboundDestinationBlockedException" /> if it matches.
    ///     <para>
    ///         Off by default: blocking private destinations would break this library's most common
    ///         configurations, including upstream-proxy chaining to <c>localhost</c> and interception of
    ///         local development servers. Only enable this when the proxy accepts requests from
    ///         untrusted clients (an SSRF-relevant deployment), where those same destinations become an
    ///         attacker-reachable pivot into the host's private network instead of an operator's own
    ///         intentional configuration.
    ///     </para>
    ///     <para>
    ///         An explicitly configured upstream proxy address (<see cref="UpStreamHttpProxy" />,
    ///         <see cref="UpStreamHttpsProxy" />, or a per-session external proxy) is always exempt -
    ///         that address is operator intent, not attacker-controlled. Checked against the resolved
    ///         address actually used to connect (no re-resolution afterward, which would make the check
    ///         a TOCTOU no-op against DNS rebinding). Not currently enforced for a SOCKS upstream with
    ///         <c>ProxyDnsRequests</c> enabled, since the proxy never resolves the origin itself in that
    ///         mode and has no address of its own to validate.
    ///     </para>
    /// </summary>
    public bool BlockPrivateNetworkDestinations { get; set; }

    /// <summary>
    ///     Which resource-bound <see cref="PolicyFamily" /> is enforced, observed, or disabled, per
    ///     the plan's rollout section. Read live by each family's enforcement call site - not baked
    ///     into a per-request snapshot at connection accept time - so assigning a new value here (a
    ///     whole-object replacement, never a mutation of the previous instance) takes effect for the
    ///     next check any in-flight or new request makes, without restarting the proxy. This is the
    ///     "runtime switch to drop to Observe without redeploying" the plan requires; see
    ///     <see cref="ProxyPolicyModes.WithAllObservedExceptDisabled" /> for the one-call way to do that.
    ///     <para>
    ///         Defaults to <see cref="ProxyPolicyModes.AllEnforce" />, matching <see cref="ProxyProfile.Balanced" />.
    ///         Assigning <see cref="Profile" /> also replaces this value with that profile's bundle;
    ///         assign <see cref="PolicyModes" /> afterward to deviate from the selected profile's modes
    ///         without changing anything else the profile set.
    ///     </para>
    /// </summary>
    public ProxyPolicyModes PolicyModes
    {
        get => policyModes;
        set => policyModes = value ?? throw new ArgumentNullException(nameof(value));
    }

    private ProxyPolicyModes policyModes = ProxyPolicyModes.AllEnforce;

    /// <summary>
    ///     The last profile applied via this property's setter, defaulting to
    ///     <see cref="ProxyProfile.Balanced" /> - the profile every field on this instance already
    ///     starts at, so a fresh <c>new ProxyServer()</c> reports <see cref="ProxyProfile.Balanced" />
    ///     without needing its setter to run once at construction time.
    ///     <para>
    ///         Assigning this property applies its entire <see cref="ProxyProfileSettings" /> bundle -
    ///         <see cref="ResourceLimits" />, <see cref="PolicyModes" />, <see cref="SupportedSslProtocols" />,
    ///         <see cref="BlockPrivateNetworkDestinations" />, <see cref="MaxConcurrentClientConnections" />
    ///         and the deadline-seconds properties - as a single atomic assignment, so a reader can
    ///         never observe a half-applied profile. Assigning any of those properties individually
    ///         afterward overrides just that one, without reverting the rest of the profile's bundle.
    ///     </para>
    ///     <para>
    ///         Logged once per <see cref="Start" /> call, by name only - never with hosts, URLs or
    ///         secrets, per the plan's rollout section.
    ///     </para>
    /// </summary>
    public ProxyProfile Profile
    {
        get => profile;
        set
        {
            var settings = ProxyProfileSettings.For(value);
            ResourceLimits = settings.ResourceLimits;
            policyModes = settings.PolicyModes;
            SupportedSslProtocols = settings.SupportedSslProtocols;
            BlockPrivateNetworkDestinations = settings.BlockPrivateNetworkDestinations;
            MaxConcurrentClientConnections = settings.MaxConcurrentClientConnections;
            ClientHeaderTimeoutSeconds = settings.ClientHeaderTimeoutSeconds;
            ResponseHeaderTimeoutSeconds = settings.ResponseHeaderTimeoutSeconds;
            IdleReadTimeoutSeconds = settings.IdleReadTimeoutSeconds;
            IdleWriteTimeoutSeconds = settings.IdleWriteTimeoutSeconds;
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds;
            profile = value;
        }
    }

    private ProxyProfile profile = ProxyProfile.Balanced;

    /// <summary>
    ///     The buffer pool used throughout this proxy instance.
    ///     Set custom implementations by implementing this interface.
    ///     By default this uses DefaultBufferPool implementation available in StreamExtended library package.
    ///     Buffer size should be at least 10 bytes.
    /// </summary>
    public IBufferPool BufferPool { get; set; }

    /// <summary>
    ///     Manages certificates used by this proxy.
    /// </summary>
    public CertificateManager CertificateManager { get; }

    /// <summary>
    ///     External proxy used for Http requests.
    /// </summary>
    public IExternalProxy? UpStreamHttpProxy { get; set; }

    /// <summary>
    ///     External proxy used for Https requests.
    /// </summary>
    public IExternalProxy? UpStreamHttpsProxy { get; set; }

    /// <summary>
    ///     Local adapter/NIC endpoint where proxy makes request via.
    ///     Defaults via any IP addresses of this machine.
    ///     When the resolved destination address family does not match this endpoint, it is
    ///     ignored so dual-stack destinations can still connect (see
    ///     <see cref="UpStreamEndPointIPv4" /> / <see cref="UpStreamEndPointIPv6" />).
    /// </summary>
    public IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     Local bind endpoint used when the resolved upstream destination is IPv4.
    ///     Takes precedence over <see cref="UpStreamEndPoint" /> for IPv4 destinations.
    /// </summary>
    public IPEndPoint? UpStreamEndPointIPv4 { get; set; }

    /// <summary>
    ///     Local bind endpoint used when the resolved upstream destination is IPv6.
    ///     Takes precedence over <see cref="UpStreamEndPoint" /> for IPv6 destinations.
    /// </summary>
    public IPEndPoint? UpStreamEndPointIPv6 { get; set; }

    /// <summary>
    ///     A list of IpAddress and port this proxy is listening to.
    /// </summary>
    public List<ProxyEndPoint> ProxyEndPoints { get; set; }

    /// <summary>
    ///     A callback to provide authentication credentials for up stream proxy this proxy is using for HTTP(S) requests.
    ///     User should return the ExternalProxy object with valid credentials.
    /// </summary>
    public Func<SessionEventArgsBase, Task<IExternalProxy?>>? GetCustomUpStreamProxyFunc { get; set; }

    /// <summary>
    ///     A callback to provide a chance for an upstream proxy failure to be handled by a new upstream proxy.
    ///     User should return the ExternalProxy object with valid credentials or null.
    /// </summary>
    public Func<SessionEventArgsBase, Task<IExternalProxy?>>? CustomUpStreamProxyFailureFunc { get; set; }

    /// <summary>
    ///     Configuration for this proxy instance's built-in diagnostic logging - the replacement for the
    ///     removed <c>ExceptionFunc</c> callback. Every exception the proxy catches (even when handled
    ///     internally and never surfaced to user code) is reported through this logger at an appropriate
    ///     severity; see <see cref="ProxyLoggingOptions" /> for the console/file sinks, enable/disable
    ///     switch, and minimum level.
    ///     Mutate the returned instance (or assign a new one) at any point; each assignment/mutation you
    ///     want to take effect must be followed by <see cref="ApplyLoggingConfiguration" /> (which
    ///     <see cref="Start" /> also calls automatically, so the configuration active at the moment the
    ///     proxy starts running is picked up for the run even if you never call it yourself). Calling it
    ///     again later - including while the proxy is already running - immediately swaps in the new
    ///     configuration; this is safe because logging never blocks or otherwise affects proxy traffic.
    /// </summary>
    public ProxyLoggingOptions Logging
    {
        get => loggingOptions;
        set => loggingOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     The live, shared logger used throughout this proxy instance. Reflects the most recent call to
    ///     <see cref="ApplyLoggingConfiguration" />.
    /// </summary>
    public ILogger Logger => logger;

    /// <summary>
    ///     Rebuilds the active logger/logger factory from the current <see cref="Logging" />
    ///     configuration, disposing any previously owned built-in providers. Called automatically from
    ///     the constructor (with the default configuration) and from <see cref="Start" />. Call this
    ///     explicitly any time after changing <see cref="Logging" /> and you want the change to take
    ///     effect immediately - whether the proxy is stopped (e.g. before using
    ///     <see cref="CertificateManager" /> directly) or already running.
    /// </summary>
    public void ApplyLoggingConfiguration()
    {
        var options = loggingOptions;

        var previousFactory = activeLoggerFactory;
        var previousFactoryOwned = ownsActiveLoggerFactory;

        if (!options.Enabled)
        {
            activeLoggerFactory = NullLoggerFactory.Instance;
            ownsActiveLoggerFactory = false;
        }
        else if (options.LoggerFactory != null)
        {
            activeLoggerFactory = options.LoggerFactory;
            ownsActiveLoggerFactory = false;
        }
        else
        {
            var factory = new global::Titanium.Web.Proxy.Logging.ProxyLoggerFactory(options.MinimumLevel);
            if (options.EnableConsole) factory.AddProvider(new ConsoleLoggerProvider(options));
            if (options.EnableFile) factory.AddProvider(new RollingFileLoggerProvider(options));
            activeLoggerFactory = factory;
            ownsActiveLoggerFactory = true;
        }

        logger = activeLoggerFactory.CreateLogger("Titanium.Web.Proxy");
        ProxyDiagnostics.FallbackLogger = logger;

        // CertificateManager may not exist yet on the very first call from the constructor.
        if (CertificateManager != null) CertificateManager.Logger = logger;

        if (previousFactoryOwned && previousFactory != activeLoggerFactory)
            try
            {
                previousFactory.Dispose();
            }
            catch
            {
                // A misbehaving sink must never prevent the logger from being replaced.
            }
    }

    /// <summary>
    ///     Enables structured request/connection timing capture. When <see langword="false" /> (the
    ///     default) no timing objects are allocated and no <see cref="DateTime.UtcNow" /> calls are made
    ///     for timing purposes anywhere in the proxy, so there is zero overhead on the hot path.
    ///     <para>
    ///         When enabled, every <see cref="SessionEventArgsBase" /> exposes a populated
    ///         <see cref="SessionEventArgsBase.Timing" /> (per-request phases: client header read,
    ///         connection wait, request send, time-to-first-byte, response delivery, total), every
    ///         upstream connection exposes a populated <c>UpstreamConnectionTiming</c> (reachable from a
    ///         session via <see cref="SessionEventArgsBase.UpstreamConnectionTiming" />, describing DNS,
    ///         TCP connect, optional upstream-proxy CONNECT, and TLS handshake durations), and a decrypted
    ///         <see cref="EventArguments.TunnelConnectSessionEventArgs" /> exposes the client-facing TLS
    ///         handshake duration via <see cref="EventArguments.TunnelConnectSessionEventArgs.ClientTlsTiming" />.
    ///     </para>
    ///     <para>
    ///         Can be toggled at any time; it only affects sessions/connections created after the change,
    ///         never mutating timing objects already handed out. Defaults to <see langword="false" />.
    ///     </para>
    /// </summary>
    public bool EnableRequestTimingCapture { get; set; }

    /// <summary>
    ///     A callback to authenticate proxy clients via basic authentication.
    ///     Parameters are username and password as provided by client.
    ///     Should return true for successful authentication.
    /// </summary>
    public Func<SessionEventArgsBase?, string, string, Task<bool>>? ProxyBasicAuthenticateFunc { get; set; }

    /// <summary>
    ///     A pluggable callback to authenticate clients by scheme instead of requiring basic authentication through
    ///     ProxyBasicAuthenticateFunc.
    ///     Parameters are current working session, schemeType, and token as provided by a calling client.
    ///     Should return success for successful authentication, continuation if the package requests, or failure.
    /// </summary>
    public Func<SessionEventArgsBase, string, string, Task<ProxyAuthenticationContext>>? ProxySchemeAuthenticateFunc
    {
        get;
        set;
    }

    /// <summary>
    ///     A collection of scheme types, e.g. basic, NTLM, Kerberos, Negotiate, to return if scheme authentication is
    ///     required.
    ///     Works in relation with ProxySchemeAuthenticateFunc.
    /// </summary>
    public IEnumerable<string> ProxyAuthenticationSchemes { get; set; } = new string[0];

    /// <summary>
    ///     Event occurs when client connection count changed.
    /// </summary>
    public event EventHandler? ClientConnectionCountChanged;

    /// <summary>
    ///     Event occurs when server connection count changed.
    /// </summary>
    public event EventHandler? ServerConnectionCountChanged;

    /// <summary>
    ///     Event occurs when inbound HTTP/3 client connection count changed.
    /// </summary>
    public event EventHandler? Http3ClientConnectionCountChanged;

    /// <summary>
    ///     Event occurs when upstream HTTP/3 server connection count changed.
    /// </summary>
    public event EventHandler? Http3ServerConnectionCountChanged;

    /// <summary>
    ///     Event to override the default verification logic of remote SSL certificate received during authentication.
    /// </summary>
    public event AsyncEventHandler<CertificateValidationEventArgs>? ServerCertificateValidationCallback;

    /// <summary>
    ///     Event to override client certificate selection during mutual SSL authentication.
    /// </summary>
    public event AsyncEventHandler<CertificateSelectionEventArgs>? ClientCertificateSelectionCallback;

    /// <summary>
    ///     Intercept request event to server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? BeforeRequest;

    /// <summary>
    ///     Intercept request body send event to server.
    ///     Subscribe to inspect or modify the request body chunk-by-chunk as it streams to the server,
    ///     without buffering the whole body. Do not combine with SessionEventArgs.GetRequestBody (which buffers).
    /// </summary>
    public event AsyncEventHandler<BeforeBodyWriteEventArgs>? OnRequestBodyWrite;

    /// <summary>
    ///     Intercept response event from server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? BeforeResponse;

    /// <summary>
    ///     Intercept response body send event to client.
    ///     Subscribe to inspect or modify the response body chunk-by-chunk as it streams to the client,
    ///     without buffering the whole body. Do not combine with SessionEventArgs.GetResponseBody (which buffers).
    /// </summary>
    public event AsyncEventHandler<BeforeBodyWriteEventArgs>? OnResponseBodyWrite;

    internal bool HasOnRequestBodyWriteSubscribers => OnRequestBodyWrite != null;
    internal bool HasOnResponseBodyWriteSubscribers => OnResponseBodyWrite != null;

    internal Task InvokeOnRequestBodyWriteAsync(object sender, BeforeBodyWriteEventArgs args) =>
        OnRequestBodyWrite?.Invoke(sender, args) ?? Task.CompletedTask;

    internal Task InvokeOnResponseBodyWriteAsync(object sender, BeforeBodyWriteEventArgs args) =>
        OnResponseBodyWrite?.Invoke(sender, args) ?? Task.CompletedTask;

    /// <summary>
    ///     Intercept after response event from server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? AfterResponse;

    /// <summary>
    ///     Customize TcpClient used for client connection upon create.
    /// </summary>
    public event AsyncEventHandler<Socket>? OnClientConnectionCreate;

    /// <summary>
    ///     Customize TcpClient used for server connection upon create.
    /// </summary>
    public event AsyncEventHandler<Socket>? OnServerConnectionCreate;

    /// <summary>
    ///     Intercept connect request sent to upstream proxy.
    /// </summary>
    public event AsyncEventHandler<ConnectRequest>? BeforeUpStreamConnectRequest;

    /// <summary>
    ///     Customize the minimum ThreadPool size (increase it on a server)
    /// </summary>
    public int ThreadPoolWorkerThread { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     Add a proxy end point.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    public void AddEndPoint(ProxyEndPoint endPoint)
    {
        if (ProxyEndPoints.Any(x =>
                x.IpAddress.Equals(endPoint.IpAddress) && endPoint.Port != 0 && x.Port == endPoint.Port))
            throw new Exception("Cannot add another endpoint to same port & ip address");

        ProxyEndPoints.Add(endPoint);

        if (ProxyRunning && endPoint is TransparentQuicProxyEndPoint quicEndPoint)
        {
            quicListenerCts ??= new CancellationTokenSource();
            ListenQuic(quicEndPoint);
        }
        else if (ProxyRunning)
        {
            Listen(endPoint);
        }
    }

    /// <summary>
    ///     Remove a proxy end point.
    ///     Will throw error if the end point doesn't exist.
    /// </summary>
    /// <param name="endPoint">The existing endpoint to remove.</param>
    public void RemoveEndPoint(ProxyEndPoint endPoint)
    {
        if (ProxyEndPoints.Contains(endPoint) == false)
            throw new Exception("Cannot remove endPoints not added to proxy");

        ProxyEndPoints.Remove(endPoint);

        if (ProxyRunning && endPoint is TransparentQuicProxyEndPoint quicEndPoint)
            QuitListenQuic(quicEndPoint);
        else if (ProxyRunning)
            QuitListen(endPoint);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTP proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTPS proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType)
    {
        SetAsSystemProxy(endPoint, protocolType, null);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    /// <param name="settings">
    ///     The Windows system proxy settings, or <see langword="null"/> to preserve the current bypass list.
    /// </param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType,
        SystemProxySettings? settings)
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure you operating system to use this proxy's port and address.");

        ValidateEndPointAsSystemProxy(endPoint);

        // Validate bypass rules up front so a malformed rule cannot leave the proxy state half-applied.
        settings?.Validate();

        var isHttp = (protocolType & ProxyProtocolType.Http) > 0;
        var isHttps = (protocolType & ProxyProtocolType.Https) > 0;

        if (isHttps)
        {
            CertificateManager.EnsureRootCertificate();

            // If certificate was trusted by the machine
            if (!CertificateManager.CertValidated)
            {
                protocolType = protocolType & ~ProxyProtocolType.Https;
                isHttps = false;
            }
        }

        // clear any settings previously added
        if (isHttp) ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

        if (isHttps) ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpsProxy = false);

        string? proxyOverride = null;
        if (settings != null)
        {
            var currentProxyOverride = SystemProxySettingsManager.GetProxyInfoFromRegistry()?.ProxyOverride;
            proxyOverride = settings.BuildProxyOverride(currentProxyOverride);
        }

        SystemProxySettingsManager.SetProxy(
            Equals(endPoint.IpAddress, IPAddress.Any) |
            Equals(endPoint.IpAddress, IPAddress.Loopback)
                ? "localhost"
                : endPoint.IpAddress.ToString(),
            endPoint.Port,
            protocolType,
            proxyOverride);

        if (isHttp) endPoint.IsSystemHttpProxy = true;

        if (isHttps) endPoint.IsSystemHttpsProxy = true;

        string? proxyType = null;
        switch (protocolType)
        {
            case ProxyProtocolType.Http:
                proxyType = "HTTP";
                break;
            case ProxyProtocolType.Https:
                proxyType = "HTTPS";
                break;
            case ProxyProtocolType.AllHttp:
                proxyType = "HTTP and HTTPS";
                break;
        }

        if (protocolType != ProxyProtocolType.None)
            ProxyDiagnostics.ReportInformation(logger,
                $"Set endpoint at Ip {endPoint.IpAddress} and port: {endPoint.Port} as System {proxyType} Proxy");
    }

    /// <summary>
    ///     Clear HTTP proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Clear HTTPS proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpsProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Restores the original proxy settings.
    /// </summary>
    public void RestoreOriginalProxySettings()
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure your operating system to use this proxy's port and address.");

        SystemProxySettingsManager.RestoreOriginalSettings();

        ClearEndpointSystemProxyFlags(ProxyProtocolType.AllHttp);
    }

    /// <summary>
    ///     Clear the specified proxy setting for current machine.
    /// </summary>
    public void DisableSystemProxy(ProxyProtocolType protocolType)
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure your operating system to use this proxy's port and address.");

        SystemProxySettingsManager.RemoveProxy(protocolType);

        // Without this, an endpoint's IsSystemHttpProxy/IsSystemHttpsProxy stays true after the
        // corresponding registry setting has already been cleared, so a later SetAsSystemProxy call
        // for the other protocol - or Stop()'s own best-effort registry cleanup - can read stale flags
        // that no longer reflect the actual system proxy configuration.
        ClearEndpointSystemProxyFlags(protocolType);
    }

    /// <summary>
    ///     Clear all proxy settings for current machine.
    /// </summary>
    public void DisableAllSystemProxies()
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually confugure you operating system to use this proxy's port and address.");

        SystemProxySettingsManager.DisableAllProxy();

        ClearEndpointSystemProxyFlags(ProxyProtocolType.AllHttp);
    }

    /// <summary>
    ///     Clears <see cref="ExplicitProxyEndPoint.IsSystemHttpProxy" />/
    ///     <see cref="ExplicitProxyEndPoint.IsSystemHttpsProxy" /> on every endpoint for the protocol(s)
    ///     named in <paramref name="protocolType" />, so those flags never outlive the registry setting
    ///     they were tracking.
    /// </summary>
    private void ClearEndpointSystemProxyFlags(ProxyProtocolType protocolType)
    {
        var clearHttp = protocolType.HasFlag(ProxyProtocolType.Http);
        var clearHttps = protocolType.HasFlag(ProxyProtocolType.Https);
        if (!clearHttp && !clearHttps) return;

        foreach (var endPoint in ProxyEndPoints.OfType<ExplicitProxyEndPoint>())
        {
            if (clearHttp) endPoint.IsSystemHttpProxy = false;
            if (clearHttps) endPoint.IsSystemHttpsProxy = false;
        }
    }

    /// <summary>
    ///     Start this proxy server instance.
    ///     <para>
    ///         Transactional: if any endpoint fails to start, every listener this call already
    ///         started is stopped, the system-upstream-proxy resolver (if this call created one) is
    ///         disposed, and <see cref="ProxyRunning" /> is left <see langword="false" /> before the
    ///         exception propagates. A caller that catches the exception is left with an instance in
    ///         exactly the same state as before calling <see cref="Start" />, not a partially-bound
    ///         proxy with some endpoints silently listening.
    ///     </para>
    /// </summary>
    /// <param name="changeSystemProxySettings">
    ///     Whether or not clear any system proxy settings which is pointing to our own endpoint (causing a cycle).
    ///     E.g due to ungracious proxy shutdown before.
    /// </param>
    public void Start(bool changeSystemProxySettings = true)
    {
        if (ProxyRunning) throw new Exception("Proxy is already running.");

        // Freeze the active logging configuration for the duration of this run.
        ApplyLoggingConfiguration();

        SetThreadPoolMinThread(ThreadPoolWorkerThread);

        // Only create the root certificate when at least one endpoint will actually perform
        // TLS decryption and does not already have a custom GenericCertificate.  Endpoints
        // whose DecryptSsl is false never need to generate leaf certificates, so creating a
        // root PFX for them is unnecessary I/O and key-generation work.
        if (ProxyEndPoints.Any(x => x.DecryptSsl && x.GenericCertificate == null))
            CertificateManager.EnsureRootCertificate();

        if (changeSystemProxySettings && SystemProxySettingsManager != null && RunTime.IsWindows &&
            !RunTime.IsUwpOnWindows)
        {
            var proxyInfo = SystemProxySettingsManager.GetProxyInfoFromRegistry();
            if (proxyInfo?.Proxies != null)
            {
                var protocolToRemove = ProxyProtocolType.None;
                foreach (var proxy in proxyInfo.Proxies.Values)
                    if (NetworkHelper.IsLocalIpAddress(proxy.HostName)
                        && ProxyEndPoints.Any(x => x.Port == proxy.Port))
                        protocolToRemove |= proxy.ProtocolType;

                if (protocolToRemove != ProxyProtocolType.None)
                    SystemProxySettingsManager.RemoveProxy(protocolToRemove, false);
            }
        }

        var assignedSystemUpStreamResolver = false;
        if (RunTime.IsWindows && ForwardToUpstreamGateway && GetCustomUpStreamProxyFunc == null &&
            SystemProxySettingsManager != null)
        {
            systemProxyResolver = new WinHttpWebProxyFinder();
            if (UpstreamProxyConfigurationScript != null)
                //Use the provided proxy configuration script
                systemProxyResolver.UsePacFile(UpstreamProxyConfigurationScript);
            else
                // Use WinHttp to handle PAC/WAPD scripts.
                systemProxyResolver.LoadFromIe();

            GetCustomUpStreamProxyFunc = GetSystemUpStreamProxy;
            assignedSystemUpStreamResolver = true;
        }

        ProxyRunning = true;

        // Name only, per the plan's rollout section - never hosts, URLs or secrets.
        ProxyLog.EffectiveProfileAtStartup(logger, profile, policyModes);

        CertificateManager.ClearIdleCertificates();

        var startedTcpEndPoints = new List<ProxyEndPoint>();
        var startedQuicEndPoints = new List<TransparentQuicProxyEndPoint>();
        var createdQuicListenerCts = false;

        try
        {
            if (EnableHttp3 && ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>().Any())
            {
                quicListenerCts = new CancellationTokenSource();
                createdQuicListenerCts = true;
                foreach (var quicEndPoint in ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>())
                {
                    ListenQuic(quicEndPoint);
                    startedQuicEndPoints.Add(quicEndPoint);
                }
            }
            else if (EnableHttp3)
            {
                Logger.LogWarning(
                    "EnableHttp3 is true but no TransparentQuicProxyEndPoint is registered. " +
                    "Add a TransparentQuicProxyEndPoint to ProxyEndPoints before calling Start().");
            }

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
                startedTcpEndPoints.Add(endPoint);
            }
        }
        catch (Exception)
        {
            // Roll back, in reverse dependency order, everything this call already started.
            // QuitListen/QuitListenQuic tolerate a listener that never started (no-op), so it is
            // safe to call them uniformly rather than re-deriving exactly how far each one got.
            foreach (var quicEndPoint in startedQuicEndPoints) SafeRollback(() => QuitListenQuic(quicEndPoint));
            foreach (var endPoint in startedTcpEndPoints) SafeRollback(() => QuitListen(endPoint));

            if (createdQuicListenerCts)
            {
                SafeRollback(() => quicListenerCts?.Cancel());
                SafeRollback(() => quicListenerCts?.Dispose());
                quicListenerCts = null;
            }

            if (assignedSystemUpStreamResolver)
            {
                if (OperatingSystem.IsWindows())
                    try
                    {
                        systemProxyResolver?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        OnException(null, ex);
                    }

                systemProxyResolver = null;
                GetCustomUpStreamProxyFunc = null;
            }

            ProxyRunning = false;

            throw;
        }
    }

    /// <summary>
    ///     Runs a single <see cref="Start" /> rollback step, reporting rather than propagating a
    ///     failure so one misbehaving teardown step cannot mask the original failure or abandon the
    ///     rest of the rollback.
    /// </summary>
    private void SafeRollback(Action rollbackStep)
    {
        try
        {
            rollbackStep();
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Stop this proxy server instance.
    ///     Endpoints remain registered so <see cref="Start" /> can re-listen on the same ports.
    ///     In-flight sessions are cancelled; pooled upstream connections are cleared. The connection
    ///     factory itself stays usable for a subsequent Start (it is only disposed with the proxy).
    /// </summary>
    public void Stop()
    {
        StopCore(cancelSessions: true, clearPools: true);
    }

    /// <summary>
    ///     Asynchronously stop this proxy server, cancel in-flight sessions, and wait briefly for
    ///     client connection count to drain before clearing the upstream pool.
    /// </summary>
    /// <param name="drainTimeout">
    ///     Maximum time to wait for active client handlers to exit after cancellation.
    ///     Defaults to 5 seconds.
    /// </param>
    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        if (!ProxyRunning) throw new Exception("Proxy is not running.");

        StopCore(cancelSessions: true, clearPools: false);

        var timeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + timeout;
        // Http3ClientConnectionCount tracks inbound QUIC clients separately from
        // ClientConnectionCount (TCP-based H1/H2); draining only the former would let this
        // return, and the pools below get cleared, while HTTP/3 streams are still in flight.
        while ((ClientConnectionCount > 0 || Http3ClientConnectionCount > 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);

        TcpConnectionFactory.ClearPools();
        await QuicConnectionPool.DrainAsync();
    }

    private void StopCore(bool cancelSessions, bool clearPools)
    {
        if (!ProxyRunning) throw new Exception("Proxy is not running.");

        if (RunTime.IsWindows && SystemProxySettingsManager != null)
        {
            var systemProxyEndPoints = ProxyEndPoints.OfType<ExplicitProxyEndPoint>()
                .Where(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy)
                .ToList();

            if (systemProxyEndPoints.Count > 0)
            {
                SystemProxySettingsManager.RestoreOriginalSettings();
                foreach (var endPoint in systemProxyEndPoints)
                {
                    endPoint.IsSystemHttpProxy = false;
                    endPoint.IsSystemHttpsProxy = false;
                }
            }
        }

        // Prevent accept callbacks from scheduling another accept while listeners are stopping.
        ProxyRunning = false;

        if (cancelSessions) CancelActiveSessions();

        foreach (var endPoint in ProxyEndPoints) QuitListen(endPoint);

        // Cancel and wait for QUIC accept loops to exit.
        quicListenerCts?.Cancel();
        foreach (var quicEndPoint in ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>())
            QuitListenQuic(quicEndPoint);
        quicListenerCts?.Dispose();
        quicListenerCts = null;

        // Keep ProxyEndPoints so Start() can re-bind the same listeners (issue #799).

        CertificateManager?.StopClearIdleCertificates();

        if (clearPools) TcpConnectionFactory.ClearPools();
        if (clearPools) QuicConnectionPool.DrainAsync().AsTask().GetAwaiter().GetResult();

        // Start() may have wired GetCustomUpStreamProxyFunc to GetSystemUpStreamProxy and created
        // systemProxyResolver to back it. Undo both together: leaving the callback in place while
        // disposing its resolver below would make a subsequent Start() see GetCustomUpStreamProxyFunc
        // != null and skip creating a fresh resolver, so the callback would call into a disposed
        // WinHttpWebProxyFinder on the first request after restart. Only clear the callback if it is
        // still the delegate we assigned - a caller who has since replaced it with their own must not
        // have that overwritten here.
        if (Equals(GetCustomUpStreamProxyFunc, (Func<SessionEventArgsBase, Task<IExternalProxy?>>)GetSystemUpStreamProxy))
            GetCustomUpStreamProxyFunc = null;

        // Release the WinHTTP session handle acquired during Start() (Windows-only type).
        if (OperatingSystem.IsWindows())
            systemProxyResolver?.Dispose();
        systemProxyResolver = null;
    }

    internal void RegisterSessionCancellation(CancellationTokenSource cancellationTokenSource)
    {
        activeSessionCancellations.TryAdd(cancellationTokenSource, 0);
    }

    internal void UnregisterSessionCancellation(CancellationTokenSource cancellationTokenSource)
    {
        activeSessionCancellations.TryRemove(cancellationTokenSource, out _);
    }

    private void CancelActiveSessions()
    {
        foreach (var cts in activeSessionCancellations.Keys)
            try
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Session already tore down its CTS.
            }
    }

    /// <summary>
    ///     Listen on given end point of local machine.
    /// </summary>
    /// <param name="endPoint">The end point to listen.</param>
    private void Listen(ProxyEndPoint endPoint)
    {
        endPoint.Listener = new TcpListener(endPoint.IpAddress, endPoint.Port);

        if (ReuseSocket && RunTime.IsSocketReuseAvailable())
            endPoint.Listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            endPoint.Listener.Start(ListenerBackLog);

            endPoint.Port = ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port;

            // accept clients asynchronously
            endPoint.Listener.BeginAcceptSocket(OnAcceptConnection, endPoint);
        }
        catch (SocketException ex)
        {
            var pex = new Exception(
                $"Endpoint {endPoint} failed to start. Check inner exception and exception data for details.", ex);
            pex.Data.Add("ipAddress", endPoint.IpAddress);
            pex.Data.Add("port", endPoint.Port);
            throw pex;
        }
    }

    /// <summary>
    ///     Verify if its safe to set this end point as system proxy.
    /// </summary>
    /// <param name="endPoint">The end point to validate.</param>
    private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
    {
        if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));

        if (!ProxyEndPoints.Contains(endPoint))
            throw new Exception("Cannot set endPoints not added to proxy as system proxy");

        if (!ProxyRunning) throw new Exception("Cannot set system proxy settings before proxy has been started.");
    }

    /// <summary>
    ///     Gets the system up stream proxy.
    /// </summary>
    /// <param name="sessionEventArgs">The session.</param>
    /// <returns>The external proxy as task result.</returns>
    private Task<IExternalProxy?> GetSystemUpStreamProxy(SessionEventArgsBase sessionEventArgs)
    {
        if (!RunTime.IsWindows)
            throw new PlatformNotSupportedException("System upstream proxy discovery is only supported on Windows.");

        var proxy = systemProxyResolver!.GetProxy(sessionEventArgs.HttpClient.Request.RequestUri);
        return Task.FromResult(proxy);
    }

    /// <summary>
    ///     Act when a connection is received from client.
    /// </summary>
    private void OnAcceptConnection(IAsyncResult asyn)
    {
        var endPoint = (ProxyEndPoint)asyn.AsyncState!;
        var listener = endPoint.Listener!;

        Socket? tcpClient = null;
        var listenerDisposed = false;

        try
        {
            tcpClient = listener.EndAcceptSocket(asyn);
        }
        catch (ObjectDisposedException)
        {
            // The listener was Stop()'d, disposing the underlying socket and
            // triggering the completion of the callback. We're already exiting.
            listenerDisposed = true;
        }
        catch (Exception ex)
        {
            // Errors here (e.g. transient socket errors under heavy load) are
            // reported but must not prevent re-arming the accept loop below.
            OnException(null, ex);
        }

        // Re-arm the accept loop as early as possible (before dispatching the
        // just-accepted client) so bursts of near-simultaneous connections are
        // drained from the backlog without delay.
        if (!listenerDisposed) BeginAcceptConnection(endPoint, listener);

        if (tcpClient != null)
        {
            if (ProxyRunning)
            {
                // Gate before spending anything on this socket beyond the accept itself: a rejected
                // connection is disposed immediately, without a handler task ever being scheduled.
                if (!TryAdmitClientConnection(endPoint))
                {
                    tcpClient.Dispose();
                }
                else
                {
                    try
                    {
                        tcpClient.NoDelay = NoDelay;
                    }
                    catch (Exception ex)
                    {
                        OnException(null, ex);
                    }

                    var acceptedClient = tcpClient;
                    Task.Run(async () =>
                    {
                        // HandleClient runs detached (fire-and-forget); an unobserved exception here would
                        // otherwise never surface anywhere and, depending on the .NET unobserved-task-
                        // exception policy, could tear down the process. Always report it instead.
                        try
                        {
                            await HandleClient(acceptedClient, endPoint);
                        }
                        catch (Exception ex)
                        {
                            ProxyDiagnostics.ReportException(logger, "Unhandled exception while handling a client connection", ex);
                        }
                        finally
                        {
                            // Synchronous, unconditional release: never tied to the TIME_WAIT-delayed
                            // decrement in TcpClientConnection.Dispose, so a burst of short-lived
                            // connections cannot starve admission for a following burst.
                            ReleaseClientConnection(endPoint);
                        }
                    });
                }
            }
            else
                tcpClient.Dispose();
        }
    }

    /// <summary>
    ///     Admission gate evaluated synchronously before an accepted socket is dispatched to a handler
    ///     task. Checks the global cap first, then the endpoint-specific one; a rejection anywhere
    ///     rolls back only what this call itself admitted; released via
    ///     <see cref="ReleaseClientConnection" /> once the corresponding handler completes.
    /// </summary>
    private bool TryAdmitClientConnection(ProxyEndPoint endPoint)
    {
        var mode = policyModes[PolicyFamily.AdmissionControl];

        if (!TryAdmitGlobal(mode))
        {
            Interlocked.Increment(ref globalAdmissionRejectionCount);
            ProxyMetrics.ConnectionRejected("global limit");
            ProxyLog.ClientConnectionAdmissionRejected(logger, endPoint, "global limit");
            return false;
        }

        if (!endPoint.TryAdmitClient(mode))
        {
            ReleaseGlobal();
            Interlocked.Increment(ref endpointAdmissionRejectionCount);
            ProxyMetrics.ConnectionRejected("endpoint limit");
            ProxyLog.ClientConnectionAdmissionRejected(logger, endPoint, "endpoint limit");
            return false;
        }

        ProxyMetrics.ConnectionAdmitted();
        return true;
    }

    /// <summary>
    ///     Releases the admission slot acquired by a prior successful <see cref="TryAdmitClientConnection" />
    ///     call for the same endpoint. Must be called exactly once per admitted connection, regardless of
    ///     whether its handler completed normally or threw.
    /// </summary>
    private void ReleaseClientConnection(ProxyEndPoint endPoint)
    {
        ProxyMetrics.ConnectionReleased();
        ReleaseGlobal();
        endPoint.ReleaseClient();
    }

    /// <summary>
    ///     Enforces <see cref="MaxConcurrentClientConnections" /> per <paramref name="mode" />: under
    ///     <see cref="PolicyMode.Enforce" />, a breach returns <see langword="false" /> without
    ///     admitting; under <see cref="PolicyMode.Observe" />, the breach is recorded but the
    ///     connection is still admitted; under <see cref="PolicyMode.Disabled" />, the limit is not
    ///     consulted at all.
    /// </summary>
    private bool TryAdmitGlobal(PolicyMode mode)
    {
        while (true)
        {
            var current = Volatile.Read(ref admittedClientConnectionCount);
            if (mode != PolicyMode.Disabled && MaxConcurrentClientConnections is { } limit && current >= limit)
            {
                ProxyMetrics.PolicyBreach(PolicyFamily.AdmissionControl, mode);
                if (mode == PolicyMode.Enforce) return false;
            }

            if (Interlocked.CompareExchange(ref admittedClientConnectionCount, current + 1, current) == current)
                return true;
        }
    }

    private void ReleaseGlobal()
    {
        Interlocked.Decrement(ref admittedClientConnectionCount);
    }

    /// <summary>
    ///     (Re)arms the accept loop for the given end point.
    ///     Any exception thrown by <see cref="TcpListener.BeginAcceptSocket" /> (e.g. transient
    ///     resource exhaustion under heavy connection load) is caught and retried instead of being
    ///     allowed to escape the async I/O completion callback, which would otherwise crash the
    ///     process or silently stop the proxy from accepting any further connections.
    /// </summary>
    private void BeginAcceptConnection(ProxyEndPoint endPoint, TcpListener listener)
    {
        if (!ProxyRunning) return;

        try
        {
            listener.BeginAcceptSocket(OnAcceptConnection, endPoint);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            // The listener was Stop()'d, disposing the underlying socket and
            // triggering the completion of the callback. We're already exiting,
            // so just return.
        }
        catch (Exception ex)
        {
            OnException(null, ex);

            // Retry shortly instead of permanently abandoning the accept loop.
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                BeginAcceptConnection(endPoint, listener);
            });
        }
    }


    /// <summary>
    ///     Change the ThreadPool.WorkerThread minThread
    /// </summary>
    /// <param name="workerThreads">minimum Threads allocated in the ThreadPool</param>
    private void SetThreadPoolMinThread(int workerThreads)
    {
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out _);

        minWorkerThreads = Math.Min(maxWorkerThreads, Math.Max(workerThreads, Environment.ProcessorCount));

        ThreadPool.SetMinThreads(minWorkerThreads, minCompletionPortThreads);
    }


    /// <summary>
    ///     Handle the client.
    /// </summary>
    /// <param name="tcpClientSocket">The client socket.</param>
    /// <param name="endPoint">The proxy endpoint.</param>
    /// <returns>The task.</returns>
    private async Task HandleClient(Socket tcpClientSocket, ProxyEndPoint endPoint)
    {
        tcpClientSocket.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
        tcpClientSocket.SendTimeout = ConnectionTimeOutSeconds * 1000;

        tcpClientSocket.LingerState = new LingerOption(true, TcpTimeWaitSeconds);

        if (EnableTcpKeepAlive)
            tcpClientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        await InvokeClientConnectionCreateEvent(tcpClientSocket);

        using (var clientConnection = new TcpClientConnection(this, tcpClientSocket))
        {
            if (endPoint is ExplicitProxyEndPoint eep)
                await HandleClient(eep, clientConnection);
            else if (endPoint is TransparentProxyEndPoint tep)
                await HandleClient(tep, clientConnection);
            else if (endPoint is SocksProxyEndPoint sep) await HandleClient(sep, clientConnection);
        }
    }

    /// <summary>
    ///     Handle exception.
    /// </summary>
    /// <param name="clientStream">The client stream.</param>
    /// <param name="exception">The exception.</param>
    private void OnException(HttpClientStream? clientStream, Exception exception)
    {
        ProxyDiagnostics.ReportException(logger, "Unhandled exception in proxy", exception);
    }

    /// <summary>
    ///     Quit listening on the given end point.
    /// </summary>
    private void QuitListen(ProxyEndPoint endPoint)
    {
        var listener = endPoint.Listener;
        if (listener == null) return;

        listener.Stop();
        listener.Server.Dispose();
    }

    /// <summary>
    ///     Update client connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateClientConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref clientConnectionCount);
        else
            Interlocked.Decrement(ref clientConnectionCount);

        try
        {
            ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update server connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateServerConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref serverConnectionCount);
        else
            Interlocked.Decrement(ref serverConnectionCount);

        try
        {
            ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update inbound HTTP/3 client connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateHttp3ClientConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref http3ClientConnectionCount);
        else
            Interlocked.Decrement(ref http3ClientConnectionCount);

        try
        {
            Http3ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update upstream HTTP/3 server connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateHttp3ServerConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref http3ServerConnectionCount);
        else
            Interlocked.Decrement(ref http3ServerConnectionCount);

        try
        {
            Http3ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Invoke client tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="clientSocket">The TcpClient object.</param>
    /// <returns></returns>
    internal async Task InvokeClientConnectionCreateEvent(Socket clientSocket)
    {
        // client connection created
        if (OnClientConnectionCreate != null)
            await OnClientConnectionCreate.InvokeAsync(this, clientSocket, logger);
    }

    /// <summary>
    ///     Invoke server tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="serverSocket">The Socket object.</param>
    /// <returns></returns>
    internal async Task InvokeServerConnectionCreateEvent(Socket serverSocket)
    {
        // server connection created
        if (OnServerConnectionCreate != null)
            await OnServerConnectionCreate.InvokeAsync(this, serverSocket, logger);
    }

    /// <summary>
    ///     Connection retry policy when using connection pool.
    /// </summary>
    private RetryPolicy<T> RetryPolicy<T>() where T : Exception
    {
        return new RetryPolicy<T>(NetworkFailureRetryAttempts, TcpConnectionFactory);
    }

    /// <summary>
    ///     Connection retry policy that respects the per-session
    ///     <see cref="SessionEventArgs.NetworkFailureRetryAttempts" /> override when set.
    /// </summary>
    private RetryPolicy<T> RetryPolicy<T>(SessionEventArgs? sessionOverride) where T : Exception
    {
        var attempts = sessionOverride?.NetworkFailureRetryAttempts ?? NetworkFailureRetryAttempts;
        return new RetryPolicy<T>(attempts, TcpConnectionFactory);
    }

    private bool disposed;

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        // No finalizer: Stop()/certificate/buffer disposal must only run on the explicit
        // Dispose path. Callers that omit Dispose leave OS sockets to safe-handle cleanup.
        if (ProxyRunning)
            try
            {
                Stop();
            }
            catch
            {
                // ignore
            }

        try
        {
            TcpConnectionFactory.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            QuicConnectionPool.DrainAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        CertificateManager?.Dispose();
        BufferPool?.Dispose();

        // SystemProxyManager is [SupportedOSPlatform("windows")]; the platform analyzer cannot
        // prove that from a null-conditional access alone, so guard explicitly (mirrors the
        // Start() rollback path below).
        if (RunTime.IsWindows) SystemProxySettingsManager?.Dispose();

        if (ownsActiveLoggerFactory)
            try
            {
                activeLoggerFactory.Dispose();
            }
            catch
            {
                // A misbehaving sink must never prevent proxy disposal from completing.
            }
    }
}