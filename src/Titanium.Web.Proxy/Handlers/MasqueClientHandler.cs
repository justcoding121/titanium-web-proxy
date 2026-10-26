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
        if (!string.Equals(capsule, "?1", StringComparison.Ordinal))
        {
            Reject(session, endPoint, 400, "capsule-protocol: ?1 is required");
            return;
        }

        if (!ConnectUdpPath.TryParse(request.RequestUriString, out var host, out var port))
        {
            Reject(session, endPoint, 400, "connect-udp path is not /.well-known/masque/udp/{host}/{port}/");
            return;
        }

        IPAddress[] resolved;
        try
        {
            resolved = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(logger, "MASQUE target DNS failed", ex);
            resolved = [];
        }

        var pin = MasqueTargetPolicy.Select(host, port, endPoint.Options.AllowedPorts,
            endPoint.Options.BlockPrivateTargets, _ => resolved);
        if (pin.Address is null)
        {
            var status = pin.Rejection == "port" || pin.Rejection == "blocked" ? 403 : 502;
            Reject(session, endPoint, status, "connect-udp target was rejected");
            return;
        }

        var client = session.ClientRemoteEndPoint.Address;
        if (!endPoint.TryAdmit(client))
        {
            session.GenericResponse("too many connect-udp tunnels", (HttpStatusCode)429,
                [new HttpHeader("retry-after", "1")]);
            return;
        }

        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
        {
            endPoint.ReleaseTunnel(client);
            return;
        }

        var udp = new Socket(pin.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            udp.Connect(new IPEndPoint(pin.Address, port));
        }
        catch (Exception ex)
        {
            udp.Dispose();
            endPoint.ReleaseTunnel(client);
            ProxyDiagnostics.ReportBenign(logger, "MASQUE UDP connect failed", ex);
            Reject(session, endPoint, 502, "connect-udp target was rejected");
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
        var released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            inbound.Writer.TryComplete();
            try
            {
                udp.Dispose();
            }
            catch (Exception)
            {
                // The receive loop unblocks on disposal.
            }

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
        // RespondStreaming marks the request cancelled so the HTTP/2 dispatcher emits this
        // response once. Emitting it here as well races that dispatcher.
        session.RespondStreaming(response, async (body, token) =>
        {
            try
            {
                await RelayAsync(endPoint, inbound.Reader, body, udp, token).ConfigureAwait(false);
            }
            finally
            {
                ReleaseOnce();
            }
        });
    }

    private static async Task RelayAsync(MasqueProxyEndPoint endPoint, ChannelReader<ReadOnlyMemory<byte>> inbound,
        Stream body, Socket udp, CancellationToken cancellationToken)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = new CapsuleDatagramChannel(inbound, body);
        var budget = new DatagramBudget(endPoint.Options.MaxDatagramsPerSecond, endPoint.Options.MaxBytesPerSecond);
        var last = Environment.TickCount64;
        var idleMs = (long)endPoint.Options.IdleTimeout.TotalMilliseconds;
        var toTarget = channel.RunClientToTargetAsync(payload =>
        {
            last = Environment.TickCount64;
            if (!budget.TryTake(payload.Length))
            {
                endPoint.CountDrop();
                return;
            }

            try
            {
                udp.Send(payload);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                endPoint.CountDrop();
                return;
            }

            endPoint.CountDatagram(fromClient: true);
        }, relay.Token).AsTask();
        var toClient = ReceiveTargetAsync(endPoint, channel, udp, budget, () => last = Environment.TickCount64, relay.Token);
        var writer = channel.WriteQueuedAsync(relay.Token).AsTask();
        var idle = WatchIdleAsync(endPoint, () => last, idleMs, relay);
        await Task.WhenAny(toTarget, toClient, writer, idle).ConfigureAwait(false);
        await relay.CancelAsync().ConfigureAwait(false);
        try
        {
            udp.Close();
        }
        catch (Exception)
        {
            // Closing the socket unblocks the receive loop.
        }

        await Task.WhenAll(
            toTarget.ContinueWith(static _ => { }, TaskScheduler.Default),
            toClient.ContinueWith(static _ => { }, TaskScheduler.Default),
            writer.ContinueWith(static _ => { }, TaskScheduler.Default)).ConfigureAwait(false);
    }

    private static async Task ReceiveTargetAsync(MasqueProxyEndPoint endPoint, CapsuleDatagramChannel channel,
        Socket udp, DatagramBudget budget, Action touch, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CapsuleReader.DefaultMaxDatagramBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await udp.ReceiveAsync(buffer.AsMemory(0, CapsuleReader.DefaultMaxDatagramBytes),
                    cancellationToken).ConfigureAwait(false);
                touch();
                if (!budget.TryTake(read))
                {
                    endPoint.CountDrop();
                    continue;
                }

                if (!channel.TryQueueToClient(buffer.AsSpan(0, read)))
                {
                    endPoint.CountDrop();
                    continue;
                }

                endPoint.CountDatagram(fromClient: false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The tunnel is closing.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WatchIdleAsync(MasqueProxyEndPoint endPoint, Func<long> last, long idleMs,
        CancellationTokenSource relay)
    {
        while (!relay.IsCancellationRequested)
        {
            await Task.Delay(500, relay.Token).ConfigureAwait(false);
            if (Environment.TickCount64 - last() > idleMs)
            {
                endPoint.CountIdleClose();
                await relay.CancelAsync().ConfigureAwait(false);
                return;
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

            if (datagrams >= maxDatagrams || bytes + size > maxBytes)
            {
                return false;
            }

            datagrams++;
            bytes += size;
            return true;
        }
    }
}
