#pragma warning disable CA1416 // QUIC APIs are only supported on specific platforms; IsSupported is checked at runtime
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy;

/// <summary>
///     HTTP/3 QUIC endpoint lifecycle and accept-loop logic.
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Cancellation source used to stop all QUIC accept loops when the proxy is stopped.
    ///     Created on Start() and cancelled on StopCore().
    /// </summary>
    private CancellationTokenSource? quicListenerCts;

    /// <summary>
    ///     Starts a <see cref="QuicListener" /> for the given inbound QUIC endpoint.
    /// </summary>
    private void ListenQuic(IQuicInboundEndPoint endPoint)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException(
                "HTTP/3 (QUIC) requires the MsQuic native library and a supported OS. " +
                "CLI/Inspector: use the matching RID zip (natives are bundled) or run `titanium http3-deps install`. " +
                "Windows: Windows 11 / Server 2022+ (OS MsQuic). " +
                "NuGet library hosts: install system libmsquic (Linux/macOS) or use a supported Windows OS. " +
                "Alpine/K8s: use the linux-musl-* zip, not linux-x64. " +
                "Set ProxyServer.EnableHttp3 = false to disable. " +
                "(System.Net.Quic.QuicListener.IsSupported is false on this machine.)");

        var cts = quicListenerCts!;

        var listenEndPoint = new IPEndPoint(endPoint.IpAddress, endPoint.Port);
        // HttpClient resolves "localhost" to ::1 first. An IPv4-only Loopback QuicListener never
        // accepts that handshake (surfaces as ALPN failure). Dual-stack IPv6Any keeps the TCP port
        // and accepts both families for reverse dual-listen / loopback scenarios.
        if (IPAddress.IsLoopback(endPoint.IpAddress)
            || endPoint.IpAddress.Equals(IPAddress.Any)
            || endPoint.IpAddress.Equals(IPAddress.IPv6Any))
        {
            listenEndPoint = new IPEndPoint(IPAddress.IPv6Any, endPoint.Port);
        }

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = listenEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            ConnectionOptionsCallback = (connection, clientHello, cancellationToken) =>
                GetQuicServerConnectionOptionsAsync(endPoint, connection, clientHello, cancellationToken)
        };

        try
        {
            endPoint.QuicListener = QuicListener.ListenAsync(listenerOptions, cts.Token).AsTask()
                .GetAwaiter().GetResult();
            endPoint.AssignPort(endPoint.QuicListener.LocalEndPoint.Port);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"QUIC endpoint {endPoint.IpAddress}:{endPoint.Port} failed to start. " +
                "Check inner exception for details.", ex);
        }

        // Fire-and-forget: accept loop runs until cts is cancelled.
        _ = AcceptQuicConnectionsAsync(endPoint, cts.Token);
    }

    /// <summary>
    ///     Stops the <see cref="QuicListener" /> for the given endpoint.
    /// </summary>
    private static void QuitListenQuic(IQuicInboundEndPoint endPoint)
    {
        var listener = endPoint.QuicListener;
        endPoint.QuicListener = null;
        listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Builds <see cref="QuicServerConnectionOptions" /> for an inbound QUIC connection.
    /// </summary>
    private async ValueTask<QuicServerConnectionOptions> GetQuicServerConnectionOptionsAsync(
        IQuicInboundEndPoint endPoint,
        QuicConnection connection,
        SslClientHelloInfo clientHello,
        CancellationToken cancellationToken)
    {
        var sniHostName = clientHello.ServerName;
        var remoteEndPoint = connection.RemoteEndPoint;
        var localEndPoint = connection.LocalEndPoint;

        string destHost;
        int destPort;

        if (endPoint.OriginalDestinationResolver != null)
        {
            var resolved = await endPoint.OriginalDestinationResolver.ResolveAsync(
                localEndPoint, remoteEndPoint, sniHostName, cancellationToken);

            if (resolved.HasValue)
            {
                (destHost, destPort) = resolved.Value;
            }
            else if (endPoint.ForwardHost != null)
            {
                destHost = endPoint.ForwardHost;
                destPort = endPoint.ForwardPort ?? 443;
            }
            else
            {
                throw new InvalidOperationException(
                    $"IOriginalDestinationResolver returned null and no ForwardHost fallback is configured " +
                    $"on endpoint {endPoint.IpAddress}:{endPoint.Port}. " +
                    "Configure ForwardHost/ForwardPort or provide an IOriginalDestinationResolver.");
            }
        }
        else if (endPoint.ForwardHost != null)
        {
            destHost = endPoint.ForwardHost;
            destPort = endPoint.ForwardPort ?? 443;
        }
        else
        {
            destHost = sniHostName ?? endPoint.GenericCertificateName;
            destPort = 443;
        }

        using var connectionCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token);

        var eventArgs = new BeforeQuicAuthenticateEventArgs(
            this, connectionCts, sniHostName, destHost, destPort, remoteEndPoint, localEndPoint);

        await endPoint.InvokeBeforeQuicAuthenticate(this, eventArgs, Logger);

        if (linked.IsCancellationRequested)
            throw new OperationCanceledException(linked.Token);

        // MITM leaf must match client SNI / GenericCertificateName — ForwardHost is the origin target.
        var certHost = !string.IsNullOrEmpty(eventArgs.SniHostName)
            ? eventArgs.SniHostName
            : endPoint.GenericCertificateName;

        endPoint.PendingQuicAuthArgs.AddOrUpdate(connection, eventArgs);

        var cert = await CertificateManager.CreateServerCertificate(certHost)
            ?? throw new InvalidOperationException(
                $"CertificateManager could not produce a certificate for '{certHost}'.");

        var serverAuthOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SupportedServerSslProtocols,
            CertificateRevocationCheckMode = CheckCertificateRevocation,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            }
        };

        return new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ServerAuthenticationOptions = serverAuthOptions,
            MaxInboundBidirectionalStreams = endPoint.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = Math.Max(3, endPoint.MaxInboundUnidirectionalStreams),
            IdleTimeout = endPoint.IdleTimeout,
            HandshakeTimeout = endPoint.HandshakeTimeout
        };
    }

    /// <summary>
    ///     Bounded accept loop for a QUIC endpoint.
    /// </summary>
    private async Task AcceptQuicConnectionsAsync(
        IQuicInboundEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        var listener = endPoint.QuicListener;
        if (listener == null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error accepting QUIC connection on {EndPoint}", endPoint.ProxyEndPoint);
                continue;
            }

            if (!endPoint.PendingQuicAuthArgs.TryGetValue(connection, out var authArgs))
            {
                _ = connection.CloseAsync(0x100, cancellationToken).AsTask();
                continue;
            }

            endPoint.PendingQuicAuthArgs.Remove(connection);
            _ = HandleQuicConnectionAsync(connection, endPoint, authArgs, cancellationToken);
        }
    }

    /// <summary>
    ///     Handles a single accepted QUIC connection.
    /// </summary>
    private async Task HandleQuicConnectionAsync(
        QuicConnection connection,
        IQuicInboundEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        CancellationToken cancellationToken)
    {
        await using (connection)
        {
            try
            {
                await Http3Connection.RunAsync(
                    connection, endPoint.ProxyEndPoint, authArgs, this, Logger, cancellationToken,
                    onBeforeRequest: OnBeforeRequest,
                    onBeforeResponse: OnBeforeResponse,
                    onAfterResponse: OnAfterResponse);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "QUIC connection closed with error");
            }
            finally
            {
                await connection.CloseAsync(0x100 /* H3_NO_ERROR */, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Injects client-facing <c>Alt-Svc</c> for dual-listen reverse HTTP/3 endpoints when the
    ///     origin response did not already advertise one.
    /// </summary>
    private static void MaybeInjectClientAltSvc(SessionEventArgs args)
    {
        if (args.ProxyEndPoint is not TransparentProxyEndPoint { EnableHttp3: true } ep)
            return;

        var response = args.HttpClient.Response;
        if (response.Headers.HeaderExists(KnownHeaders.AltSvc.String))
            return;

        response.Headers.AddHeader(KnownHeaders.AltSvc, $"h3=\":{ep.Port}\"; ma=86400");
    }
}
