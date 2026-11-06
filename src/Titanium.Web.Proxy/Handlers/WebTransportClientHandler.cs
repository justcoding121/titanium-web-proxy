using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.WebTransport;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private const string WebTransportBasicPrefix = "Basic ";

    private async Task HandleWebTransportClient(WebTransportProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        RegisterSessionCancellation(cancellationTokenSource);
        var certificate = endPoint.GenericCertificate;
        if (certificate is null)
        {
            certificate = await CertificateManager.CreateServerCertificate("localhost").ConfigureAwait(false);
            if (certificate is null)
            {
                return;
            }

            endPoint.GenericCertificate ??= certificate;
            certificate = endPoint.GenericCertificate;
        }

        var sslStream = new SslStream(clientConnection.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = endPoint.Options.AllowHttp11Upgrade
                    ? SslExtensions.Http2AndHttp11ProtocolAsList
                    : SslExtensions.Http2ProtocolAsList,
            };
            ApplyListenerTlsPolicy(options);
            CertificateManager.ConfigureServerCertificate(options, certificate);
            await AwaitPendingClientHelloAsync(clientConnection).ConfigureAwait(false);
            await sslStream.AuthenticateAsServerAsync(options, cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(logger, "WebTransport client handshake failed", ex);
            await sslStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var negotiated = sslStream.NegotiatedApplicationProtocol;
        var http11 = endPoint.Options.AllowHttp11Upgrade
            && (negotiated == SslApplicationProtocol.Http11 || negotiated.Protocol.Length == 0);
        if (negotiated != SslApplicationProtocol.Http2 && !http11)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        clientConnection.NegotiatedApplicationProtocol = negotiated;
        var clientStream = new HttpClientStream(this, clientConnection, sslStream, BufferPool, cancellationTokenSource.Token);
        if (negotiated == SslApplicationProtocol.Http2)
        {
            await SendWebTransportAsync(clientStream, endPoint, cancellationTokenSource).ConfigureAwait(false);
            return;
        }

        await HandleWebTransportHttp11Async(endPoint, clientStream, cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task SendWebTransportAsync(HttpClientStream clientStream, WebTransportProxyEndPoint endPoint,
        CancellationTokenSource cancellationTokenSource)
    {
        var origin = new NullOriginStream(cancellationTokenSource.Token, advertiseConnectProtocol: true);
        await ConsumeHttp2ConnectionPrefaceAsync(clientStream, cancellationTokenSource.Token).ConfigureAwait(false);
        await Http2Helper.SendHttp2(clientStream, origin,
            () => new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource),
            (session, ctx) => WebTransportOnBeforeRequestAsync(session, ctx, endPoint),
            (_, _) => Task.CompletedTask,
            OnAfterResponse,
            _ => { },
            cancellationTokenSource, clientStream.Connection.Id, logger,
            MaxDecodedHeaderListBytes, enableRfc8441: true, ResourceLimits,
            httpInterceptionEnabled: false, advertiseWebTransport: true).ConfigureAwait(false);
    }

    private async Task WebTransportOnBeforeRequestAsync(SessionEventArgs session, Http2StreamContext ctx,
        WebTransportProxyEndPoint endPoint)
    {
        var request = session.HttpClient.Request;
        if (endPoint.Options.RequireAuth && !await WebTransportAuthenticatedAsync(endPoint, session).ConfigureAwait(false))
        {
            session.GenericResponse("proxy authentication required", HttpStatusCode.ProxyAuthenticationRequired,
                [new HttpHeader("proxy-authenticate", "Basic realm=\"webtransport\"")]);
            return;
        }

        if (!ExtendedConnectProtocols.IsWebTransport(request.ExtendedConnectProtocol)
            || !string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            session.GenericResponse("only webtransport is supported on this listener", HttpStatusCode.NotImplemented);
            return;
        }

        var capsule = request.Headers.GetFirstHeader("capsule-protocol")?.Value;
        if (capsule != null && !string.Equals(capsule, "?1", StringComparison.Ordinal))
        {
            session.GenericResponse("capsule-protocol must be absent or ?1", HttpStatusCode.BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(endPoint.Options.OriginHost))
        {
            session.GenericResponse("webtransport origin is not configured", HttpStatusCode.BadGateway);
            return;
        }

        var client = MasqueProxyEndPoint.ClientKey(session.ClientRemoteEndPoint.Address);
        if (!endPoint.TryAdmit(client))
        {
            session.GenericResponse("too many webtransport sessions", (HttpStatusCode)429,
                [new HttpHeader("retry-after", "1")]);
            return;
        }

        var ownsSlot = true;
        try
        {
            if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
            {
                session.GenericResponse("internal error", HttpStatusCode.InternalServerError);
                return;
            }

            var path = request.RequestUri.AbsolutePath;
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }

            var opened = await WebTransportOrigin.OpenHttp11Async(
                endPoint.Options.OriginHost, endPoint.Options.OriginPort, endPoint.Options.OriginHttps,
                path, ctx.CancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                session.GenericResponse("webtransport origin upgrade failed", HttpStatusCode.BadGateway);
                return;
            }

            var inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            streamState.InboundTunnelChannel = inbound;
            streamState.IsExternalBridge = true;
            streamState.IsExtendedConnect = true;
            var released = 0;
            void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                {
                    return;
                }

                inbound.Writer.TryComplete();
                opened.DisposeAsync().AsTask();
                endPoint.ReleaseSession(client);
            }

            streamState.Cancellation.Token.Register(ReleaseOnce);
            var response = new Response
            {
                HttpVersion = HttpHeader.Version20,
                StatusCode = 200,
                StatusDescription = "OK",
            };
            response.Headers.AddHeader("capsule-protocol", "?1");
            ownsSlot = false;
            session.RespondStreaming(response, async (body, token) =>
            {
                try
                {
                    await WebTransportOrigin.RelayAsync(inbound.Reader, opened.Stream, body, token).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseOnce();
                }
            });
        }
        finally
        {
            if (ownsSlot)
            {
                endPoint.ReleaseSession(client);
            }
        }
    }

    private async Task HandleWebTransportHttp11Async(WebTransportProxyEndPoint endPoint, HttpClientStream clientStream,
        CancellationToken cancellationToken)
    {
        RequestStatusInfo requestLine;
        try
        {
            requestLine = await clientStream.ReadRequestLine(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return;
        }

        if (requestLine.IsEmpty())
        {
            return;
        }

        var headers = new HeaderCollection();
        await HeaderParser.ReadHeaders(clientStream, headers, cancellationToken,
            clientStream.HeaderBudget(isRequest: true)).ConfigureAwait(false);
        var upgrade = headers.GetFirstHeader("upgrade")?.Value;
        if (!string.Equals(requestLine.Method, "GET", StringComparison.OrdinalIgnoreCase)
            || upgrade is null
            || !upgrade.Contains("webtransport", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHttp11StatusAsync(clientStream, 501, "Not Implemented", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (endPoint.Options.RequireAuth
            && !await WebTransportAuthenticatedHeaderAsync(endPoint, headers.GetFirstHeader("proxy-authorization")?.Value)
                .ConfigureAwait(false))
        {
            await WriteHttp11StatusAsync(clientStream, 407, "Proxy Authentication Required", cancellationToken,
                "Proxy-Authenticate: Basic realm=\"webtransport\"\r\n").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(endPoint.Options.OriginHost))
        {
            await WriteHttp11StatusAsync(clientStream, 502, "Bad Gateway", cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = requestLine.RequestUri.GetString();
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var opened = await WebTransportOrigin.OpenHttp11Async(
            endPoint.Options.OriginHost, endPoint.Options.OriginPort, endPoint.Options.OriginHttps,
            path, cancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            await WriteHttp11StatusAsync(clientStream, 502, "Bad Gateway", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (opened)
        {
            await WriteHttp11StatusAsync(clientStream, 101, "Switching Protocols", cancellationToken,
                "Connection: Upgrade\r\nUpgrade: webtransport\r\nCapsule-Protocol: ?1\r\n").ConfigureAwait(false);
            using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var toOrigin = CopyStreamAsync(clientStream, opened.Stream, relay.Token);
            var toClient = CopyStreamAsync(opened.Stream, clientStream, relay.Token);
            await Task.WhenAny(toOrigin, toClient).ConfigureAwait(false);
            await relay.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> WebTransportAuthenticatedAsync(WebTransportProxyEndPoint endPoint, SessionEventArgs session)
    {
        var header = session.HttpClient.Request.Headers.GetFirstHeader("proxy-authorization")?.Value;
        return await WebTransportAuthenticatedHeaderAsync(endPoint, header).ConfigureAwait(false);
    }

    private async Task<bool> WebTransportAuthenticatedHeaderAsync(WebTransportProxyEndPoint endPoint, string? header)
    {
        if (header is null || !header.StartsWith(WebTransportBasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[WebTransportBasicPrefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var split = decoded.IndexOf(':');
        if (split <= 0)
        {
            return false;
        }

        var user = decoded[..split];
        var password = decoded[(split + 1)..];
        if (endPoint.Options.AuthenticateFunc is { } listenerAuth)
        {
            return await listenerAuth(user, password).ConfigureAwait(false);
        }

        return ProxyBasicAuthenticateFunc is not null
            && await ProxyBasicAuthenticateFunc(null, user, password).ConfigureAwait(false);
    }

    private static async Task WriteHttp11StatusAsync(Stream stream, int status, string reason, CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\n{extraHeaders}Content-Length: 0\r\n\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
