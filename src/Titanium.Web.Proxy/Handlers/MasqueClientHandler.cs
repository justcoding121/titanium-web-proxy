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
                ApplicationProtocols = SslExtensions.Http2ProtocolAsList,
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

        if (sslStream.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        clientConnection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
        var clientStream = new HttpClientStream(this, clientConnection, sslStream, BufferPool, cancellationTokenSource.Token);
        await SendMasqueAsync(clientStream, endPoint, cancellationTokenSource).ConfigureAwait(false);
    }

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
