using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private async Task HandleMasqueClient(MasqueProxyEndPoint endPoint, TcpClientConnection clientConnection)
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

        var networkStream = clientConnection.GetStream();
        var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        try
        {
            SslStreamCertificateContext? context = null;
            try
            {
                context = CertificateManager.CreateSslCertificateContext(certificate);
            }
            catch (CryptographicException)
            {
                // A leaf minted outside this CertificateManager can fail the offline chain
                // build. SslStream still accepts the certificate directly.
            }

            var options = new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = SupportedSslProtocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = endPoint.Options.AllowHttp11Upgrade
                    ? SslExtensions.Http2AndHttp11ProtocolAsList
                    : SslExtensions.Http2ProtocolAsList,
            };
            if (context is not null)
            {
                options.ServerCertificateContext = context;
            }
            else
            {
                options.ServerCertificate = certificate;
            }

            await AwaitPendingClientHelloAsync(clientConnection).ConfigureAwait(false);
            await sslStream.AuthenticateAsServerAsync(options, cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(logger, "MASQUE client handshake failed", ex);
            await sslStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var negotiated = sslStream.NegotiatedApplicationProtocol;
        var http11Upgrade = endPoint.Options.AllowHttp11Upgrade
            && (negotiated == SslApplicationProtocol.Http11 || negotiated.Protocol.Length == 0);
        if (negotiated != SslApplicationProtocol.Http2 && !http11Upgrade)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        clientConnection.NegotiatedApplicationProtocol = negotiated;
        var clientStream = new HttpClientStream(this, clientConnection, sslStream, BufferPool, cancellationTokenSource.Token);
        if (negotiated == SslApplicationProtocol.Http2)
        {
            await SendMasqueAsync(clientStream, endPoint, cancellationTokenSource).ConfigureAwait(false);
            return;
        }

        await HandleMasqueHttp11Async(endPoint, clientConnection, clientStream, cancellationTokenSource).ConfigureAwait(false);
    }

    private async Task HandleMasqueHttp11Async(MasqueProxyEndPoint endPoint, TcpClientConnection clientConnection,
        HttpClientStream clientStream, CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var admitted = false;
        IPAddress client = IPAddress.None;
        MasqueTunnelOpen? opened = null;
        try
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
            try
            {
                await HeaderParser.ReadHeaders(clientStream, headers, cancellationToken,
                    clientStream.HeaderBudget(isRequest: true)).ConfigureAwait(false);
            }
            catch (HeaderLimitsExceededException)
            {
                await WriteHttp11Async(clientStream, 431, "Request Header Fields Too Large",
                    "header limits exceeded", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!Http11UpgradeAccepted(requestLine, headers, out var rejection))
            {
                await WriteHttp11Async(clientStream, 400, "Bad Request", rejection, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (endPoint.Options.RequireAuth
                && !await AuthenticatedHeaderAsync(endPoint, headers.GetFirstHeader("proxy-authorization")?.Value)
                    .ConfigureAwait(false))
            {
                await WriteHttp11Async(clientStream, 407, "Proxy Authentication Required",
                    "proxy authentication required", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!ConnectUdpPath.TryParse(requestLine.RequestUri.GetString(), out var host, out var port))
            {
                await WriteHttp11Async(clientStream, 400, "Bad Request",
                    "connect-udp path is not /.well-known/masque/udp/{host}/{port}/", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            client = clientConnection.RemoteEndPoint is IPEndPoint remote
                ? MasqueProxyEndPoint.ClientKey(remote.Address)
                : IPAddress.None;
            if (!endPoint.TryAdmit(client))
            {
                await WriteHttp11Async(clientStream, 429, "Too Many Requests",
                    "too many connect-udp tunnels", cancellationToken).ConfigureAwait(false);
                return;
            }

            admitted = true;
            var (status, reason, open) = await MasqueTunnel.OpenAsync(
                endPoint, host, port, logger, cancellationToken).ConfigureAwait(false);
            opened = open;
            if (opened is null)
            {
                await WriteHttp11Async(clientStream, status, ReasonPhrase(status),
                    reason ?? "connect-udp target was rejected", cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteHttp11Async(clientStream, 101, "Switching Protocols", null, cancellationToken)
                .ConfigureAwait(false);
            var inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pump = PumpHttp11CapsulesAsync(clientStream, inbound.Writer, relayStop.Token);
            try
            {
                await MasqueTunnel.RelayAsync(endPoint, inbound.Reader, clientStream, opened.Socket, logger,
                    relayStop.Token).ConfigureAwait(false);
            }
            finally
            {
                await relayStop.CancelAsync().ConfigureAwait(false);
                inbound.Writer.TryComplete();
            }

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The tunnel is closing.
            }
        }
        finally
        {
            opened?.Release();
            if (admitted)
            {
                endPoint.ReleaseTunnel(client);
            }

            await clientStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Http11UpgradeAccepted(RequestStatusInfo requestLine, HeaderCollection headers, out string rejection)
    {
        rejection = "connect-udp upgrade was rejected";
        if (!string.Equals(requestLine.Method, "GET", StringComparison.OrdinalIgnoreCase)
            || requestLine.Version != HttpHeader.Version11)
        {
            return false;
        }

        if (!HeaderListsToken(headers.GetFirstHeader("connection")?.Value, "upgrade")
            || !HeaderListsToken(headers.GetFirstHeader("upgrade")?.Value, "connect-udp"))
        {
            return false;
        }

        if (!MasqueRequestChecks.CapsuleProtocolAccepted(headers.GetFirstHeader("capsule-protocol")?.Value))
        {
            rejection = "capsule-protocol must be absent or ?1";
            return false;
        }

        var length = headers.GetFirstHeader("content-length")?.Value;
        if (length is not null && (!int.TryParse(length.Trim(), out var bytes) || bytes != 0))
        {
            rejection = "connect-udp upgrade cannot carry a body";
            return false;
        }

        return true;
    }

    private static bool HeaderListsToken(string? value, string token)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var part in value.Split(','))
        {
            if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> AuthenticatedHeaderAsync(MasqueProxyEndPoint endPoint, string? header)
    {
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
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

        if (ProxyBasicAuthenticateFunc is null)
        {
            return false;
        }

        return await ProxyBasicAuthenticateFunc(null, user, password).ConfigureAwait(false);
    }

    private static async Task PumpHttp11CapsulesAsync(HttpClientStream clientStream,
        ChannelWriter<ReadOnlyMemory<byte>> inbound, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await clientStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    inbound.TryComplete();
                    return;
                }

                var copy = new byte[read];
                buffer.AsSpan(0, read).CopyTo(copy);
                await inbound.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            inbound.TryComplete();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteHttp11Async(Stream stream, int status, string reason, string? body,
        CancellationToken cancellationToken)
    {
        var payload = body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body).AsMemory();
        string head;
        if (status == 101)
        {
            head = "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: connect-udp\r\nCapsule-Protocol: ?1\r\n\r\n";
        }
        else
        {
            var authenticate = status == 407 ? "Proxy-Authenticate: Basic realm=\"masque\"\r\n" : string.Empty;
            head = $"HTTP/1.1 {status} {reason}\r\nConnection: close\r\n{authenticate}Content-Length: {payload.Length}\r\n\r\n";
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        if (status != 101 && payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        403 => "Forbidden",
        429 => "Too Many Requests",
        502 => "Bad Gateway",
        _ => "Error",
    };

    private async Task SendMasqueAsync(HttpClientStream clientStream, MasqueProxyEndPoint endPoint,
        CancellationTokenSource cancellationTokenSource)
    {
        var origin = new NullOriginStream(cancellationTokenSource.Token, advertiseConnectProtocol: true);
        await ConsumeHttp2ConnectionPrefaceAsync(clientStream, cancellationTokenSource.Token).ConfigureAwait(false);
        await Http2Helper.SendHttp2(clientStream, origin,
            () => new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource),
            (session, ctx) => MasqueOnBeforeRequestAsync(session, ctx, endPoint),
            (_, _) => Task.CompletedTask,
            OnAfterResponse,
            _ => { },
            cancellationTokenSource, clientStream.Connection.Id, logger,
            MaxDecodedHeaderListBytes, enableRfc8441: true, ResourceLimits,
            httpInterceptionEnabled: false).ConfigureAwait(false);
    }

    private async Task MasqueOnBeforeRequestAsync(SessionEventArgs session, Http2StreamContext ctx,
        MasqueProxyEndPoint endPoint)
    {
        var request = session.HttpClient.Request;
        if (endPoint.Options.RequireAuth && !await AuthenticatedAsync(endPoint, session).ConfigureAwait(false))
        {
            session.GenericResponse("proxy authentication required", HttpStatusCode.ProxyAuthenticationRequired,
                [new HttpHeader("proxy-authenticate", "Basic realm=\"masque\"")]);
            endPoint.Reject();
            return;
        }

        if (!string.Equals(request.ExtendedConnectProtocol, "connect-udp", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            Reject(session, endPoint, 501, "only connect-udp is supported on this listener");
            return;
        }

        var capsule = request.Headers.GetFirstHeader("capsule-protocol")?.Value;
        if (!MasqueRequestChecks.CapsuleProtocolAccepted(capsule))
        {
            Reject(session, endPoint, 400, "capsule-protocol must be absent or ?1");
            return;
        }

        if (request.Authority.Length == 0 || !request.IsHttps)
        {
            Reject(session, endPoint, 400, "connect-udp requires an https authority");
            return;
        }

        if (!ConnectUdpPath.TryParse(request.RequestUriString, out var host, out var port))
        {
            Reject(session, endPoint, 400, "connect-udp path is not /.well-known/masque/udp/{host}/{port}/");
            return;
        }

        var client = MasqueProxyEndPoint.ClientKey(session.ClientRemoteEndPoint.Address);
        if (!endPoint.TryAdmit(client))
        {
            session.GenericResponse("too many connect-udp tunnels", (HttpStatusCode)429,
                [new HttpHeader("retry-after", "1")]);
            return;
        }

        var ownsSlot = true;
        try
        {
        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
        {
            Reject(session, endPoint, 500, "internal error");
            return;
        }

        var (status, reason, opened) = await MasqueTunnel.OpenAsync(
            endPoint, host, port, logger, ctx.CancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            Reject(session, endPoint, status, reason ?? "connect-udp target was rejected");
            return;
        }

        var udp = opened.Socket;
        var inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        streamState.InboundTunnelChannel = inbound;
        streamState.IsExternalBridge = true;
        var released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            inbound.Writer.TryComplete();
            opened.Release();
            endPoint.ReleaseTunnel(client);
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
        // RespondStreaming marks the request cancelled so the HTTP/2 dispatcher emits this
        // response once. Emitting it here as well races that dispatcher.
        session.RespondStreaming(response, async (body, token) =>
        {
            try
            {
                await MasqueTunnel.RelayAsync(endPoint, inbound.Reader, body, udp, logger, token).ConfigureAwait(false);
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
                endPoint.ReleaseTunnel(client);
            }
        }
    }

    private async Task<bool> AuthenticatedAsync(MasqueProxyEndPoint endPoint, SessionEventArgs session)
    {
        var header = session.HttpClient.Request.Headers.GetFirstHeader("proxy-authorization")?.Value;
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
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

        if (ProxyBasicAuthenticateFunc is null)
        {
            return false;
        }

        return await ProxyBasicAuthenticateFunc(session, user, password).ConfigureAwait(false);
    }

    private static void Reject(SessionEventArgs session, MasqueProxyEndPoint endPoint, int status, string body)
    {
        session.GenericResponse(body, (HttpStatusCode)status);
        endPoint.Reject();
    }
}

/// <summary>Last activity time shared by the relay tasks without a torn read.</summary>
internal sealed class IdleClock
{
    private long ticks = Environment.TickCount64;

    public void Touch() => Volatile.Write(ref ticks, Environment.TickCount64);

    public long Last => Volatile.Read(ref ticks);
}

/// <summary>One-second token bucket owned by a single tunnel.</summary>
internal sealed class DatagramBudget
{
    private readonly int maxDatagrams;
    private readonly int maxBytes;
    private readonly object gate = new();
    private readonly Func<long> clock;
    private long windowStart;
    private int datagrams;
    private int bytes;

    public DatagramBudget(int maxDatagrams, int maxBytes, Func<long>? clock = null)
    {
        this.maxDatagrams = maxDatagrams;
        this.maxBytes = maxBytes;
        this.clock = clock ?? (() => Environment.TickCount64);
        windowStart = this.clock();
    }

    public bool TryTake(int size)
    {
        lock (gate)
        {
            var now = clock();
            if (now - windowStart >= 1000)
            {
                windowStart = now;
                datagrams = 0;
                bytes = 0;
            }

            if (datagrams >= maxDatagrams || (long)bytes + size > maxBytes)
            {
                return false;
            }

            datagrams++;
            bytes += size;
            return true;
        }
    }
}
