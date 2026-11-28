using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Http3;
using Titanium.Quic;
using Titanium.Tls;

namespace Titanium.Net.Http;

/// <summary>
/// HTTP client that always uses the managed TLS stack.
/// HTTP/1.1 and HTTP/2 go through an inner handler whose connect callback owns the <see cref="TlsStream"/>.
/// HTTP/3 opens a managed QUIC connection and uses the HTTP/3 codec.
/// </summary>
/// <remarks>
/// The inner handler sees an <c>http</c> URI, so it does not open its own TLS stream.
/// Port 443 stays on that URI when the caller used the default HTTPS port.
/// <c>Host</c> stays the caller's host.
/// The inner handler sends <c>:scheme http</c> on HTTP/2, and there is no hook to change it.
/// A 400 or 421 on GET, HEAD, OPTIONS, or TRACE is retried once over HTTP/1.1 when the request allows a lower version.
/// A server that selects HTTP/1.1 during ALPN is retried over HTTP/1.1 before any request bytes, for any method, when a lower version is allowed.
/// The negotiated protocol is remembered per authority for ten minutes, up to 256 authorities.
/// HTTP/3 sends <c>:scheme https</c>. The response body is collected before <see cref="SendAsync"/> returns.
/// A failed HTTP/3 connect, before any request byte, falls back to HTTP/2 or HTTP/1.1 when the request allows a lower version.
/// </remarks>
public sealed class TitaniumHttpHandler : HttpMessageHandler
{
    private const int MaxAuthorities = 256;
    private const string FipsRefusal =
        "The managed TLS stack is disabled while FIPS mode is on. Set TlsStreamOptions.AllowManagedStackUnderFips or TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1.";

    /// <summary>Marks an inner request whose connect callback must authenticate a managed TLS stream.</summary>
    internal static readonly HttpRequestOptionsKey<bool> ManagedTlsOption = new("Titanium.Net.Http.ManagedTls");

    private readonly SocketsHttpHandler _sockets = new();
    private readonly HttpMessageInvoker _inner;
    private readonly ConcurrentDictionary<string, AlpnEntry> _alpn = new();
    private SslClientAuthenticationOptions _sslOptions = new();

    /// <summary>Creates a handler. The inner handler's connect callback owns the managed TLS stream.</summary>
    public TitaniumHttpHandler()
    {
        // The HTTP/3 codec stays referenced for the client that follows this adapter.
        _ = typeof(Http3FrameCodec).Assembly;
        _sockets.ConnectCallback = ConnectAsync;
        _inner = new HttpMessageInvoker(_sockets, disposeHandler: false);
    }

    /// <summary>Gets or sets the TLS options copied onto each managed handshake. The handler chooses ALPN.</summary>
    public SslClientAuthenticationOptions SslOptions
    {
        get => _sslOptions;
        set => _sslOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets or sets the fingerprint profile passed to <see cref="TlsStream"/>. Null uses the built-in hello.</summary>
    public TlsFingerprintProfile? FingerprintProfile { get; set; }

    /// <summary>Gets or sets the QUIC options used for an HTTP/3 connection. Null uses the defaults.</summary>
    public ManagedQuicClientConnectionOptions? QuicOptions { get; set; }

    /// <summary>Gets or sets whether a later request may use Alt-Svc. This handler does not read Alt-Svc yet.</summary>
    public bool EnableAltSvc { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="SendAsync"/> may run while FIPS mode is on.
    /// The environment variable <c>TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1</c> allows it as well.
    /// </summary>
    public bool AllowManagedStackUnderFips { get; set; }

    /// <summary>Gets or sets the proxy forwarded to the inner handler.</summary>
    public IWebProxy? Proxy
    {
        get => _sockets.Proxy;
        set => _sockets.Proxy = value;
    }

    /// <summary>Gets or sets whether the inner handler uses <see cref="Proxy"/>.</summary>
    public bool UseProxy
    {
        get => _sockets.UseProxy;
        set => _sockets.UseProxy = value;
    }

    /// <summary>Gets or sets the cookie container forwarded to the inner handler.</summary>
    public CookieContainer? CookieContainer
    {
        get => _sockets.CookieContainer;
        set => _sockets.CookieContainer = value;
    }

    /// <summary>Gets or sets whether the inner handler sends cookies.</summary>
    public bool UseCookies
    {
        get => _sockets.UseCookies;
        set => _sockets.UseCookies = value;
    }

    /// <summary>Gets or sets whether the inner handler follows redirects.</summary>
    public bool AllowAutoRedirect
    {
        get => _sockets.AllowAutoRedirect;
        set => _sockets.AllowAutoRedirect = value;
    }

    /// <summary>Gets or sets how many redirects the inner handler follows.</summary>
    public int MaxAutomaticRedirections
    {
        get => _sockets.MaxAutomaticRedirections;
        set => _sockets.MaxAutomaticRedirections = value;
    }

    /// <summary>Gets or sets the decompression methods forwarded to the inner handler.</summary>
    public DecompressionMethods AutomaticDecompression
    {
        get => _sockets.AutomaticDecompression;
        set => _sockets.AutomaticDecompression = value;
    }

    /// <summary>Gets or sets how long the inner handler keeps a pooled connection.</summary>
    public TimeSpan PooledConnectionLifetime
    {
        get => _sockets.PooledConnectionLifetime;
        set => _sockets.PooledConnectionLifetime = value;
    }

    /// <summary>Gets or sets how long the inner handler keeps an idle pooled connection.</summary>
    public TimeSpan PooledConnectionIdleTimeout
    {
        get => _sockets.PooledConnectionIdleTimeout;
        set => _sockets.PooledConnectionIdleTimeout = value;
    }

    /// <summary>Gets or sets how many connections the inner handler opens to one server.</summary>
    public int MaxConnectionsPerServer
    {
        get => _sockets.MaxConnectionsPerServer;
        set => _sockets.MaxConnectionsPerServer = value;
    }

    /// <summary>Gets or sets how long a TCP connect and the managed handshake may take. The default matches the inner handler.</summary>
    public TimeSpan ConnectTimeout
    {
        get => _sockets.ConnectTimeout;
        set => _sockets.ConnectTimeout = value;
    }

    /// <summary>
    /// Builds the request the inner handler sends.
    /// An <c>https</c> URI becomes <c>http</c> with an explicit port, and the managed-TLS option is set.
    /// </summary>
    /// <param name="request">The caller's request. This method does not modify it.</param>
    /// <param name="version">The version the inner handler must use.</param>
    /// <param name="policy">The version policy the inner handler must use.</param>
    /// <returns>The request handed to the inner handler.</returns>
    internal static HttpRequestMessage CreateInnerRequest(HttpRequestMessage request, Version version, HttpVersionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri original = request.RequestUri ?? throw new InvalidOperationException("The request URI is required.");
        bool rewritten = string.Equals(original.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        Uri uri = original;
        if (rewritten)
        {
            UriBuilder builder = new(original)
            {
                Scheme = Uri.UriSchemeHttp,
                Port = original.IsDefaultPort ? 443 : original.Port,
            };
            uri = builder.Uri;
        }

        HttpRequestMessage inner = new(request.Method, uri)
        {
            Version = version,
            VersionPolicy = policy,
            Content = request.Content,
        };
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            inner.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (rewritten)
        {
            inner.Headers.Host = string.IsNullOrEmpty(request.Headers.Host)
                ? (original.IsDefaultPort ? original.IdnHost : original.Authority)
                : request.Headers.Host;
            inner.Options.Set(ManagedTlsOption, true);
        }

        return inner;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireFips();
        if (WantsHttp3(request))
        {
            try
            {
                return await Http3Client.SendAsync(this, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Http3ConnectException) when (request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower)
            {
            }
        }

        VersionChoice choice = Choose(request);
        HttpResponseMessage response;
        bool retried = false;
        try
        {
            response = await SendCoreAsync(request, choice.Version, choice.Policy, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (choice.CanFallBack && IsAlpnDowngrade(ex))
        {
            retried = true;
            RememberHttp11(request.RequestUri);
            response = await SendCoreAsync(request, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (choice.UsedH2 && choice.CanFallBack && IsReplaySafe(request.Method) && ex.HttpRequestError == HttpRequestError.HttpProtocolError)
        {
            retried = true;
            RememberHttp11(request.RequestUri);
            response = await SendCoreAsync(request, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, cancellationToken).ConfigureAwait(false);
        }

        if (!retried && choice.UsedH2 && choice.CanFallBack && IsReplaySafe(request.Method) && IsSchemeRejection(response.StatusCode))
        {
            response.Dispose();
            RememberHttp11(request.RequestUri);
            response = await SendCoreAsync(request, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _sockets.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        Version version,
        HttpVersionPolicy policy,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage innerRequest = CreateInnerRequest(request, version, policy);
        try
        {
            HttpResponseMessage response = await _inner.SendAsync(innerRequest, cancellationToken).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
        finally
        {
            innerRequest.Content = null;
            innerRequest.Dispose();
        }
    }

    private VersionChoice Choose(HttpRequestMessage request)
    {
        bool knownHttp11 = TryKnownHttp11(request.RequestUri);
        bool allowHigher = request.VersionPolicy == HttpVersionPolicy.RequestVersionOrHigher;
        bool asksForHttp2 = request.Version.Major >= 2;
        bool exact = request.VersionPolicy == HttpVersionPolicy.RequestVersionExact;
        bool tryHttp2 = asksForHttp2 || allowHigher;
        if (knownHttp11 && !(exact && asksForHttp2))
        {
            tryHttp2 = false;
        }

        if (!tryHttp2)
        {
            return new VersionChoice(HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, false, false);
        }

        bool canFallBack = request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower
            || (allowHigher && request.Version.Major < 2);
        return new VersionChoice(HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact, true, canFallBack);
    }

    private void RequireFips()
    {
        if (!FipsEnvironment.IsEnabled() || AllowManagedStackUnderFips || OverrideIsSet())
        {
            return;
        }

        throw new NotSupportedException(FipsRefusal);
    }

    private static bool WantsHttp3(HttpRequestMessage request) => request.Version.Major == 3;

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        CancellationTokenSource? linked = null;
        CancellationToken token = cancellationToken;
        TimeSpan budget = _sockets.ConnectTimeout;
        if (budget > TimeSpan.Zero && budget != Timeout.InfiniteTimeSpan)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(budget);
            token = linked.Token;
        }

        try
        {
            return await OpenAsync(context, token).ConfigureAwait(false);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    private async ValueTask<Stream> OpenAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        bool useTls = context.InitialRequestMessage.Options.TryGetValue(ManagedTlsOption, out bool managed) && managed;
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        NetworkStream network = new(socket, ownsSocket: true);
        if (!useTls)
        {
            return network;
        }

        bool requireHttp2 = context.InitialRequestMessage.Version.Major >= 2;
        TlsStream tls = new(network, new TlsStreamOptions
        {
            FingerprintProfile = FingerprintProfile,
            AllowManagedStackUnderFips = AllowManagedStackUnderFips,
        });
        bool handedOff = false;
        try
        {
            await tls.AuthenticateAsClientAsync(CopySslOptions(context, requireHttp2), cancellationToken).ConfigureAwait(false);
            SslApplicationProtocol negotiated = tls.NegotiatedApplicationProtocol;
            string key = AuthorityKey(context.InitialRequestMessage.RequestUri);
            if (negotiated != SslApplicationProtocol.Http2)
            {
                Remember(key, SslApplicationProtocol.Http11);
                if (requireHttp2)
                {
                    throw new ServerChoseHttp11Exception();
                }
            }
            else
            {
                Remember(key, SslApplicationProtocol.Http2);
            }

            handedOff = true;
            return tls;
        }
        finally
        {
            if (!handedOff)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private SslClientAuthenticationOptions CopySslOptions(SocketsHttpConnectionContext context, bool requireHttp2)
    {
        string host = _sslOptions.TargetHost ?? string.Empty;
        if (host.Length == 0)
        {
            host = context.InitialRequestMessage.RequestUri?.IdnHost ?? string.Empty;
        }

        if (host.Length == 0)
        {
            host = context.DnsEndPoint.Host;
        }

        List<SslApplicationProtocol> protocols = new();
        if (requireHttp2)
        {
            protocols.Add(SslApplicationProtocol.Http2);
        }

        protocols.Add(SslApplicationProtocol.Http11);
        return new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = _sslOptions.ClientCertificates,
            CertificateRevocationCheckMode = _sslOptions.CertificateRevocationCheckMode,
            CertificateChainPolicy = _sslOptions.CertificateChainPolicy,
            EncryptionPolicy = _sslOptions.EncryptionPolicy,
            EnabledSslProtocols = _sslOptions.EnabledSslProtocols,
            RemoteCertificateValidationCallback = _sslOptions.RemoteCertificateValidationCallback,
            LocalCertificateSelectionCallback = _sslOptions.LocalCertificateSelectionCallback,
            ClientCertificateContext = _sslOptions.ClientCertificateContext,
            CipherSuitesPolicy = _sslOptions.CipherSuitesPolicy,
            AllowRenegotiation = false,
            ApplicationProtocols = protocols,
        };
    }

    private bool TryKnownHttp11(Uri? uri)
    {
        string key = AuthorityKey(uri);
        if (!_alpn.TryGetValue(key, out AlpnEntry entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= Environment.TickCount64)
        {
            _alpn.TryRemove(key, out _);
            return false;
        }

        return entry.Protocol == SslApplicationProtocol.Http11;
    }

    private void RememberHttp11(Uri? uri) => Remember(AuthorityKey(uri), SslApplicationProtocol.Http11);

    private void Remember(string key, SslApplicationProtocol protocol)
    {
        if (key.Length == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_alpn.Count >= MaxAuthorities)
        {
            foreach (KeyValuePair<string, AlpnEntry> entry in _alpn)
            {
                if (entry.Value.ExpiresAt <= now)
                {
                    _alpn.TryRemove(entry.Key, out _);
                }
            }
        }

        if (_alpn.Count >= MaxAuthorities)
        {
            _alpn.Clear();
        }

        _alpn[key] = new AlpnEntry(protocol, now + (long)TimeSpan.FromMinutes(10).TotalMilliseconds);
    }

    private static string AuthorityKey(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        return uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsReplaySafe(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;

    private static bool IsSchemeRejection(HttpStatusCode status) =>
        status == HttpStatusCode.BadRequest || status == HttpStatusCode.MisdirectedRequest;

    private static bool IsAlpnDowngrade(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ServerChoseHttp11Exception)
            {
                return true;
            }
        }

        return false;
    }

    private static bool OverrideIsSet() =>
        string.Equals(
            Environment.GetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE"),
            "1",
            StringComparison.Ordinal);

    private readonly struct VersionChoice
    {
        public VersionChoice(Version version, HttpVersionPolicy policy, bool usedH2, bool canFallBack)
        {
            Version = version;
            Policy = policy;
            UsedH2 = usedH2;
            CanFallBack = canFallBack;
        }

        public Version Version { get; }

        public HttpVersionPolicy Policy { get; }

        public bool UsedH2 { get; }

        public bool CanFallBack { get; }
    }

    private readonly struct AlpnEntry
    {
        public AlpnEntry(SslApplicationProtocol protocol, long expiresAt)
        {
            Protocol = protocol;
            ExpiresAt = expiresAt;
        }

        public SslApplicationProtocol Protocol { get; }

        public long ExpiresAt { get; }
    }

    private sealed class ServerChoseHttp11Exception : HttpRequestException
    {
        public ServerChoseHttp11Exception()
            : base("The server selected HTTP/1.1.")
        {
        }
    }
}
