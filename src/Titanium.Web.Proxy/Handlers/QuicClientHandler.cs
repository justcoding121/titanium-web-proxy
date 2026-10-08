#pragma warning disable CA1416 // QUIC APIs are only supported on specific platforms; IsSupported is checked at runtime
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
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
    ///     Starts inbound <see cref="QuicListener" />s for the endpoint.
    ///     Loopback (<c>127.0.0.1</c> or <c>::1</c>) listens on both families at the same port.
    ///     <see cref="IPAddress.Any" /> and <see cref="IPAddress.IPv6Any" /> stay on <c>::</c>.
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

        try
        {
            // A host without IPv6 has no ::1 to bind, so keep the configured address alone.
            if (IsExactLoopback(endPoint.IpAddress) && Socket.OSSupportsIPv6)
                ListenLoopbackQuic(endPoint, cts);
            else
                ListenSingleQuic(endPoint, cts);

            // Fire-and-forget: accept loops run until cts is cancelled.
            _ = AcceptQuicConnectionsAsync(endPoint, cts.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"QUIC endpoint {endPoint.IpAddress}:{endPoint.Port} failed to start. " +
                "Check inner exception for details.", ex);
        }
    }

    /// <summary>
    ///     One listener. Wildcard addresses use dual-stack <see cref="IPAddress.IPv6Any" /> so a
    ///     real all-interfaces proxy still accepts both families.
    /// </summary>
    private void ListenSingleQuic(IQuicInboundEndPoint endPoint, CancellationTokenSource cts)
    {
        var address = IsWildcard(endPoint.IpAddress) ? IPAddress.IPv6Any : endPoint.IpAddress;
        var listener = BindQuicListener(endPoint, new IPEndPoint(address, endPoint.Port), cts.Token);
        endPoint.QuicListener = listener;
        endPoint.AssignPort(listener.LocalEndPoint.Port);
    }

    /// <summary>
    ///     <c>::1</c> and <c>127.0.0.1</c> on the same port. HttpClient to <c>https://localhost</c>
    ///     prefers <c>::1</c>; <c>QuicHttp3Client</c> to <see cref="IPAddress.Loopback" /> uses
    ///     <c>127.0.0.1</c>. One family misses the other and surfaces as an ALPN failure.
    ///     Bind <c>::1</c> first so an ephemeral port is chosen there, then bind IPv4 to that port.
    ///     Dual-listen already has the TCP port and binds both to it.
    /// </summary>
    private void ListenLoopbackQuic(IQuicInboundEndPoint endPoint, CancellationTokenSource cts)
    {
        var ephemeral = endPoint.Port == 0;
        const int maxAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            var v6 = BindQuicListener(
                endPoint, new IPEndPoint(IPAddress.IPv6Loopback, endPoint.Port), cts.Token);
            endPoint.QuicListener = v6;
            var port = v6.LocalEndPoint.Port;
            endPoint.AssignPort(port);
            try
            {
                endPoint.LoopbackV4QuicListener = BindQuicListener(
                    endPoint, new IPEndPoint(IPAddress.Loopback, port), cts.Token);
                return;
            }
            catch (Exception ex) when (ephemeral && attempt < maxAttempts && IsAddressAlreadyInUse(ex))
            {
                DisposeQuicListener(v6);
                endPoint.QuicListener = null;
                endPoint.LoopbackV4QuicListener = null;
                endPoint.AssignPort(0);
            }
            catch (Exception)
            {
                // Start() only rolls back listeners it has already published. A ::1 socket left
                // assigned here would stay bound after the IPv4 bind fails.
                DisposeQuicListener(v6);
                endPoint.QuicListener = null;
                endPoint.LoopbackV4QuicListener = null;
                throw;
            }
        }
    }

    private QuicListener BindQuicListener(
        IQuicInboundEndPoint endPoint, IPEndPoint listenEndPoint, CancellationToken cancellationToken)
    {
        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = listenEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            ConnectionOptionsCallback = (connection, clientHello, callbackToken) =>
                GetQuicServerConnectionOptionsAsync(endPoint, connection, clientHello, callbackToken)
        };

        return QuicListener.ListenAsync(listenerOptions, cancellationToken).AsTask()
            .GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Stops every <see cref="QuicListener" /> for the given endpoint.
    /// </summary>
    private static void QuitListenQuic(IQuicInboundEndPoint endPoint)
    {
        var primary = endPoint.QuicListener;
        var v4 = endPoint.LoopbackV4QuicListener;
        endPoint.QuicListener = null;
        endPoint.LoopbackV4QuicListener = null;
        try
        {
            DisposeQuicListener(primary);
        }
        finally
        {
            DisposeQuicListener(v4);
        }
    }

    private static void DisposeQuicListener(QuicListener? listener)
    {
        listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     <c>127.0.0.1</c> and <c>::1</c> only. <see cref="IPAddress.IsLoopback" /> is true for all of
    ///     127/8, which must stay on the address the caller configured.
    /// </summary>
    private static bool IsExactLoopback(IPAddress address) =>
        IPAddress.Loopback.Equals(address) || IPAddress.IPv6Loopback.Equals(address);

    private static bool IsWildcard(IPAddress address) =>
        IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address);

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

        var connectionCts = new CancellationTokenSource();
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
    ///     Accept loops for every listener on the endpoint (one, or both loopback families).
    /// </summary>
    private Task AcceptQuicConnectionsAsync(
        IQuicInboundEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        var primary = endPoint.QuicListener;
        var v4 = endPoint.LoopbackV4QuicListener;
        if (primary == null)
            return v4 == null
                ? Task.CompletedTask
                : AcceptQuicListenerAsync(endPoint, v4, cancellationToken);

        return v4 == null
            ? AcceptQuicListenerAsync(endPoint, primary, cancellationToken)
            : Task.WhenAll(
                AcceptQuicListenerAsync(endPoint, primary, cancellationToken),
                AcceptQuicListenerAsync(endPoint, v4, cancellationToken));
    }

    /// <summary>
    ///     Bounded accept loop for one <see cref="QuicListener" />.
    /// </summary>
    private async Task AcceptQuicListenerAsync(
        IQuicInboundEndPoint endPoint,
        QuicListener listener,
        CancellationToken cancellationToken)
    {
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
                // Options callback never completed or auth args were lost — close without CTS ownership.
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
            using var connectionCts = authArgs.TaskCancellationSource;
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
