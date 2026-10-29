using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.Udp;

/// <summary>
///     Relays UDP datagrams between clients and an upstream with one session per client endpoint. A single receive
///     loop reads the listening socket; the first datagram from a client endpoint picks an upstream (a cluster
///     destination through the load balancer, or the fixed forward host), connects a UDP socket to it, and later
///     datagrams from that endpoint reuse the socket. Each session has its own upstream receive loop that sends
///     replies back through the listening socket to that client endpoint only. Idle sessions are closed by a timer
///     wheel, so no session owns a timer. The listener has no connection to any HTTP path.
/// </summary>
public sealed class UdpRelayListener : IAsyncDisposable, IDisposable
{
    // SIO_UDP_CONNRESET: stops an ICMP port-unreachable for an earlier send from failing the next receive on the
    // listening socket with WSAECONNRESET, which would otherwise let one dead peer break receiving for everyone.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    // EndPoint.Create is an instance method that only reads the template, so these are shared by every loop.
    private static readonly IPEndPoint V4Template = new(IPAddress.Any, 0);
    private static readonly IPEndPoint V6Template = new(IPAddress.IPv6Any, 0);

    private const int ListenerBufferBytes = 1024 * 1024;

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromSeconds(30);

    private readonly UdpRelayOptions options;
    private readonly ClusterManager? clusters;
    private readonly LoadBalancer? loadBalancer;
    private readonly TimeProvider time;
    private readonly long originTimestamp;
    private readonly ConcurrentDictionary<SocketAddress, UdpSession> sessions = new();
    private readonly Dictionary<IPAddress, UdpClientState> clients = new();
    private readonly object clientsGate = new();
    private readonly object wheelGate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, (IPAddress Address, long ExpiresMs)> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly UdpRelayStats stats = new();

    private Socket? socket;
    private UdpSessionWheel? wheel;
    private ITimer? sweepTimer;
    private Task? receiveLoop;
    private int sessionCount;
    private int draining;
    private int disposed;

    /// <param name="options">Listener settings. Copy semantics: later changes to the object are not observed.</param>
    /// <param name="clusters">Cluster manager that owns destination state. Required with <see cref="UdpRelayOptions.ClusterId" />.</param>
    /// <param name="loadBalancer">Picks the destination for a new session. Required with <see cref="UdpRelayOptions.ClusterId" />.</param>
    public UdpRelayListener(UdpRelayOptions options, ClusterManager? clusters = null, LoadBalancer? loadBalancer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!string.IsNullOrWhiteSpace(options.ClusterId) && (clusters is null || loadBalancer is null))
        {
            throw new ArgumentException("A cluster-backed UDP listener needs a cluster manager and a load balancer.");
        }

        this.options = options;
        this.clusters = clusters;
        this.loadBalancer = loadBalancer;
        time = options.Time ?? TimeProvider.System;
        originTimestamp = time.GetTimestamp();
    }

    /// <summary>The bound address and port. Null before <see cref="Start" />.</summary>
    public IPEndPoint? LocalEndPoint => socket?.LocalEndPoint as IPEndPoint;

    /// <summary>Sessions open right now.</summary>
    public int ActiveSessions => Volatile.Read(ref sessionCount);

    /// <summary>True after <see cref="BeginDrain" />. A draining listener relays existing sessions and opens no new one.</summary>
    public bool IsDraining => Volatile.Read(ref draining) != 0;

    internal UdpRelayStats Stats => stats;

    /// <summary>Binds the socket and starts the receive loop and the idle sweep.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (socket is not null)
        {
            throw new InvalidOperationException("The UDP listener is already started.");
        }

        var created = new Socket(options.BindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (options.BindAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
                options.BindAddress.Equals(IPAddress.IPv6Any))
            {
                created.DualMode = true;
            }

            ConnResetDisabled = DisableConnReset(created);

            // A burst of first datagrams from many clients lands here while the single loop is busy opening
            // sessions. The default buffer on some systems is only a few kilobytes, which drops them silently.
            created.ReceiveBufferSize = ListenerBufferBytes;
            created.SendBufferSize = ListenerBufferBytes;
            created.Bind(new IPEndPoint(options.BindAddress, options.Port));
        }
        catch
        {
            created.Dispose();
            throw;
        }

        socket = created;
        if (!string.IsNullOrWhiteSpace(options.ForwardHost))
        {
            // Resolve once now so a bad name fails startup instead of every session.
            _ = ResolveAsync(options.ForwardHost!, stopping.Token).AsTask().GetAwaiter().GetResult();
        }

        var nowMs = NowMs();
        wheel = new UdpSessionWheel(options.IdleTimeout, nowMs);
        var tick = TimeSpan.FromMilliseconds(wheel.TickMilliseconds);
        sweepTimer = time.CreateTimer(static state => ((UdpRelayListener)state!).Sweep(), this, tick, tick);
        receiveLoop = Task.Run(() => ReceiveLoopAsync(created, stopping.Token));
    }

    /// <summary>Stops opening sessions. Datagrams on open sessions keep flowing until they go idle.</summary>
    public void BeginDrain() => Interlocked.Exchange(ref draining, 1);

    /// <summary>
    ///     Calls <see cref="BeginDrain" /> and waits until every session has closed or <paramref name="timeout" />
    ///     passes, whichever is first. Returns true when no session is left.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        BeginDrain();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (ActiveSessions > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started) < timeout)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        return ActiveSessions == 0;
    }

    /// <summary>Closes every session and the listening socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        stopping.Cancel();
        sweepTimer?.Dispose();
        var listening = socket;
        try
        {
            listening?.Dispose();
        }
        catch (SocketException)
        {
            // Already torn down.
        }

        // Wait for the receive loop to finish before closing sessions. Closing first races a
        // datagram already past the disposed check that can OpenSession after the sweep.
        if (receiveLoop is not null)
        {
            try
            {
                await receiveLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The loop is already unwinding because the socket is gone.
            }
        }

        // Snapshot may miss a session that raced into the map as the loop exited; sweep twice,
        // then clear. After the receive loop has stopped, nothing else opens sessions.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var session in sessions.Values)
            {
                CloseSession(session);
            }
        }

        sessions.Clear();
        Interlocked.Exchange(ref sessionCount, 0);

        stopping.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes sessions that have been idle for the timeout, as of the listener's clock.</summary>
    internal void Sweep()
    {
        if (wheel is null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        List<UdpSession> expired = [];
        lock (wheelGate)
        {
            wheel.Advance(NowMs(), expired);
        }

        foreach (var session in expired)
        {
            CloseSession(session);
        }
    }

    private long NowMs() => (long)time.GetElapsedTime(originTimestamp).TotalMilliseconds;

    /// <summary>True when Windows was told not to surface ICMP port-unreachable as a receive error on the listening socket.</summary>
    internal bool ConnResetDisabled { get; private set; }

    private static bool DisableConnReset(Socket created)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            created.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            return true;
        }
        catch (SocketException)
        {
            // Not supported on this socket. The receive loop also tolerates ConnectionReset.
            return false;
        }
    }

    private async Task ReceiveLoopAsync(Socket listening, CancellationToken cancellationToken)
    {
        // One extra byte tells a datagram of exactly the cap from a larger one that the OS truncated.
        var buffer = ArrayPool<byte>.Shared.Rent(options.MaxDatagramBytes + 1);
        var from = new SocketAddress(listening.AddressFamily);
        var consecutiveErrors = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await listening.ReceiveFromAsync(
                        buffer.AsMemory(0, options.MaxDatagramBytes + 1), SocketFlags.None, from, cancellationToken)
                        .ConfigureAwait(false);
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                {
                    // Windows reports a datagram larger than the buffer as an error and discards it.
                    Drop(UdpDropReason.Oversized);
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.Interrupted)
                {
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Shutdown or SocketError.NotSocket)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (++consecutiveErrors > 100)
                    {
                        break;
                    }

                    await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await HandleDatagramAsync(listening, from, buffer, received, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // One bad datagram must not stop the loop that serves every other client.
                    Drop(UdpDropReason.UpstreamError);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask HandleDatagramAsync(
        Socket listening, SocketAddress from, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (length == 0)
        {
            Drop(UdpDropReason.Empty);
            return;
        }

        if (length > options.MaxDatagramBytes)
        {
            Drop(UdpDropReason.Oversized);
            return;
        }

        var nowMs = NowMs();
        if (sessions.TryGetValue(from, out var existing))
        {
            if (!existing.Client.TryTake(nowMs, options.MaxDatagramsPerSecondPerClient))
            {
                Drop(UdpDropReason.RateLimited);
                return;
            }

            await RelayToUpstreamAsync(existing, buffer, length, nowMs, cancellationToken).ConfigureAwait(false);
            return;
        }

        var opened = await OpenSessionAsync(listening, from, nowMs, cancellationToken).ConfigureAwait(false);
        if (opened is not null)
        {
            await RelayToUpstreamAsync(opened, buffer, length, nowMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<UdpSession?> OpenSessionAsync(
        Socket listening, SocketAddress from, long nowMs, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return null;
        }

        IPEndPoint endpoint;
        try
        {
            var template = from.Family == AddressFamily.InterNetworkV6 ? V6Template : V4Template;
            endpoint = template.Create(from) as IPEndPoint ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SocketException or NotSupportedException)
        {
            Drop(UdpDropReason.Malformed);
            return null;
        }

        if (!IsPlausibleSource(endpoint))
        {
            // A client that spoofs a broadcast or multicast source would turn replies into amplification.
            Drop(UdpDropReason.Malformed);
            return null;
        }

        if (IsDraining || Volatile.Read(ref disposed) != 0)
        {
            Drop(UdpDropReason.Draining);
            return null;
        }

        if (Volatile.Read(ref sessionCount) >= options.MaxSessions)
        {
            Drop(UdpDropReason.SessionCap);
            return null;
        }

        var key = ClientKey(endpoint.Address);
        var client = AdmitClient(key, nowMs, out var refusal);
        if (client is null)
        {
            Drop(refusal);
            return null;
        }

        Socket? upstream = null;
        IDisposable? lease = null;
        var registered = false;
        try
        {
            var picked = await PickUpstreamAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (picked.Reason is { } reason)
            {
                Drop(reason);
                ReleaseClient(client);
                return null;
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                ReleaseClient(client);
                return null;
            }

            try
            {
                upstream = new Socket(picked.EndPoint!.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                upstream.Connect(picked.EndPoint);
            }
            catch (SocketException)
            {
                upstream?.Dispose();
                ReportFailure(picked.Destination);
                Drop(UdpDropReason.UpstreamError);
                ReleaseClient(client);
                return null;
            }

            if (picked.Destination is { } destination && loadBalancer is not null)
            {
                lease = loadBalancer.Health.TrackRequest(destination.Id);
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                lease?.Dispose();
                upstream.Dispose();
                ReleaseClient(client);
                return null;
            }

            var session = new UdpSession(CloneAddress(from), endpoint, client, upstream, picked.Destination, lease, nowMs);
            if (!sessions.TryAdd(session.ClientAddress, session))
            {
                lease?.Dispose();
                upstream.Dispose();
                ReleaseClient(client);
                return null;
            }

            registered = true;
            Interlocked.Increment(ref sessionCount);
            lock (wheelGate)
            {
                wheel!.Add(session);
            }

            ProxyMetrics.UdpSessionOpened();
            stats.CountOpened();
            _ = RunUpstreamLoopAsync(listening, session, stopping.Token);
            return session;
        }
        catch
        {
            lease?.Dispose();
            upstream?.Dispose();
            if (!registered)
            {
                ReleaseClient(client);
            }

            throw;
        }
    }

    private async ValueTask<(IPEndPoint? EndPoint, DestinationConfig? Destination, UdpDropReason? Reason)> PickUpstreamAsync(
        IPEndPoint clientEndpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ClusterId))
        {
            return await ResolveEndPointAsync(options.ForwardHost!, options.ForwardPort, null, cancellationToken)
                .ConfigureAwait(false);
        }

        var snapshot = clusters!.Snapshot;
        if (!snapshot.Clusters.TryGetValue(options.ClusterId!, out var cluster))
        {
            return (null, null, UdpDropReason.NoDestination);
        }

        // Select would wait for capacity on a cluster with a queue, which would stall the loop for every client.
        if (loadBalancer!.CountEligible(cluster, snapshot) == 0)
        {
            return (null, null, UdpDropReason.NoDestination);
        }

        // Hash the client endpoint so consistent-hash clusters spread sessions the way the affinity mode promises.
        var destination = loadBalancer.Select(cluster, snapshot, new LoadBalanceContext(hashInput: clientEndpoint.ToString()));
        if (destination is null)
        {
            return (null, null, UdpDropReason.NoDestination);
        }

        return await ResolveEndPointAsync(destination.Address, destination.Port, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<(IPEndPoint? EndPoint, DestinationConfig? Destination, UdpDropReason? Reason)> ResolveEndPointAsync(
        string host, int port, DestinationConfig? destination, CancellationToken cancellationToken)
    {
        try
        {
            var address = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            return (new IPEndPoint(address, port), destination, null);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException or ArgumentException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            ReportFailure(destination);
            return (null, null, UdpDropReason.ResolveFailed);
        }
    }

    private async ValueTask<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var nowMs = NowMs();
        if (resolved.TryGetValue(host, out var cached) && cached.ExpiresMs > nowMs)
        {
            return cached.Address;
        }

        if (clusters is not null && options.ClusterId is not null)
        {
            // A destination with dnsRefreshSeconds is already kept fresh by the cluster's background refresh.
            if (clusters.Snapshot.Clusters.TryGetValue(options.ClusterId, out var owner))
            {
                foreach (var destination in owner.Destinations)
                {
                    if (string.Equals(destination.Address, host, StringComparison.OrdinalIgnoreCase) &&
                        clusters.TryGetResolved(destination.Id, out var known))
                    {
                        return known;
                    }
                }
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        IPAddress address;
        try
        {
            address = options.Resolver is { } resolver
                ? await resolver(host, timeout.Token).ConfigureAwait(false)
                : await DnsResolveAsync(host, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Resolving {host} timed out.");
        }

        resolved[host] = (address, nowMs + (long)ResolveCacheTtl.TotalMilliseconds);
        return address;
    }

    private static async ValueTask<IPAddress> DnsResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress? v6 = null;
        foreach (var candidate in addresses)
        {
            if (candidate.AddressFamily == AddressFamily.InterNetwork)
            {
                return candidate;
            }

            v6 ??= candidate;
        }

        return v6 ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private async ValueTask RelayToUpstreamAsync(
        UdpSession session, byte[] buffer, int length, long nowMs, CancellationToken cancellationToken)
    {
        session.Touch(nowMs);
        try
        {
            await session.Upstream.SendAsync(buffer.AsMemory(0, length), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            Drop(UdpDropReason.UpstreamError);
            OnUpstreamError(session, ex.SocketErrorCode);
            return;
        }
        catch (ObjectDisposedException)
        {
            // The sweep closed the session between the lookup and the send.
            Drop(UdpDropReason.UpstreamError);
            return;
        }

        ProxyMetrics.UdpDatagramRelayed("in", length);
        stats.CountIn(length);
    }

    private async Task RunUpstreamLoopAsync(Socket listening, UdpSession session, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(options.MaxDatagramBytes + 1);
        var healthy = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !session.IsClosed)
            {
                int received;
                try
                {
                    received = await session.Upstream.ReceiveAsync(
                        buffer.AsMemory(0, options.MaxDatagramBytes + 1), SocketFlags.None, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                {
                    Drop(UdpDropReason.Oversized);
                    continue;
                }
                catch (SocketException ex)
                {
                    // On a connected UDP socket an ICMP unreachable for an earlier send surfaces here.
                    OnUpstreamError(session, ex.SocketErrorCode);
                    if (session.IsClosed)
                    {
                        break;
                    }

                    try
                    {
                        // An error that leaves the session open must not turn this loop into a spin.
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }

                if (received == 0)
                {
                    Drop(UdpDropReason.Empty);
                    continue;
                }

                if (received > options.MaxDatagramBytes)
                {
                    Drop(UdpDropReason.Oversized);
                    continue;
                }

                session.Touch(NowMs());
                if (!healthy)
                {
                    healthy = true;
                    ReportSuccess(session.Destination);
                }

                try
                {
                    // The reply goes to the endpoint that opened the session. Nothing here accepts a destination
                    // from the datagram, so a reflected packet cannot be steered at a third party.
                    //
                    // The send is synchronous on purpose. A datagram send does not wait on the peer, and many session
                    // loops share this one socket: SendToAsync(SocketAddress) from several loops at once failed
                    // intermittently inside the framework (ArgumentException on e.RemoteEndPoint), losing the reply.
                    listening.SendTo(buffer.AsSpan(0, received), SocketFlags.None, session.ClientAddress);
                }
                catch (SocketException)
                {
                    Drop(UdpDropReason.ClientError);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ProxyMetrics.UdpDatagramRelayed("out", received);
                stats.CountOut(received);
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an unexpected failure must end the session here rather than vanish.
            stats.NoteFault(ex);
            Drop(UdpDropReason.UpstreamError);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            CloseSession(session);
        }
    }

    private void OnUpstreamError(UdpSession session, SocketError error)
    {
        stats.NoteUpstreamError(error);
        switch (error)
        {
            case SocketError.OperationAborted:
            case SocketError.NotSocket:
            case SocketError.Shutdown:
                return;
            case SocketError.NoBufferSpaceAvailable:
            case SocketError.WouldBlock:
            case SocketError.Interrupted:
            case SocketError.MessageSize:
                // Local overload or a one-off. The destination is not at fault.
                return;
            default:
                ReportFailure(session.Destination);
                if (error is SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.NetworkDown)
                {
                    // The upstream said nothing is listening. A later datagram from this client opens a fresh
                    // session, which the load balancer may place on another destination.
                    CloseSession(session);
                }

                return;
        }
    }

    private void ReportFailure(DestinationConfig? destination)
    {
        if (destination is null || loadBalancer is null || clusters is null || options.ClusterId is null)
        {
            return;
        }

        var snapshot = clusters.Snapshot;
        snapshot.Clusters.TryGetValue(options.ClusterId, out var cluster);
        loadBalancer.Health.ReportStatus(
            destination.Id, 503, clusters, cluster?.HealthCheck?.Passive, cluster?.Destinations.Count ?? 1);
    }

    private void ReportSuccess(DestinationConfig? destination)
    {
        if (destination is null || loadBalancer is null || clusters is null || options.ClusterId is null)
        {
            return;
        }

        var snapshot = clusters.Snapshot;
        snapshot.Clusters.TryGetValue(options.ClusterId, out var cluster);
        loadBalancer.Health.ReportStatus(
            destination.Id, 200, clusters, cluster?.HealthCheck?.Passive, cluster?.Destinations.Count ?? 1);
    }

    private void CloseSession(UdpSession session)
    {
        if (!session.TryClose())
        {
            return;
        }

        sessions.TryRemove(new KeyValuePair<SocketAddress, UdpSession>(session.ClientAddress, session));
        try
        {
            session.Upstream.Dispose();
        }
        catch (SocketException)
        {
            // Nothing left to release.
        }

        session.Lease?.Dispose();
        ReleaseClient(session.Client);

        // Upstream loops may CloseSession after DisposeAsync has already zeroed the counter.
        // Never publish a negative ActiveSessions.
        int count;
        do
        {
            count = Volatile.Read(ref sessionCount);
            if (count <= 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref sessionCount, count - 1, count) != count);

        ProxyMetrics.UdpSessionClosed();
        stats.CountClosed();
    }

    private UdpClientState? AdmitClient(IPAddress key, long nowMs, out UdpDropReason refusal)
    {
        lock (clientsGate)
        {
            if (!clients.TryGetValue(key, out var state))
            {
                state = new UdpClientState(key, options.MaxDatagramsPerSecondPerClient, nowMs);
                clients[key] = state;
            }

            if (state.Sessions >= options.MaxSessionsPerClient)
            {
                refusal = UdpDropReason.ClientCap;
                return null;
            }

            if (!state.TryTake(nowMs, options.MaxDatagramsPerSecondPerClient))
            {
                if (state.Sessions == 0)
                {
                    clients.Remove(key);
                }

                refusal = UdpDropReason.RateLimited;
                return null;
            }

            state.Sessions++;
            refusal = default;
            return state;
        }
    }

    private void ReleaseClient(UdpClientState state)
    {
        lock (clientsGate)
        {
            if (state.Sessions > 0)
            {
                state.Sessions--;
            }

            if (state.Sessions == 0 && clients.TryGetValue(state.Key, out var current) && ReferenceEquals(current, state))
            {
                clients.Remove(state.Key);
            }
        }
    }

    private void Drop(UdpDropReason reason)
    {
        ProxyMetrics.UdpDatagramDropped(UdpDropReasons.NameOf(reason));
        stats.CountDrop(reason);
    }

    private static SocketAddress CloneAddress(SocketAddress source)
    {
        var copy = new SocketAddress(source.Family, source.Size);
        source.Buffer.Span[..source.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }

    /// <summary>The address limits are counted by: IPv4-mapped addresses unwrap, and an IPv6 client counts by its /64.</summary>
    internal static IPAddress ClientKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>False for a source no real client has: port 0, unspecified, broadcast, or multicast.</summary>
    internal static bool IsPlausibleSource(IPEndPoint endpoint)
    {
        var address = endpoint.Address;
        if (endpoint.Port == 0 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            {
                return false;
            }
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? !address.IsIPv6Multicast
            : (address.GetAddressBytes()[0] & 0xF0) != 0xE0;
    }
}
