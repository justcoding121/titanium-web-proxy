#pragma warning disable CA1416 // QUIC APIs are only supported on specific platforms; IsSupported is checked at runtime
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
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
    ///     Starts a <see cref="QuicListener" /> for the given <see cref="TransparentQuicProxyEndPoint" />.
    ///     Called from <see cref="Start" /> for each QUIC endpoint in <see cref="ProxyEndPoints" />.
    /// </summary>
    private void ListenQuic(TransparentQuicProxyEndPoint endPoint)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException(
                "HTTP/3 (QUIC) requires the MsQuic native library and a supported OS. " +
                "Windows: Windows 11 / Server 2022+. " +
                "Linux: install libmsquic (e.g. apt install libmsquic). " +
                "macOS: bundle libmsquic, libssl, and libcrypto alongside the app with @loader_path RPATH. " +
                "Set ProxyServer.EnableHttp3 = false to disable. " +
                "(System.Net.Quic.QuicListener.IsSupported is false on this machine.)");

        var cts = quicListenerCts!;

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(endPoint.IpAddress, endPoint.Port),
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            ConnectionOptionsCallback = (connection, clientHello, cancellationToken) =>
                GetQuicServerConnectionOptionsAsync(endPoint, connection, clientHello, cancellationToken)
        };

        try
        {
            endPoint.QuicListener = QuicListener.ListenAsync(listenerOptions, cts.Token).GetAwaiter().GetResult();
            endPoint.Port = endPoint.QuicListener.LocalEndPoint.Port;
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
    ///     Stops the <see cref="QuicListener" /> for the given endpoint and waits for the accept loop
    ///     to exit. Called from <see cref="StopCore" />.
    /// </summary>
    private static void QuitListenQuic(TransparentQuicProxyEndPoint endPoint)
    {
        var listener = endPoint.QuicListener;
        endPoint.QuicListener = null;
        listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Builds <see cref="QuicServerConnectionOptions" /> for an inbound QUIC connection by:
    ///     <list type="number">
    ///       <item><description>Resolving the original (pre-NAT) destination via <see cref="IOriginalDestinationResolver" />.</description></item>
    ///       <item><description>Firing the <see cref="TransparentQuicProxyEndPoint.BeforeQuicAuthenticate" /> event.</description></item>
    ///       <item><description>Obtaining a MITM certificate from <see cref="CertificateManager" />.</description></item>
    ///     </list>
    /// </summary>
    private async ValueTask<QuicServerConnectionOptions> GetQuicServerConnectionOptionsAsync(
        TransparentQuicProxyEndPoint endPoint,
        QuicConnection connection,
        SslClientHelloInfo clientHello,
        CancellationToken cancellationToken)
    {
        var sniHostName = clientHello.ServerName;
        var remoteEndPoint = (IPEndPoint)connection.RemoteEndPoint;
        var localEndPoint = (IPEndPoint)connection.LocalEndPoint;

        // Resolve original (pre-NAT) destination.
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
                // Cannot determine destination — reject.
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
            // Fall back to SNI as last resort (unreliable for non-SNI clients or IP-literal URLs).
            destHost = sniHostName ?? endPoint.GenericCertificateName;
            destPort = 443;
        }

        // Fire BeforeQuicAuthenticate — allows operator to override destination, protocol policy, or reject.
        using var connectionCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token);

        var eventArgs = new BeforeQuicAuthenticateEventArgs(
            this, connectionCts, sniHostName, destHost, destPort, remoteEndPoint, localEndPoint);

        await endPoint.InvokeBeforeQuicAuthenticate(this, eventArgs, Logger);

        if (linked.IsCancellationRequested)
        {
            // Rejected by the event handler — abort early.
            throw new OperationCanceledException(linked.Token);
        }

        // Use the (possibly overridden) forward target for certificate selection.
        var certHost = eventArgs.ForwardHost;

        // Store auth args so the accept loop can retrieve them when the connection is accepted.
        endPoint.PendingQuicAuthArgs.AddOrUpdate(connection, eventArgs);

        // Obtain or generate a MITM leaf certificate for this hostname.
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
            }        };

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
    ///     Bounded accept loop for a QUIC endpoint. Runs until <paramref name="cancellationToken" /> is
    ///     cancelled (on proxy stop) or the listener is disposed.
    /// </summary>
    private async Task AcceptQuicConnectionsAsync(
        TransparentQuicProxyEndPoint endPoint,
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
                Logger.LogError(ex, "Error accepting QUIC connection on {EndPoint}", endPoint);
                continue;
            }

            // Handle each QUIC connection on its own task — do not await.
            if (!endPoint.PendingQuicAuthArgs.TryGetValue(connection, out var authArgs))
            {
                // No auth args means the options callback failed or was skipped — reject this connection.
                _ = connection.CloseAsync(0x100, cancellationToken).AsTask();
                continue;
            }
            endPoint.PendingQuicAuthArgs.Remove(connection);
            _ = HandleQuicConnectionAsync(connection, endPoint, authArgs, cancellationToken);
        }
    }

    /// <summary>
    ///     Handles a single accepted QUIC connection: runs the H3 stream accept loop for the lifetime of
    ///     the connection.
    /// </summary>
    private async Task HandleQuicConnectionAsync(
        QuicConnection connection,
        TransparentQuicProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        CancellationToken cancellationToken)
    {
        await using (connection)
        {
            try
            {
                await Http3Connection.RunAsync(
                    connection, endPoint, authArgs, this, Logger, cancellationToken,
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
}
