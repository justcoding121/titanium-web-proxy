using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Http3;
using Titanium.Quic;
using Titanium.Tls;

namespace Titanium.Net.Http;

/// <summary>
/// HTTP client that always uses the managed TLS stack.
/// HTTP/1.1 and HTTP/2 go through an inner handler whose connect callback owns the <see cref="TlsStream"/>.
/// HTTP/3 uses a managed QUIC connection. One connection is reused per authority.
/// </summary>
/// <remarks>
/// The inner handler sees an <c>http</c> URI, so it does not open its own TLS stream. A caller <c>http</c> URI is sent as cleartext, including when it asks for version 3.
/// A redirect is followed here, including an HTTP/3 response. AllowAutoRedirect false returns that response. Each hop is rewritten the same way, so an https Location stays on the managed stream. Authorization is dropped when the host changes, including on HTTP/3. The same host keeps it. A 300 or 302 turns POST into GET, including on HTTP/3. An HTTP/3 redirect to an http URL is fetched as HTTP/1.1. A later https hop returns to HTTP/3. Cancelling the request stops an HTTP/3 redirect while the next hop is waiting. A 303 turns POST into GET and leaves HEAD as HEAD, including on HTTP/3. A 307 keeps the method and body, and an HTTP/3 hop sends that body again. A cleartext hop sends it again too. A 301 turns POST into GET, including on HTTP/3. A 308 keeps the method and body, and an HTTP/3 hop sends that body again.
/// A Cookie header is copied onto an HTTP/3 hop only when UseCookies is false.
/// Cookies and decompression stay on the inner handler. A cleartext hop from HTTP/3 still decompresses gzip and sends a cookie stored for that http URI. A Set-Cookie on that hop is stored for the next request.
/// Port 443 stays on that URI when the caller used the default HTTPS port.
/// <c>Host</c> stays the caller's host. An HTTP/3 redirect does not send a caller Host, and the next hop's <c>:authority</c> is the URI authority.
/// The inner handler sends <c>:scheme http</c> on HTTP/2, and there is no hook to change it.
/// A 400 or 421 on GET, HEAD, OPTIONS, or TRACE is retried once over HTTP/1.1 when the request allows a lower version.
/// An exact HTTP/2 request that the server resets does not fall back to HTTP/1.1.
/// A POST that allows a lower version is not retried after that reset.
/// A server that selects HTTP/1.1 during ALPN is retried over HTTP/1.1 before any request bytes, for any method, when a lower version is allowed.
/// The negotiated protocol is remembered per authority for ten minutes, up to 256 authorities.
/// HTTP/3 sends <c>:scheme https</c>. <see cref="SendAsync"/> returns after the response headers. The body is read as the caller reads it.
/// A HEADERS frame after the body is applied to the response trailers while that read runs.
/// Cancelling that read stops it while it is waiting for the next frame.
/// Cancelling an HTTP/1.1 body read stops it while the rest of the body is still waiting.
/// An HTTP/1.1 connection is reused until <see cref="PooledConnectionLifetime"/> elapses. The next request opens another connection.
/// While that connection is busy, <see cref="MaxConnectionsPerServer"/> of 1 keeps the next HTTP/1.1 request waiting.
/// An <c>http</c> request is sent to the configured proxy. The request line is the absolute URI.
/// An <c>https</c> request opens a CONNECT tunnel to that proxy, then the managed TLS handshake.
/// A proxy that refuses the CONNECT tunnel fails the request before the TLS handshake.
/// One connection is reused for an authority until 16 request streams, the idle timeout, or the connection lifetime.
/// A further request opens another connection, up to <see cref="MaxConnectionsPerServer"/>.
/// A GOAWAY on the peer's control stream stops new requests on that connection and closes it once nothing is in flight.
/// The dynamic table is capped at 4096 bytes. A request inserts a field that is not in the static table when the peer allows a dynamic table and at least one blocked stream. A peer insert is decoded.
/// A failed HTTP/3 connect, before any request byte, falls back to the adapter when the request allows a lower version, and when an Alt-Svc upgrade fails.
/// When <see cref="EnableAltSvc"/> is set, a later request that allows a higher version uses HTTP/3 after <c>Alt-Svc: h3</c> from the same origin.
/// The cache holds 256 origins. An entry ends when its <c>ma</c> elapses. A missing <c>ma</c> lasts 24 hours. <c>ma=0</c> and <c>clear</c> drop the entry. A different host is ignored.
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
    private readonly Http3Pool _pool = new();
    private readonly ConcurrentDictionary<string, AlpnEntry> _alpn = new();
    private readonly AltSvcCache _alt = new();
    private SslClientAuthenticationOptions _sslOptions = new();
    private bool _followRedirects = true;

    /// <summary>Creates a handler. The inner handler's connect callback owns the managed TLS stream.</summary>
    public TitaniumHttpHandler()
    {
        // The HTTP/3 codec stays referenced for the client that follows this adapter.
        _ = typeof(Http3FrameCodec).Assembly;
        _sockets.ConnectCallback = ConnectAsync;
        _sockets.AllowAutoRedirect = false;
        _inner = new HttpMessageInvoker(_sockets, disposeHandler: false);
    }

    /// <summary>HTTP/3 connections kept for reuse. Internal so the HTTP/3 client can rent one.</summary>
    internal Http3Pool Pool => _pool;

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

    /// <summary>
    /// Gets or sets whether a later request that allows a higher version uses HTTP/3 after <c>Alt-Svc: h3</c> from the same origin.
    /// The cache holds 256 origins and drops an entry when its <c>ma</c> elapses. A missing <c>ma</c> lasts 24 hours.
    /// </summary>
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

    /// <summary>
    /// Gets or sets whether a 3xx response is followed.
    /// Each hop is sent through this handler, so an https Location stays on the managed stream.
    /// </summary>
    public bool AllowAutoRedirect
    {
        get => _followRedirects;
        set => _followRedirects = value;
    }

    /// <summary>Gets or sets how many redirects this handler follows, including an HTTP/3 chain.</summary>
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

    /// <summary>Gets or sets how long a TCP connect, the managed TLS handshake, and an HTTP/3 handshake may take. The default matches the inner handler.</summary>
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
        if (_followRedirects && request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
        response = await FollowRedirectsAsync(request, response, cancellationToken).ConfigureAwait(false);
        NoteAltSvc(request, response);
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool viaAlt = false;
        int? altPort = null;
        if (!WantsHttp3(request)
            && IsHttps(request)
            && EnableAltSvc
            && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrHigher
            && request.RequestUri is Uri origin
            && _alt.TryGet(origin, DateTimeOffset.UtcNow, out int cachedPort))
        {
            viaAlt = true;
            altPort = cachedPort;
        }

        if (WantsHttp3(request) || viaAlt)
        {
            try
            {
                return await Http3Client.SendAsync(this, request, altPort, cancellationToken).ConfigureAwait(false);
            }
            catch (Http3ConnectException) when (viaAlt || request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower)
            {
            }
        }

        return await SendAdapterAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(
        HttpRequestMessage original,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!_followRedirects || original.RequestUri is null)
        {
            return response;
        }

        HttpRequestMessage current = original;
        HttpRequestMessage? owned = null;
        int left = _sockets.MaxAutomaticRedirections;
        while (IsRedirect(response.StatusCode))
        {
            if (response.Headers.Location is not Uri location)
            {
                break;
            }

            if (left <= 0)
            {
                response.Dispose();
                if (owned is not null)
                {
                    owned.Content = null;
                    owned.Dispose();
                }

                throw new HttpRequestException("The redirect limit was reached.");
            }

            Uri nextUri = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (nextUri.Fragment.Length > 0)
            {
                UriBuilder builder = new(nextUri) { Fragment = string.Empty };
                nextUri = builder.Uri;
            }

            HttpMethod method = RedirectMethod(current.Method, response.StatusCode);
            bool keepBody = current.Content is not null
                && method == current.Method
                && response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            Version hopVersion = original.Version;
            HttpVersionPolicy hopPolicy = original.VersionPolicy;
            if (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && original.Version.Major == 3)
            {
                hopVersion = HttpVersion.Version11;
                hopPolicy = HttpVersionPolicy.RequestVersionExact;
            }

            HttpRequestMessage next = new(method, nextUri)
            {
                Version = hopVersion,
                VersionPolicy = hopPolicy,
            };
            foreach (KeyValuePair<string, IEnumerable<string>> header in current.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || (UseCookies && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(current.RequestUri?.IdnHost, nextUri.IdnHost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (keepBody)
            {
                next.Content = current.Content;
            }

            NoteAltSvc(original, response);
            response.Dispose();
            if (owned is not null)
            {
                owned.Content = null;
                owned.Dispose();
            }

            owned = next;
            current = next;
            left--;
            response = await SendOnceAsync(current, cancellationToken).ConfigureAwait(false);
        }

        response.RequestMessage = original;
        if (owned is not null)
        {
            owned.Content = null;
            owned.Dispose();
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendAdapterAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static HttpMethod RedirectMethod(HttpMethod method, HttpStatusCode status)
    {
        if (status == HttpStatusCode.SeeOther)
        {
            return method == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get;
        }

        if (method == HttpMethod.Post
            && status is HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
        {
            return HttpMethod.Get;
        }

        return method;
    }

    private void NoteAltSvc(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (!EnableAltSvc || request.RequestUri is null || !response.Headers.TryGetValues("Alt-Svc", out IEnumerable<string>? values))
        {
            return;
        }

        _alt.Note(request.RequestUri, string.Join(",", values), DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pool.Dispose();
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
        if (request.Version.Major == 3 && !IsHttps(request))
        {
            return new VersionChoice(HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, false, false);
        }

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

    private static bool WantsHttp3(HttpRequestMessage request) => request.Version.Major == 3 && IsHttps(request);

    private static bool IsHttps(HttpRequestMessage request) =>
        request.RequestUri is Uri uri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

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

        Uri? requestUri = context.InitialRequestMessage.RequestUri;
        if (requestUri is not null && IsProxyEndpoint(context.DnsEndPoint, requestUri))
        {
            try
            {
                await OpenProxyTunnelAsync(network, requestUri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await network.DisposeAsync().ConfigureAwait(false);
                throw;
            }
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

    private static bool IsProxyEndpoint(DnsEndPoint endpoint, Uri origin) =>
        !string.Equals(endpoint.Host, origin.IdnHost, StringComparison.OrdinalIgnoreCase) || endpoint.Port != origin.Port;

    private static async Task OpenProxyTunnelAsync(Stream stream, Uri origin, CancellationToken cancellationToken)
    {
        string authority = origin.IdnHost + ":" + origin.Port.ToString(CultureInfo.InvariantCulture);
        string connect = "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect), cancellationToken).ConfigureAwait(false);
        string headers = await ReadAsciiHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!IsProxySuccess(headers))
        {
            throw new HttpRequestException("The proxy refused the tunnel.");
        }
    }

    private static async Task<string> ReadAsciiHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new HttpRequestException("The proxy closed the connection.");
            }

            length += read;
            if (length >= 4
                && buffer[length - 4] == (byte)'\r'
                && buffer[length - 3] == (byte)'\n'
                && buffer[length - 2] == (byte)'\r'
                && buffer[length - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
        }

        throw new HttpRequestException("The proxy response headers are too long.");
    }

    private static bool IsProxySuccess(string headers)
    {
        int end = headers.IndexOf("\r\n", StringComparison.Ordinal);
        string line = end >= 0 ? headers[..end] : headers;
        string[] parts = line.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status) && status is >= 200 and <= 299;
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
