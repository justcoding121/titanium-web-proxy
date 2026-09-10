using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

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
            const int maxDualListenAttempts = 20;
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
                catch (Exception ex) when (ephemeralDual && dualAttempts < maxDualListenAttempts
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
