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
    ///     Start this proxy server instance.
    ///     <para>
    ///         Transactional: if any endpoint fails to start, every listener this call already
    ///         started is stopped, the system-upstream-proxy resolver (if this call created one) is
    ///         disposed, and <see cref="ProxyRunning" /> is left <see langword="false" /> before the
    ///         exception propagates. A caller that catches the exception is left with an instance in
    ///         exactly the same state as before calling <see cref="Start" />, not a partially-bound
    ///         proxy with some endpoints silently listening.
    ///     </para>
    /// </summary>
    /// <param name="changeSystemProxySettings">
    ///     Whether or not clear any system proxy settings which is pointing to our own endpoint (causing a cycle).
    ///     E.g due to ungracious proxy shutdown before.
    /// </param>
    public void Start(bool changeSystemProxySettings = true) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (ProxyRunning) throw new InvalidOperationException("Proxy is already running.");

        // Freeze the active logging configuration for the duration of this run.
        ApplyLoggingConfiguration();

        SetThreadPoolMinThread(ThreadPoolWorkerThread);

        // Only create the root certificate when at least one endpoint will actually perform
        // TLS decryption and does not already have a custom GenericCertificate.  Endpoints
        // whose DecryptSsl is false never need to generate leaf certificates, so creating a
        // root PFX for them is unnecessary I/O and key-generation work.
        if (ProxyEndPoints.Any(x => x.DecryptSsl && x.GenericCertificate == null))
            CertificateManager.EnsureRootCertificate();

        if (changeSystemProxySettings && SystemProxySettingsManager != null)
        {
            try
            {
                var ownedPorts = ProxyEndPoints.Select(x => x.Port).ToHashSet();
                var protocolToRemove = SystemProxySettingsManager.GetStaleLocalProxyProtocols(ownedPorts);
                if (protocolToRemove != ProxyProtocolType.None)
                    SystemProxySettingsManager.RemoveProxy(protocolToRemove, false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Clearing stale system proxy on Start failed (continuing)");
            }
        }

        var assignedSystemUpStreamResolver = false;
        if (RunTime.IsWindows && ForwardToUpstreamGateway && GetCustomUpStreamProxyFunc == null &&
            SystemProxySettingsManager != null)
        {
            systemProxyResolver = new WinHttpWebProxyFinder();
            if (UpstreamProxyConfigurationScript != null)
                //Use the provided proxy configuration script
                systemProxyResolver.UsePacFile(UpstreamProxyConfigurationScript);
            else
                // Use WinHttp to handle PAC/WAPD scripts.
                systemProxyResolver.LoadFromIe();

            GetCustomUpStreamProxyFunc = GetSystemUpStreamProxy;
            assignedSystemUpStreamResolver = true;
        }

        ProxyRunning = true;

        // Name only, per the plan's rollout section - never hosts, URLs or secrets.
        ProxyLog.EffectiveProfileAtStartup(logger, profile, policyModes);

        _ = CertificateManager.ClearIdleCertificates();

        var startedTcpEndPoints = new List<ProxyEndPoint>();
        var startedQuicEndPoints = new List<IQuicInboundEndPoint>();
        var createdQuicListenerCts = false;

        try
        {
            var hasUdpOnlyQuic = ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>().Any();
            var hasDualListenHttp3 = ProxyEndPoints.OfType<TransparentProxyEndPoint>()
                .Any(e => e.EnableHttp3);
            var needsInboundHttp3 = EnableHttp3 && (hasUdpOnlyQuic || hasDualListenHttp3);

            if (needsInboundHttp3)
                quicListenerCts = new CancellationTokenSource();
            else if (EnableHttp3)
            {
                // Explicit/SOCKS endpoints speak TCP to the client; EnableHttp3 still correctly
                // arms origin-side QUIC (Alt-Svc / H2↔H3 bridge). That is the Inspector/CLI happy
                // path — do not warn. Warn only when nothing can use either inbound or origin H3
                // (no client-facing TCP endpoints), which usually means a misconfigured Start().
                var hasTcpClientFacingEndpoint = ProxyEndPoints.Any(e =>
                    e is ExplicitProxyEndPoint or SocksProxyEndPoint or TransparentProxyEndPoint);
                if (!hasTcpClientFacingEndpoint)
                {
                    Logger.LogWarning(
                        "EnableHttp3 is true but no inbound HTTP/3 endpoint is registered. " +
                        "Add a TransparentQuicProxyEndPoint, or a TransparentProxyEndPoint with EnableHttp3, " +
                        "before calling Start().");
                }
            }

            // UDP-only transparent QUIC first (no TCP on that port).
            if (needsInboundHttp3 && hasUdpOnlyQuic)
            {
                createdQuicListenerCts = true;
                foreach (var quicEndPoint in ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>())
                {
                    ListenQuic(quicEndPoint);
                    startedQuicEndPoints.Add(quicEndPoint);
                }
            }

            // TCP endpoints. Dual-listen reverse H3: bind TCP first (assign ephemeral port), then UDP
            // on the same IP:port so HttpClient can discover H3 via Alt-Svc or RequestVersionExact.
            // Windows TCP and UDP port spaces are independent — ephemeral TCP can land on a UDP port
            // that is already taken / excluded (WSAEADDRINUSE). Retry ephemeral dual-listen binds.
            foreach (var endPoint in ProxyEndPoints)
            {
                if (endPoint is TransparentQuicProxyEndPoint)
                    continue;

                if (endPoint is TransparentProxyEndPoint { EnableHttp3: true } dualListenGate)
                {
                    if (!EnableHttp3)
                        throw new InvalidOperationException(
                            "TransparentProxyEndPoint.EnableHttp3 requires ProxyServer.EnableHttp3 = true.");
                    if (!dualListenGate.DecryptSsl)
                        throw new InvalidOperationException(
                            "TransparentProxyEndPoint.EnableHttp3 requires DecryptSsl = true.");
                }

                var wantDualQuic = EnableHttp3 && endPoint is TransparentProxyEndPoint { EnableHttp3: true };
                var ephemeralDual = wantDualQuic && endPoint.Port == 0;
                const int maxDualListenAttempts = 20;
                var dualAttempts = 0;
                while (true)
                {
                    dualAttempts++;
                    Listen(endPoint);

                    if (!wantDualQuic)
                    {
                        startedTcpEndPoints.Add(endPoint);
                        break;
                    }

                    var dual = (TransparentProxyEndPoint)endPoint;
                    try
                    {
                        createdQuicListenerCts = true;
                        quicListenerCts ??= new CancellationTokenSource();
                        ListenQuic(dual);
                        startedTcpEndPoints.Add(endPoint);
                        startedQuicEndPoints.Add(dual);
                        break;
                    }
                    catch (Exception ex) when (ephemeralDual && dualAttempts < maxDualListenAttempts
                                               && IsAddressAlreadyInUse(ex))
                    {
                        SafeRollback(() => QuitListenQuic(dual));
                        SafeRollback(() => QuitListen(endPoint));
                        endPoint.Port = 0;
                    }
                }
            }
        }
        catch (Exception startEx)
        {
            // Roll back, in reverse dependency order, everything this call already started.
            // QuitListen/QuitListenQuic tolerate a listener that never started (no-op), so it is
            // safe to call them uniformly rather than re-deriving exactly how far each one got.
            ProxyDiagnostics.ReportCaught(logger,
                "ProxyServer.Start failed; rolling back and rethrowing", startEx);
            foreach (var quicEndPoint in startedQuicEndPoints) SafeRollback(() => QuitListenQuic(quicEndPoint));
            foreach (var endPoint in startedTcpEndPoints) SafeRollback(() => QuitListen(endPoint));

            if (createdQuicListenerCts)
            {
                SafeRollback(() => quicListenerCts?.Cancel());
                SafeRollback(() => quicListenerCts?.Dispose());
                quicListenerCts = null;
            }

            if (assignedSystemUpStreamResolver)
            {
                if (OperatingSystem.IsWindows())
                    try
                    {
                        systemProxyResolver?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        OnException(null, ex);
                    }

                systemProxyResolver = null;
                GetCustomUpStreamProxyFunc = null;
            }

            ProxyRunning = false;

            throw;
        }
    }

    /// <summary>
    ///     Runs a single <see cref="Start" /> rollback step, reporting rather than propagating a
    ///     failure so one misbehaving teardown step cannot mask the original failure or abandon the
    ///     rest of the rollback.
    /// </summary>
    private void SafeRollback(Action rollbackStep)
    {
        try
        {
            rollbackStep();
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Stop this proxy server instance.
    ///     Endpoints remain registered so <see cref="Start" /> can re-listen on the same ports.
    ///     In-flight sessions are cancelled; pooled upstream connections are cleared. The connection
    ///     factory itself stays usable for a subsequent Start (it is only disposed with the proxy).
    /// </summary>
    public void Stop()
    {
        StopCore(cancelSessions: true, clearPools: true);
    }

    /// <summary>
    ///     Asynchronously stop this proxy server, cancel in-flight sessions, and wait briefly for
    ///     client connection count to drain before clearing the upstream pool.
    /// </summary>
    /// <param name="drainTimeout">
    ///     Maximum time to wait for active client handlers to exit after cancellation.
    ///     Defaults to 5 seconds.
    /// </param>
    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        if (!ProxyRunning) throw new InvalidOperationException("Proxy is not running.");

        StopCore(cancelSessions: true, clearPools: false);

        var timeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + timeout;
        // Http3ClientConnectionCount tracks inbound QUIC clients separately from
        // ClientConnectionCount (TCP-based H1/H2); draining only the former would let this
        // return, and the pools below get cleared, while HTTP/3 streams are still in flight.
        while ((ClientConnectionCount > 0 || Http3ClientConnectionCount > 0) && DateTime.UtcNow < deadline)
            // StopCore already cancelled quicListenerCts; drain polling must not share that token.
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);

        TcpConnectionFactory.ClearPools();
        await QuicConnectionPool.DrainAsync();
        await Http2OriginConnectionPool.DrainAsync();
    }

    private void StopCore(bool cancelSessions, bool clearPools) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (!ProxyRunning) throw new InvalidOperationException("Proxy is not running.");

        if (SystemProxySettingsManager != null)
        {
            var systemProxyEndPoints = ProxyEndPoints.OfType<ExplicitProxyEndPoint>()
                .Where(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy)
                .ToList();

            if (systemProxyEndPoints.Count > 0)
            {
                SystemProxySettingsManager.RestoreOriginalSettings();
                foreach (var endPoint in systemProxyEndPoints)
                {
                    endPoint.IsSystemHttpProxy = false;
                    endPoint.IsSystemHttpsProxy = false;
                }
            }
        }

        // Prevent accept callbacks from scheduling another accept while listeners are stopping.
        ProxyRunning = false;

        if (cancelSessions) CancelActiveSessions();

        foreach (var endPoint in ProxyEndPoints)
        {
            if (endPoint is TransparentQuicProxyEndPoint)
                continue;
            QuitListen(endPoint);
        }

        // Cancel and wait for QUIC accept loops to exit (UDP-only + dual-listen reverse).
        quicListenerCts?.Cancel();
        foreach (var quicEndPoint in ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>())
            QuitListenQuic(quicEndPoint);
        foreach (var dual in ProxyEndPoints.OfType<TransparentProxyEndPoint>().Where(e => e.EnableHttp3))
            QuitListenQuic(dual);
        quicListenerCts?.Dispose();
        quicListenerCts = null;

        // Keep ProxyEndPoints so Start() can re-bind the same listeners (issue #799).

        CertificateManager?.StopClearIdleCertificates();

        if (clearPools) TcpConnectionFactory.ClearPools();
        if (clearPools) DrainPoolBlocking(QuicConnectionPool.DrainAsync());
        if (clearPools) DrainPoolBlocking(Http2OriginConnectionPool.DrainAsync());

        // Start() may have wired GetCustomUpStreamProxyFunc to GetSystemUpStreamProxy and created
        // systemProxyResolver to back it. Undo both together: leaving the callback in place while
        // disposing its resolver below would make a subsequent Start() see GetCustomUpStreamProxyFunc
        // != null and skip creating a fresh resolver, so the callback would call into a disposed
        // WinHttpWebProxyFinder on the first request after restart. Only clear the callback if it is
        // still the delegate we assigned - a caller who has since replaced it with their own must not
        // have that overwritten here.
        if (Equals(GetCustomUpStreamProxyFunc, (Func<SessionEventArgsBase, Task<IExternalProxy?>>)GetSystemUpStreamProxy))
            GetCustomUpStreamProxyFunc = null;

        // Release the WinHTTP session handle acquired during Start() (Windows-only type).
        if (OperatingSystem.IsWindows())
            systemProxyResolver?.Dispose();
        systemProxyResolver = null;
    }
    private bool disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [SuppressMessage("ApiDesign", "RS0016:Add public types and members to the declared API",
        Justification = "Protected Dispose(bool) is required by the standard IDisposable pattern but is not public API.")]
    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            // No finalizer: Stop()/certificate/buffer disposal must only run on the explicit
            // Dispose path. Callers that omit Dispose leave OS sockets to safe-handle cleanup.
            StopIfRunning();
            DisposeConnectionResources();
            DisposeOwnedLoggerFactory();
        }

        disposed = true;
    }

    private void StopIfRunning()
    {
        if (!ProxyRunning) return;

        try
        {
            Stop();
        }
        catch
        {
            // ignore
        }
    }

    private void DisposeConnectionResources()
    {
        try
        {
            TcpConnectionFactory.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            DrainPoolBlocking(QuicConnectionPool.DrainAsync());
        }
        catch
        {
            // ignore
        }

        try
        {
            DrainPoolBlocking(Http2OriginConnectionPool.DrainAsync());
        }
        catch
        {
            // ignore
        }

        CertificateManager?.Dispose();
        BufferPool?.Dispose();
        _svcbDiscoveryCoordinator?.Dispose();

        // SystemProxyManager is [SupportedOSPlatform("windows")]; the platform analyzer cannot
        // prove that from a null-conditional access alone, so guard explicitly.
        SystemProxySettingsManager?.Dispose();
    }

    private void DisposeOwnedLoggerFactory()
    {
        if (!ownsActiveLoggerFactory) return;

        try
        {
            activeLoggerFactory.Dispose();
        }
        catch
        {
            // A misbehaving sink must never prevent proxy disposal from completing.
        }
    }
}
