using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Pool of origin HTTP/2 connections for one client H2 session. Distributes newly opened client
///     streams across up to <see cref="ProxyResourceLimits.MaxOriginHttp2ConnectionsPerAuthority" />
///     origin legs (round-robin with capacity preference) and remaps stream ids so each origin
///     connection keeps an independent odd-id space and write path.
/// </summary>
internal sealed class Http2OriginRelayPool : IAsyncDisposable
{
    private readonly List<OriginLeg> legs = new();
    private readonly object gate = new();
    private readonly ConcurrentDictionary<int, StreamAssignment> clientToOrigin = new();
    private readonly Func<CancellationToken, Task<TcpServerConnection>> openOriginAsync;
    private readonly ProxyResourceLimits resourceLimits;
    private readonly ILogger logger;
    private readonly CancellationTokenSource poolCts = new();
    private int rrCursor;
    private int disposed;

    public Http2OriginRelayPool(
        TcpServerConnection primary,
        Func<CancellationToken, Task<TcpServerConnection>> openOriginAsync,
        ProxyResourceLimits resourceLimits,
        ILogger logger,
        SemaphoreSlim primaryWriteLock)
    {
        this.openOriginAsync = openOriginAsync;
        this.resourceLimits = resourceLimits;
        this.logger = logger;
        legs.Add(CreateLeg(primary, primaryWriteLock, ownsWriteLock: false));
    }

    public int LegCount
    {
        get { lock (gate) return legs.Count; }
    }

    public OriginLeg PrimaryLeg
    {
        get { lock (gate) return legs[0]; }
    }

    public IReadOnlyList<OriginLeg> SnapshotLegs()
    {
        lock (gate) return legs.ToArray();
    }

    /// <summary>
    ///     Assigns <paramref name="clientStreamId"/> to an origin leg, opening a new leg when every
    ///     existing leg is at its soft capacity and the pool has room.
    /// </summary>
    public async ValueTask<StreamAssignment> AssignStreamAsync(int clientStreamId,
        CancellationToken cancellationToken)
    {
        if (clientToOrigin.TryGetValue(clientStreamId, out var existing))
            return existing;

        OriginLeg? chosen;
        lock (gate)
        {
            chosen = PickLegUnderLock();
        }

        if (chosen == null || NeedsNewLeg(chosen))
        {
            var opened = await TryOpenLegAsync(cancellationToken).ConfigureAwait(false);
            if (opened != null)
                chosen = opened;
        }

        if (chosen == null)
        {
            lock (gate) chosen = PickLeastLoadedUnderLock();
        }

        int originStreamId;
        lock (chosen.IdLock)
        {
            originStreamId = chosen.NextStreamId;
            chosen.NextStreamId += 2;
            chosen.ActiveStreams++;
        }

        chosen.SendFlow.RegisterStream(originStreamId);

        var assignment = new StreamAssignment(chosen, originStreamId);
        if (!clientToOrigin.TryAdd(clientStreamId, assignment))
        {
            chosen.SendFlow.RemoveStream(originStreamId);
            lock (chosen.IdLock) chosen.ActiveStreams--;
            return clientToOrigin[clientStreamId];
        }

        chosen.OriginToClient[originStreamId] = clientStreamId;
        return assignment;
    }

    public bool TryGetAssignment(int clientStreamId, out StreamAssignment assignment) =>
        clientToOrigin.TryGetValue(clientStreamId, out assignment!);

    public void ReleaseStream(int clientStreamId)
    {
        if (!clientToOrigin.TryRemove(clientStreamId, out var assignment))
            return;

        assignment.Leg.OriginToClient.TryRemove(assignment.OriginStreamId, out _);
        assignment.Leg.SendFlow.RemoveStream(assignment.OriginStreamId);
        lock (assignment.Leg.IdLock)
        {
            if (assignment.Leg.ActiveStreams > 0)
                assignment.Leg.ActiveStreams--;
        }
    }

    private int SoftCapPerLeg()
    {
        // Spread streams across origin legs before any single connection saturates. Dividing by
        // MaxOrigin*4 (default 8) opens additional legs under typical RPS concurrency (32–128).
        // Soft=1/2 fan-out was tried; cool remeasure showed no gain vs Soft≈8 (extra legs tax
        // cleartext connect without helping FrameWriter parallelism enough).
        return Math.Max(1, resourceLimits.MaxConcurrentStreamsPerConnection /
                           Math.Max(1, resourceLimits.MaxOriginHttp2ConnectionsPerAuthority * 4));
    }

    private bool NeedsNewLeg(OriginLeg current)
    {
        var softCap = SoftCapPerLeg();
        lock (gate)
        {
            if (legs.Count >= resourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
                return false;
        }

        return Volatile.Read(ref current.ActiveStreams) >= softCap;
    }

    private OriginLeg? PickLegUnderLock()
    {
        if (legs.Count == 0) return null;

        var softCap = SoftCapPerLeg();
        var start = rrCursor++ % legs.Count;
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[(start + i) % legs.Count];
            if (leg.ActiveStreams < softCap)
                return leg;
        }

        return null;
    }

    private OriginLeg PickLeastLoadedUnderLock()
    {
        OriginLeg best = legs[0];
        for (var i = 1; i < legs.Count; i++)
        {
            if (legs[i].ActiveStreams < best.ActiveStreams)
                best = legs[i];
        }

        return best;
    }

    private async Task<OriginLeg?> TryOpenLegAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (legs.Count >= resourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
                return null;
        }

        TcpServerConnection? connection = null;
        try
        {
            connection = await openOriginAsync(cancellationToken).ConfigureAwait(false);
            connection.Http2SessionStarted = true;
            await connection.Stream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await Http2Helper.SendHttp2ClientConnectionStartupAsync(connection.Stream, cancellationToken)
                .ConfigureAwait(false);

            var writeLock = new SemaphoreSlim(1, 1);
            var leg = CreateLeg(connection, writeLock, ownsWriteLock: true);
            lock (gate)
            {
                if (legs.Count >= resourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
                {
                    _ = DisposeLegAsync(leg);
                    return PickLeastLoadedUnderLock();
                }

                legs.Add(leg);
                return leg;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to open additional origin HTTP/2 connection for multi-leg relay");
            if (connection != null)
            {
                try { connection.Dispose(); }
                catch { /* ignore */ }
            }

            return null;
        }
    }

    private static OriginLeg CreateLeg(TcpServerConnection connection, SemaphoreSlim writeLock, bool ownsWriteLock)
    {
        return new OriginLeg(connection, new Http2FrameWriter(connection.Stream, writeLock), writeLock, ownsWriteLock);
    }

    private static async Task DisposeLegAsync(OriginLeg leg)
    {
        try { await leg.Writer.DisposeAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
        try { leg.Connection.Dispose(); }
        catch { /* ignore */ }
        if (leg.OwnsWriteLock)
        {
            try { leg.WriteLock.Dispose(); }
            catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try { await poolCts.CancelAsync(); }
        catch { /* ignore */ }

        OriginLeg[] snapshot;
        lock (gate) snapshot = legs.ToArray();
        foreach (var leg in snapshot)
            await DisposeLegAsync(leg).ConfigureAwait(false);

        poolCts.Dispose();
    }

    internal sealed class OriginLeg
    {
        public OriginLeg(TcpServerConnection connection, Http2FrameWriter writer, SemaphoreSlim writeLock,
            bool ownsWriteLock)
        {
            Connection = connection;
            Writer = writer;
            Stream = connection.Stream;
            WriteLock = writeLock;
            OwnsWriteLock = ownsWriteLock;
            NextStreamId = 1;
            SendFlow = new Http2FlowController();
        }

        public TcpServerConnection Connection { get; }
        public Stream Stream { get; }
        public Http2FrameWriter Writer { get; }
        public SemaphoreSlim WriteLock { get; }
        public bool OwnsWriteLock { get; }
        public object IdLock { get; } = new();
        public int NextStreamId;
        public int ActiveStreams;
        public ConcurrentDictionary<int, int> OriginToClient { get; } = new();
        public Http2FlowController SendFlow { get; }
        public Http2Settings Settings { get; } = new();
        public int InitialWindowUpdateSent;
    }

    internal readonly struct StreamAssignment
    {
        public StreamAssignment(OriginLeg leg, int originStreamId)
        {
            Leg = leg;
            OriginStreamId = originStreamId;
        }

        public OriginLeg Leg { get; }
        public int OriginStreamId { get; }
    }
}
