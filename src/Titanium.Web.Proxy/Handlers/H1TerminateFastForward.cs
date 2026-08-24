using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private static readonly Lazy<int> H1TerminateLiteProcessId = new(() => 0);

    /// <summary>
    ///     Interception-off transparent reverse with fixed <see cref="TransparentBaseProxyEndPoint.ForwardHost" />:
    ///     bodiless GET/HEAD without allocating <see cref="SessionEventArgs" /> (H3→H1 session-lite analogue).
    ///     New-connection TLS terminate pays Schannel per accept; skipping the session graph cuts parallel
    ///     GC under c=32 (cool: c=1 already leads YARP, c=32 was ~0.88–0.90×).
    ///     Callers must also refuse <see cref="UpstreamHttpProtocol.Http2"/> / <see cref="UpstreamHttpProtocol.Http3"/>
    ///     at the connection level — this path only speaks HTTP/1.1 TCP to the origin.
    /// </summary>
    private static bool CanUseH1TerminateLite(ProxyEndPoint endPoint, Request request, bool enable100Continue,
        bool enableWinAuth, bool hasCustomUpstreamProxyFunc)
    {
        if (enable100Continue || enableWinAuth || hasCustomUpstreamProxyFunc)
            return false;

        if (endPoint is not TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 })
            return false;

        if (request.HasBody || request.UpgradeToWebSocket)
            return false;

        var method = request.Method;
        return method is not null
               && (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                   || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Forward one bodiless reverse GET/HEAD without a session bag. Returns whether the client
    ///     connection should stay open for another keep-alive request.
    /// </summary>
    private async Task<bool> ForwardH1TerminateLiteAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream,
        Request request,
        CancellationToken cancellationToken)
    {
        request.Locked = true;
        request.IsBodyReceived = true;

        // Remember before stripping hop-by-hop Connection for the origin write — NC clients send
        // Connection: close; forwarding it forces the origin to close and defeats pooling (Bare still
        // ConnectAsyncs every time and was beating TWP at c=32).
        var clientRequestedClose = H1TerminateClientRequestedClose(request);
        request.Headers.RemoveHeader(KnownHeaders.Connection);

        var isHttps = !endPoint.ForwardCleartext && request.IsHttps;
        // Terminate with ForwardCleartext: origin is cleartext regardless of client TLS.
        if (endPoint.ForwardCleartext)
            isHttps = false;

        var connectHost = endPoint.ForwardHost;
        var connectPort = endPoint.ForwardPort;

        string? poolKey = null;
        if (endPoint.CachedHttp11PoolKey != null && endPoint.CachedHttp11PoolIsHttps == isHttps)
            poolKey = endPoint.CachedHttp11PoolKey;

        TcpServerConnection? connection = null;
        SessionEventArgs? openSession = null;
        var closeConnection = false;
        try
        {
            if (poolKey != null)
                TcpConnectionFactory.TryRentPooled(this, poolKey, SslExtensions.Http11ProtocolAsList,
                    out connection);

            if (connection == null)
            {
                string host;
                int port;
                if (connectHost != null && connectPort is { } fwdPort)
                {
                    host = connectHost;
                    port = fwdPort;
                }
                else
                {
                    (host, port) = request.GetOriginHostPort(isHttps ? 443 : 80);
                }

                openSession = CreateH1TerminateLiteColdSession(endPoint, clientStream);
                connection = await TcpConnectionFactory.GetServerConnection(
                    this, host, port, HttpHeader.Version11, isHttps,
                    SslExtensions.Http11ProtocolAsList, false, openSession,
                    UpStreamEndPoint,
                    isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy,
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey)
                    ?? throw new InvalidOperationException(
                        $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

                endPoint.CachedHttp11PoolKey = connection.CacheKey;
                endPoint.CachedHttp11PoolIsHttps = isHttps;
            }

            var http = new HttpWebClient(null, request, H1TerminateLiteProcessId);
            http.SetConnection(connection);
            await http.SendRequest(false, isTransparent: true, OriginHttpVersionPolicy, cancellationToken);
            await http.ReceiveResponse(cancellationToken);

            var response = http.Response;
            if (response.StatusCode is >= 100 and <= 199)
            {
                // 1xx needs the full session path's interim loop — close origin and signal fallback.
                throw new InvalidOperationException("H1 terminate lite does not handle interim 1xx responses.");
            }

            try
            {
                Http1FramingValidator.Validate(response, FramingSource.Http1WireTransparent,
                    PolicyModes.AllowAmbiguousFraming);
            }
            catch (Http1FramingException framingEx)
            {
                ProxyMetrics.ParserError("framing");
                ProxyDiagnostics.ReportBenign(logger, "Origin response has ambiguous HTTP/1 framing", framingEx);
                closeConnection = true;
                var badGateway = new GenericResponse(System.Net.HttpStatusCode.BadGateway)
                {
                    HttpVersion = request.HttpVersion
                };
                badGateway.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose);
                await clientStream.WriteResponseAsync(badGateway, cancellationToken);
                return false;
            }

            response.Locked = true;

            // Known-CL ≤64 KiB: materialize then one client write (headers+body) — same as fast path.
            // Larger / chunked bodies: still stream via CopyBody with a throwaway session (hooks unused).
            const int coalesceBodyLimit = 64 * 1024;
            if (response.HasBody
                && !response.IsChunked
                && !response.HasTrailingHeaders
                && response.ContentLength is > 0 and <= coalesceBodyLimit)
            {
                var length = (int)response.ContentLength;
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await connection.Stream.ReadAsync(body.AsMemory(read, length - read),
                        cancellationToken);
                    if (n == 0)
                        break;
                    read += n;
                }

                if (read != length)
                {
                    Array.Resize(ref body, read);
                    closeConnection = true;
                }

                response.Body = body;
                response.IsBodyReceived = true;
                response.IsBodyRead = true;
                await clientStream.WriteResponseAsync(response, cancellationToken);
            }
            else if (response.HasBody)
            {
                await clientStream.WriteResponseAsync(response, cancellationToken);
                var copySession = openSession ?? CreateH1TerminateLiteColdSession(endPoint, clientStream);
                try
                {
                    copySession.IsFastPath = true;
                    copySession.HttpClient.Response = response;
                    await connection.Stream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                        false, copySession, cancellationToken);
                    response.IsBodyReceived = true;
                }
                finally
                {
                    if (copySession != openSession)
                    {
                        copySession.Dispose();
                        copySession.CancellationTokenSource.Dispose();
                    }
                }
            }
            else
            {
                await clientStream.WriteResponseAsync(response, cancellationToken);
            }

            if (!response.KeepAlive
                || (connection.Stream is HttpStream residual && residual.DataAvailable))
                closeConnection = true;

            // Client Connection: close (NC) → stop accept-loop KA (origin may stay pooled).
            return response.KeepAlive && !clientRequestedClose;
        }
        catch (RetryableServerConnectionException)
        {
            closeConnection = true;
            throw;
        }
        catch
        {
            closeConnection = true;
            throw;
        }
        finally
        {
            if (connection != null)
                await TcpConnectionFactory.Release(connection, closeConnection);

            if (openSession != null)
            {
                openSession.CancellationTokenSource.Dispose();
                openSession.Dispose();
            }
        }
    }

    private static bool H1TerminateClientRequestedClose(Request request)
    {
        var headerValue = request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

        if (request.HttpVersion == HttpHeader.Version10)
            return headerValue == null
                   || !headerValue.Equals(KnownHeaders.ConnectionKeepAlive.String, StringComparison.OrdinalIgnoreCase);

        return headerValue != null
               && headerValue.Equals(KnownHeaders.ConnectionClose.String, StringComparison.OrdinalIgnoreCase);
    }

    private SessionEventArgs CreateH1TerminateLiteColdSession(TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream)
    {
        var nullStream = new HttpClientStream(this, clientStream.Connection, Stream.Null, BufferPool,
            CancellationToken.None, rentReadBuffer: false);
        var stubCts = new CancellationTokenSource();
        return new SessionEventArgs(this, endPoint, nullStream, null, stubCts)
        {
            IsFastPath = true
        };
    }
}
