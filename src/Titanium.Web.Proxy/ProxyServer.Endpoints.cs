using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

/// <inheritdoc />
/// <summary>
///     This class is the backbone of proxy. One can create as many instances as needed.
///     However care should be taken to avoid using the same listening ports across multiple instances.
/// </summary>
public partial class ProxyServer : IDisposable
{
    /// <summary>
    ///     Add a proxy end point.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    public void AddEndPoint(ProxyEndPoint endPoint) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (ProxyEndPoints.Any(x =>
                x.IpAddress.Equals(endPoint.IpAddress) && endPoint.Port != 0 && x.Port == endPoint.Port))
            throw new InvalidOperationException("Cannot add another endpoint to same port & ip address");

        ProxyEndPoints.Add(endPoint);

        if (ProxyRunning && endPoint is TransparentQuicProxyEndPoint quicEndPoint)
        {
            quicListenerCts ??= new CancellationTokenSource();
            ListenQuic(quicEndPoint);
        }
        else if (ProxyRunning)
        {
            var wantDualQuic = EnableHttp3 && endPoint is TransparentProxyEndPoint { EnableHttp3: true };
            var ephemeralDual = wantDualQuic && endPoint.Port == 0;
            var dualAttempts = 0;
            while (true)
            {
                dualAttempts++;
                Listen(endPoint);

                if (!wantDualQuic)
                    break;

                var dualListen = (TransparentProxyEndPoint)endPoint;
                if (!dualListen.DecryptSsl)
                    throw new InvalidOperationException(
                        "TransparentProxyEndPoint.EnableHttp3 requires DecryptSsl = true.");

                try
                {
                    quicListenerCts ??= new CancellationTokenSource();
                    ListenQuic(dualListen);
                    break;
                }
                catch (Exception ex) when (ephemeralDual && dualAttempts < MaxEphemeralDualListenAttempts
                                           && IsAddressAlreadyInUse(ex))
                {
                    QuitListenQuic(dualListen);
                    QuitListen(endPoint);
                    endPoint.Port = 0;
                }
            }
        }
    }

    /// <summary>
    ///     Starts the HTTP/3 (QUIC) side of a decrypting <see cref="TransparentProxyEndPoint" /> that is already added
    ///     and listening over TCP, without touching its TCP listener. A host that must not hold the UDP port yet (a hot
    ///     restart successor while the old process still owns it) adds the endpoint with
    ///     <see cref="TransparentProxyEndPoint.EnableHttp3" /> off and calls this once the port is free. On failure
    ///     the endpoint stays TCP only and the exception propagates.
    /// </summary>
    /// <param name="endPoint">An endpoint previously passed to <see cref="AddEndPoint" /> while the proxy is running.</param>
    /// <exception cref="InvalidOperationException">
    ///     The proxy is not running, the endpoint was not added, it does not decrypt TLS, or
    ///     <see cref="EnableHttp3" /> is off.
    /// </exception>
    public void StartHttp3Listener(TransparentProxyEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (!ProxyRunning)
            throw new InvalidOperationException("The proxy must be running to start an HTTP/3 listener.");

        if (!ProxyEndPoints.Contains(endPoint))
            throw new InvalidOperationException("Cannot start HTTP/3 on an endpoint that was not added to the proxy.");

        if (!endPoint.DecryptSsl)
            throw new InvalidOperationException("TransparentProxyEndPoint.EnableHttp3 requires DecryptSsl = true.");

#pragma warning disable TWP001
        if (!EnableHttp3)
            throw new InvalidOperationException("ProxyServer.EnableHttp3 is off.");
#pragma warning restore TWP001

        var wasEnabled = endPoint.EnableHttp3;
        endPoint.EnableHttp3 = true;
        try
        {
            quicListenerCts ??= new CancellationTokenSource();
            ListenQuic(endPoint);
        }
        catch
        {
            endPoint.EnableHttp3 = wasEnabled;
            throw;
        }
    }

    /// <summary>
    ///     Closes every TCP listening socket and leaves everything else alone: endpoints stay registered, connections
    ///     that were already accepted keep running, and QUIC listeners keep their UDP ports. A hot-restart predecessor
    ///     calls this on the handover signal so the kernel stops distributing new connections to it (with
    ///     SO_REUSEPORT its socket would otherwise keep receiving, and dropping, a share of them for the whole drain).
    ///     Connections still queued unaccepted on a closed socket are reset by the OS. Not reversible until
    ///     <see cref="Stop" /> and <see cref="Start" />.
    /// </summary>
    public void CloseTcpListeners()
    {
        foreach (var endPoint in ProxyEndPoints.ToList())
        {
            if (endPoint is TransparentQuicProxyEndPoint || endPoint.Listener is not { } listener)
                continue;

            // The accept loop ends on the disposed socket. AcceptLoopCts is left alone: cancelling it would also
            // abort the passthrough relays that were accepted through this listener.
            try
            {
                listener.Stop();
                listener.Server.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        }
    }

    /// <summary>
    ///     Remove a proxy end point.
    ///     Will throw error if the end point doesn't exist.
    /// </summary>
    /// <param name="endPoint">The existing endpoint to remove.</param>
    public void RemoveEndPoint(ProxyEndPoint endPoint)
    {
        if (!ProxyEndPoints.Contains(endPoint))
            throw new InvalidOperationException("Cannot remove endPoints not added to proxy");

        ProxyEndPoints.Remove(endPoint);

        if (ProxyRunning && endPoint is TransparentQuicProxyEndPoint quicEndPoint)
            QuitListenQuic(quicEndPoint);
        else if (ProxyRunning)
        {
            if (endPoint is TransparentProxyEndPoint { EnableHttp3: true } dualListen)
                QuitListenQuic(dualListen);
            QuitListen(endPoint);
        }
    }
    /// <summary>
    ///     Update client connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateClientConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref clientConnectionCount);
        else
            Interlocked.Decrement(ref clientConnectionCount);

        try
        {
            ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update server connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateServerConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref serverConnectionCount);
        else
            Interlocked.Decrement(ref serverConnectionCount);

        try
        {
            ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update inbound HTTP/3 client connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateHttp3ClientConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref http3ClientConnectionCount);
        else
            Interlocked.Decrement(ref http3ClientConnectionCount);

        try
        {
            Http3ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update upstream HTTP/3 server connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateHttp3ServerConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref http3ServerConnectionCount);
        else
            Interlocked.Decrement(ref http3ServerConnectionCount);

        try
        {
            Http3ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Invoke client tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="clientSocket">The TcpClient object.</param>
    /// <returns></returns>
    internal Task InvokeClientConnectionCreateEvent(Socket clientSocket)
    {
        return OnClientConnectionCreate != null
            ? OnClientConnectionCreate.InvokeAsync(this, clientSocket, logger)
            : Task.CompletedTask;
    }

    /// <summary>
    ///     Invoke server tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="serverSocket">The Socket object.</param>
    /// <returns></returns>
    internal Task InvokeServerConnectionCreateEvent(Socket serverSocket)
    {
        return OnServerConnectionCreate != null
            ? OnServerConnectionCreate.InvokeAsync(this, serverSocket, logger)
            : Task.CompletedTask;
    }
}
