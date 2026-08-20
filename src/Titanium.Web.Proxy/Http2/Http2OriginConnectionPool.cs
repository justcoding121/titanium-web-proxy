using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Process-scoped fan-in pool of <see cref="Http2OriginConnection" /> instances keyed by authority.
///     Many H1 or H3 clients multiplex onto a small set of shared origin H2 connections (the inverse of
///     <see cref="Http2OriginRelayPool" />, which fans one H2 client out to many origin legs).
///     <para>
///         Rent does <em>not</em> exclusive-checkout: concurrent callers share the same connection and
///         each open a stream via <see cref="Http2OriginConnection.SendAsync" />. A new TCP+H2 session is
///         opened only when every usable member is at soft stream capacity and the authority still has
///         room under <see cref="ProxyResourceLimits.MaxOriginHttp2ConnectionsPerAuthority" />.
///     </para>
/// </summary>
internal sealed class Http2OriginConnectionPool : IAsyncDisposable
{
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, AuthorityEntry> pool =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ProxyServer proxyServer;
    private readonly CancellationTokenSource cleanupCts = new();
    private readonly Task cleanupTask;
    private readonly SemaphoreSlim drainGate = new(1, 1);
    private volatile bool draining;

    internal Http2OriginConnectionPool(ProxyServer proxyServer)
    {
        this.proxyServer = proxyServer;
        cleanupTask = Task.Run(ClearIdleConnectionsAsync, cleanupCts.Token);
    }

    /// <summary>
    ///     Builds the pool key for an H2 origin authority using the same identity dimensions as
    ///     <see cref="TcpConnectionFactory.GetConnectionCacheKey" />.
    /// </summary>
    internal static string BuildPoolKey(
        ProxyServer server,
        SessionEventArgs sessionArgs,
        string host,
        int port,
        string? connectHost,
        int? connectPort)
    {
        var originIsHttps = sessionArgs.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
        var upStreamProxy = sessionArgs.CustomUpStreamProxyUsed
                            ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;
        return BuildPoolKey(server, sessionArgs.ProxyEndPoint, upStreamProxy, upStreamEndPoint,
            host, port, connectHost, connectPort);
    }

    /// <summary>
    ///     Pool-key builder for the session-less H3→H2 fast path (no <see cref="SessionEventArgs" />).
    /// </summary>
    internal static string BuildPoolKey(
        ProxyServer server,
        ProxyEndPoint endPoint,
        IExternalProxy? customUpStreamProxy,
        IPEndPoint? upStreamEndPoint,
        string host,
        int port,
        string? connectHost,
        int? connectPort)
    {
        var originIsHttps = endPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
        var upStreamProxy = customUpStreamProxy
                            ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);
        var effectiveProxy = TcpConnectionFactory.GetEffectiveUpstreamProxy(upStreamProxy, host, port);
        var effectiveEndPoint = upStreamEndPoint ?? server.UpStreamEndPoint;

        return TcpConnectionFactory.GetConnectionCacheKey(
            host, port, originIsHttps,
            originIsHttps ? SslExtensions.Http2ProtocolAsList : null,
            effectiveEndPoint, effectiveProxy,
            connectHost, connectPort,
            server.UpStreamEndPointIPv4, server.UpStreamEndPointIPv6);
    }

    /// <summary>
    ///     Returns a usable shared origin connection for <paramref name="poolKey" />, opening one when
    ///     needed. The caller must not dispose the connection on client disconnect; use
    ///     <see cref="Invalidate" /> only when the connection is known bad (GOAWAY/fault) or the user
    ///     requested <c>CloseServerConnection</c>.
    /// </summary>
    internal async ValueTask<Http2OriginConnection> RentAsync(
        string poolKey,
        Func<CancellationToken, Task<Http2OriginConnection>> openAsync,
        CancellationToken cancellationToken)
    {
        if (draining)
            throw new ObjectDisposedException(nameof(Http2OriginConnectionPool));

        var entry = pool.GetOrAdd(poolKey, static _ => new AuthorityEntry());
        Interlocked.Increment(ref entry.Interest);
        try
        {
            DiagPickStats.OnRent();
            var limits = proxyServer.ResourceLimits;
            var picked = TryPick(entry, limits);
            if (picked != null)
                return picked;

            // Soft-miss. Skip CreationGate only when a Gate-held snapshot says the authority
            // is already at max — open is impossible, so serializing on CreationGate cannot
            // create and would only convoy oversubscribed rents (c=64).
            if (!CanOpenAnother(entry, limits))
            {
                DiagPickStats.OnTryPickAny();
                picked = TryPickAnyUsable(entry);
                if (picked != null)
                    return picked;
            }

            DiagPickStats.OnCreationGate();
            await entry.CreationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (draining)
                    throw new ObjectDisposedException(nameof(Http2OriginConnectionPool));

                picked = TryPick(entry, limits);
                if (picked != null)
                    return picked;

                if (!CanOpenAnother(entry, limits))
                {
                    DiagPickStats.OnTryPickAny();
                    picked = TryPickAnyUsable(entry);
                    if (picked != null)
                        return picked;
                }

                DiagPickStats.OnOpen();
                var created = await openAsync(cancellationToken).ConfigureAwait(false);
                lock (entry.Gate)
                {
                    if (draining ||
                        entry.Connections.Count >= limits.MaxOriginHttp2ConnectionsPerAuthority)
                    {
                        // Another opener won or we are shutting down — dispose the spare unless we
                        // have zero members left to serve this request.
                        if (entry.Connections.Count > 0)
                        {
                            created.Dispose();
                            return TryPickAnyUsable(entry)
                                   ?? throw new Http2OriginGoAwayException(
                                       "No usable origin HTTP/2 connection remains in the pool.");
                        }
                    }

                    entry.Connections.Add(created);
                    return created;
                }
            }
            finally
            {
                entry.CreationGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref entry.Interest);
        }
    }

    /// <summary>
    ///     Offers an already-established connection (e.g. the H1 bridge seed from negotiation) into the
    ///     pool for <paramref name="poolKey" />. Disposes it when the authority is already at capacity.
    /// </summary>
    internal void Offer(string poolKey, Http2OriginConnection connection)
    {
        if (draining || !connection.IsUsable || connection.IsNearStreamIdExhaustion)
        {
            connection.Dispose();
            return;
        }

        var entry = pool.GetOrAdd(poolKey, static _ => new AuthorityEntry());
        lock (entry.Gate)
        {
            if (draining ||
                entry.Connections.Count >= proxyServer.ResourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
            {
                connection.Dispose();
                return;
            }

            entry.Connections.Add(connection);
            connection.Touch();
        }
    }

    /// <summary>
    ///     Stops handing <paramref name="connection" /> out. In-flight streams are allowed to finish;
    ///     the connection disposes itself when the last lease/stream drains.
    /// </summary>
    internal void Invalidate(string poolKey, Http2OriginConnection connection)
    {
        if (pool.TryGetValue(poolKey, out var entry))
        {
            lock (entry.Gate)
                entry.Connections.Remove(connection);
        }

        connection.Retire();
    }

    /// <summary>
    ///     Cancels idle sweep, disposes every pooled connection, and clears the dictionary. Called from
    ///     <see cref="ProxyServer.Stop" /> / Dispose so static-bag leaks cannot outlive the proxy.
    /// </summary>
    internal async ValueTask DrainAsync()
    {
        await drainGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            draining = true;
            try
            {
                cleanupCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // DrainAsync is idempotent (Stop then Dispose).
            }

            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            foreach (var kvp in pool)
            {
                Http2OriginConnection[] snapshot;
                lock (kvp.Value.Gate)
                {
                    snapshot = kvp.Value.Connections.ToArray();
                    kvp.Value.Connections.Clear();
                }

                foreach (var c in snapshot)
                {
                    try
                    {
                        c.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                kvp.Value.CreationGate.Dispose();
            }

            pool.Clear();
            cleanupCts.Dispose();
        }
        finally
        {
            drainGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await DrainAsync().ConfigureAwait(false);

    private static Http2OriginConnection? TryPick(AuthorityEntry entry, ProxyResourceLimits limits)
    {
        var snapshot = SnapshotMembers(entry);
        Http2OriginConnection? best = null;
        var bestActive = int.MaxValue;
        var activeSum = 0;
        var softCapSample = 0;
        foreach (var c in snapshot)
        {
            if (!c.IsUsable || c.IsNearStreamIdExhaustion)
                continue;

            var soft = c.SoftStreamCapacity;
            softCapSample = soft;
            var active = c.ActiveStreamCount;
            activeSum += active;
            if (active >= soft)
                continue;

            if (active < bestActive)
            {
                best = c;
                bestActive = active;
            }
        }

        DiagPickStats.OnTryPick(snapshot.Length, softCapSample, activeSum, best != null);
        best?.Touch();
        _ = limits;
        return best;
    }

    private static Http2OriginConnection? TryPickAnyUsable(AuthorityEntry entry)
    {
        var snapshot = SnapshotMembers(entry);
        Http2OriginConnection? best = null;
        var bestActive = int.MaxValue;
        foreach (var c in snapshot)
        {
            if (!c.IsUsable || c.IsNearStreamIdExhaustion)
                continue;

            var active = c.ActiveStreamCount;
            if (active < bestActive)
            {
                best = c;
                bestActive = active;
            }
        }

        best?.Touch();
        return best;
    }

    /// <summary>
    ///     Prune + copy member refs under Gate; caller picks outside the lock using Interlocked
    ///     actives. Stale snapshot is acceptable (IsUsable filters retirements).
    /// </summary>
    private static Http2OriginConnection[] SnapshotMembers(AuthorityEntry entry)
    {
        lock (entry.Gate)
        {
            PruneUnusableUnderLock(entry);
            return entry.Connections.Count == 0
                ? []
                : entry.Connections.ToArray();
        }
    }

    private static bool CanOpenAnother(AuthorityEntry entry, ProxyResourceLimits limits)
    {
        lock (entry.Gate)
        {
            PruneUnusableUnderLock(entry);
            return entry.Connections.Count < limits.MaxOriginHttp2ConnectionsPerAuthority;
        }
    }

    private static void PruneUnusableUnderLock(AuthorityEntry entry)
    {
        for (var i = entry.Connections.Count - 1; i >= 0; i--)
        {
            var c = entry.Connections[i];
            if (c.IsUsable && !c.IsNearStreamIdExhaustion)
                continue;

            entry.Connections.RemoveAt(i);
            // Never Dispose here while siblings may still be in SendAsync — Retire waits for idle.
            c.Retire();
        }
    }

    private async Task ClearIdleConnectionsAsync()
    {
        try
        {
            while (!cleanupCts.IsCancellationRequested)
            {
                await Task.Delay(IdleSweepInterval, cleanupCts.Token).ConfigureAwait(false);
                var cutOff = DateTime.UtcNow - IdleConnectionTimeout;

                foreach (var kvp in pool)
                {
                    if (Volatile.Read(ref kvp.Value.Interest) > 0)
                        continue;

                    List<Http2OriginConnection>? toDispose = null;
                    lock (kvp.Value.Gate)
                    {
                        for (var i = kvp.Value.Connections.Count - 1; i >= 0; i--)
                        {
                            var c = kvp.Value.Connections[i];
                            if (c.ActiveStreamCount > 0 || c.LeaseCount > 0)
                                continue;

                            if (c.IsUsable && c.LastUsedUtc >= cutOff && !c.IsNearStreamIdExhaustion)
                                continue;

                            kvp.Value.Connections.RemoveAt(i);
                            (toDispose ??= new List<Http2OriginConnection>()).Add(c);
                        }
                    }

                    if (toDispose == null)
                        continue;

                    foreach (var c in toDispose)
                    {
                        try
                        {
                            c.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shut down
        }
    }

    private sealed class AuthorityEntry
    {
        internal readonly object Gate = new();
        internal readonly List<Http2OriginConnection> Connections = new();
        internal readonly SemaphoreSlim CreationGate = new(1, 1);
        internal int Interest;
    }

    /// <summary>
    ///     Diag-only counters for pool pick shape (env <c>TWP_DIAG_POOL_PICK=1</c>). Process-wide; not thread-local.
    /// </summary>
    internal static class DiagPickStats
    {
        private static bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK"), "1",
                StringComparison.Ordinal);

        internal static long RentCalls;
        internal static long TryPickCalls;
        internal static long TryPickHits;
        internal static long TryPickSoftMiss;
        internal static long CreationGateEnters;
        internal static long TryPickAnyUsableCalls;
        internal static long Opens;
        internal static long MemberSumOnPick;
        internal static long MemberSamples;
        internal static long SoftCapSumOnPick;
        internal static long ActiveSumOnPick;

        private static int loggerStarted;

        internal static bool IsEnabled => Enabled;

        internal static void OnRent()
        {
            if (!Enabled) return;
            var n = Interlocked.Increment(ref RentCalls);
            EnsureLogger();
            if (n % 5000 == 0)
                Emit($"tick rents={n}");
        }

        private static void EnsureLogger()
        {
            if (Interlocked.Exchange(ref loggerStarted, 1) != 0)
                return;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Emit("exit");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(2000).ConfigureAwait(false);
                        Emit("periodic");
                    }
                }
                catch
                {
                    // ignore
                }
            });
        }

        private static void Emit(string tag)
        {
            var line = $"[TWP_DIAG_POOL_PICK {tag}] {FormatSummary()}";
            var path = Environment.GetEnvironmentVariable("TWP_DIAG_POOL_PICK_OUT");
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                    return;
                }
                catch
                {
                    // fall through
                }
            }

            Console.Error.WriteLine(line);
        }

        internal static void OnTryPick(int members, int softCap, int activeSum, bool hit)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref TryPickCalls);
            Interlocked.Add(ref MemberSumOnPick, members);
            Interlocked.Increment(ref MemberSamples);
            Interlocked.Add(ref SoftCapSumOnPick, softCap);
            Interlocked.Add(ref ActiveSumOnPick, activeSum);
            if (hit) Interlocked.Increment(ref TryPickHits);
            else Interlocked.Increment(ref TryPickSoftMiss);
        }

        internal static void OnCreationGate()
        {
            if (Enabled) Interlocked.Increment(ref CreationGateEnters);
        }

        internal static void OnTryPickAny()
        {
            if (Enabled) Interlocked.Increment(ref TryPickAnyUsableCalls);
        }

        internal static void OnOpen()
        {
            if (Enabled) Interlocked.Increment(ref Opens);
        }

        internal static string FormatSummary()
        {
            var rents = Volatile.Read(ref RentCalls);
            var picks = Volatile.Read(ref TryPickCalls);
            var hits = Volatile.Read(ref TryPickHits);
            var miss = Volatile.Read(ref TryPickSoftMiss);
            var gate = Volatile.Read(ref CreationGateEnters);
            var any = Volatile.Read(ref TryPickAnyUsableCalls);
            var opens = Volatile.Read(ref Opens);
            var samples = Volatile.Read(ref MemberSamples);
            var avgMembers = samples == 0 ? 0.0 : (double)Volatile.Read(ref MemberSumOnPick) / samples;
            var avgSoft = samples == 0 ? 0.0 : (double)Volatile.Read(ref SoftCapSumOnPick) / samples;
            var avgActive = samples == 0 ? 0.0 : (double)Volatile.Read(ref ActiveSumOnPick) / samples;
            var missRate = picks == 0 ? 0.0 : 100.0 * miss / picks;
            return
                $"rents={rents} tryPick={picks} hit={hits} softMiss={miss} ({missRate:F1}%) " +
                $"creationGate={gate} tryPickAny={any} opens={opens} " +
                $"avgMembers={avgMembers:F2} avgSoftCapSample={avgSoft:F1} avgActiveSum={avgActive:F1}";
        }
    }
}
