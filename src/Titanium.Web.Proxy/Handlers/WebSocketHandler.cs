using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Handle upgrade to websocket
    /// </summary>
    private async Task HandleWebSocketUpgrade(SessionEventArgs args,
        HttpClientStream clientStream, TcpServerConnection serverConnection,
        CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken)
    {
        await serverConnection.Stream.WriteRequestAsync(args.HttpClient.Request, cancellationToken);

        // WebSocket upgrades are exempt from short response-header deadlines; use idle-read if configured.
        using (var idleScope = ProxyTimeoutScope.Create(cancellationToken,
                   ResolveIdleReadTimeout(args), ProxyTimeoutKind.IdleRead))
        {
            try
            {
                var httpStatus = await serverConnection.Stream.ReadResponseStatus(idleScope.Token)
                                 ?? throw new IOException(
                                     "Server closed the connection before sending a WebSocket upgrade response.");

                var upgradeResponse = args.HttpClient.Response;
                upgradeResponse.HttpVersion = httpStatus.Version;
                upgradeResponse.StatusCode = httpStatus.StatusCode;
                upgradeResponse.StatusDescription = httpStatus.Description;

                await HeaderParser.ReadHeaders(serverConnection.Stream, upgradeResponse.Headers, idleScope.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException || idleScope.IsTimedOut())
            {
                idleScope.ThrowIfTimedOut(ex);
                throw;
            }
        }

        args.Timing?.MarkResponseHeadersReceived();

        // If user requested call back then do it - before the response is written to the client, matching
        // every other response path (see ResponseHandler.HandleHttpSessionResponse's OnBeforeResponse call).
        // This lets a subscriber inspect/modify the upgrade response, or deny the upgrade entirely via
        // args.Respond(...), before anything reaches the wire. Previously this fired only after the
        // original server response had already been written to the client, so any change made here was
        // silently lost and a denied upgrade would still fall through to the raw relay below.
        if (!args.HttpClient.Response.Locked) await OnBeforeResponse(args);

        // it may have changed in the user event
        var response = args.HttpClient.Response;
        var userReplacedResponse = response.Locked;
        response.Locked = true;

        await clientStream.WriteResponseAsync(response, cancellationToken);
        args.IsClientResponseCommitted = true;

        // The upgrade handshake is what "request timing" means for a WebSocket session - mark it complete
        // here rather than leaving it to the shared OnAfterResponse chokepoint (see its remarks), which
        // only runs once the raw relay below returns and would otherwise make TotalDuration cover the
        // entire (potentially very long-lived) WebSocket connection instead of just the HTTP upgrade.
        args.Timing?.MarkComplete();

        // A BeforeResponse handler that replaced the response (e.g. to deny the upgrade) has taken full
        // control of the exchange - same as the normal HTTP response path, there is nothing left to relay:
        // the server connection is being torn down by the caller regardless (WebSocket sessions are never
        // pooled), so any unread bytes left over from the original server response are simply discarded
        // along with it.
        if (userReplacedResponse) return;

        // Frame-level interception when BeforeWebSocketFrame is subscribed; otherwise keep the
        // zero-overhead raw byte relay. DataSent/DataReceived still fire for written bytes.
        if (args.HasWebSocketFrameInterceptHandler)
        {
            await WebSocketInterceptRelay.RelayAsync(clientStream, serverConnection.Stream, BufferPool,
                args, cancellationTokenSource);
        }
        else
        {
            await TcpHelper.SendRaw(clientStream, serverConnection.Stream, BufferPool,
                args.OnDataSent, args.OnDataReceived, cancellationTokenSource, logger);
        }
    }
}