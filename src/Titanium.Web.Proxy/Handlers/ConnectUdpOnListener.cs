using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     CONNECT-UDP on an existing listener. Same target policy, auth, and caps as a MASQUE listener.
    ///     The client does not get a UDP socket unless <see cref="ProxyEndPoint.AcceptConnectUdp"/> is set.
    /// </summary>
    internal async Task RelayConnectUdpAsync(SessionEventArgs session, int streamId,
        Http2ConnectionState connectionState, System.IO.Stream clientStream, Http2StreamState streamState,
        CancellationToken cancellationToken)
    {
        var endPoint = session.ProxyEndPoint;
        if (!endPoint.AcceptConnectUdp)
        {
            session.GenericResponse("connect-udp is not enabled on this listener", HttpStatusCode.NotImplemented);
            await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken);
            return;
        }

        var host = endPoint.ConnectUdpHost;
        if (host.Options.RequireAuth && !await ConnectUdpAuthenticatedAsync(host, session).ConfigureAwait(false))
        {
            session.GenericResponse("proxy authentication required", HttpStatusCode.ProxyAuthenticationRequired,
                [new HttpHeader("proxy-authenticate", "Basic realm=\"masque\"")]);
            await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken);
            return;
        }

        var request = session.HttpClient.Request;
        if (!ConnectUdpPath.TryParse(request.RequestUriString, out var targetHost, out var targetPort))
        {
            session.GenericResponse("connect-udp path is not /.well-known/masque/udp/{host}/{port}/", HttpStatusCode.BadRequest);
            await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken);
            return;
        }

        var client = MasqueProxyEndPoint.ClientKey(session.ClientRemoteEndPoint.Address);
        if (!host.TryAdmit(client))
        {
            session.GenericResponse("too many connect-udp tunnels", (HttpStatusCode)429,
                [new HttpHeader("retry-after", "1")]);
            await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken);
            return;
        }

        var ownsSlot = true;
        try
        {
            var (status, reason, opened) = await MasqueTunnel.OpenAsync(
                host, targetHost, targetPort, logger, cancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                session.GenericResponse(reason ?? "connect-udp target was rejected", (HttpStatusCode)(status == 0 ? 502 : status));
                await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken);
                return;
            }

            if (streamState.InboundTunnelChannel == null)
            {
                streamState.InboundTunnelChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });
            }

            streamState.IsExtendedConnect = true;
            var released = 0;
            void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                {
                    return;
                }

                streamState.InboundTunnelChannel.Writer.TryComplete();
                opened.Release();
                host.ReleaseTunnel(client);
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
                    await MasqueTunnel.RelayAsync(host, streamState.InboundTunnelChannel.Reader, body, opened.Socket, logger, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ReleaseOnce();
                }
            });
            await Http2Helper.EmitSyntheticResponseAsync(session, streamId, connectionState, clientStream, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsSlot)
            {
                host.ReleaseTunnel(client);
            }
        }
    }

    private async Task<bool> ConnectUdpAuthenticatedAsync(MasqueProxyEndPoint endPoint, SessionEventArgs session)
    {
        var header = session.HttpClient.Request.Headers.GetFirstHeader("proxy-authorization")?.Value;
        const string prefix = "Basic ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[prefix.Length..].Trim()));
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
            && await ProxyBasicAuthenticateFunc(session, user, password).ConfigureAwait(false);
    }

    private async Task RunConnectUdpOnListenerAsync(SessionEventArgs session, Http2StreamContext ctx,
        Http2StreamState streamState, CancellationToken cancellationToken)
    {
        await RelayConnectUdpAsync(session, ctx.StreamId, ctx.ConnectionState, ctx.ClientStream, streamState, cancellationToken)
            .ConfigureAwait(false);
    }
}
