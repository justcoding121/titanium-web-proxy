#pragma warning disable CA1416 // QuicException is the BCL error type. This library does not start the platform QUIC stack.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Quic;

/// <summary>A UDP listener that accepts managed QUIC connections.</summary>
public sealed class ManagedQuicListener : IAsyncDisposable
{
    private readonly object _gate = new();

    private readonly IUdpPacketTransport _transport;

    private readonly ManagedQuicConnectionOptionsCallback _callback;

    private readonly TimeSpan _handshakeTimeout;

    private readonly CancellationTokenSource _stop = new();

    private readonly Dictionary<string, Tracked> _connections = new(StringComparer.Ordinal);

    private readonly Queue<ManagedQuicConnection> _ready = new();

    private readonly Queue<TaskCompletionSource<ManagedQuicConnection>> _waiters = new();

    private readonly Task _loop;

    private readonly bool _injected;

    private ManagedQuicListener(IUdpPacketTransport transport, ManagedQuicConnectionOptionsCallback callback, TimeSpan handshakeTimeout, bool injected)
    {
        _transport = transport;
        _callback = callback;
        _handshakeTimeout = handshakeTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : handshakeTimeout;
        _injected = injected;
        LocalEndPoint = transport.LocalEndpoint;
        _loop = injected ? Task.CompletedTask : RunAsync(_stop.Token);
    }

    /// <summary>Gets the bound local endpoint.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Binds a UDP socket and starts accepting.</summary>
    /// <param name="options">The endpoint, the certificate callback, and the FIPS switch.</param>
    /// <param name="cancellationToken">A token that cancels binding.</param>
    /// <returns>The listening socket.</returns>
    public static ValueTask<ManagedQuicListener> ListenAsync(ManagedQuicListenerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ListenEndPoint);
        ArgumentNullException.ThrowIfNull(options.ConnectionOptionsCallback);
        cancellationToken.ThrowIfCancellationRequested();
        QuicConnectionSession.RejectFips(options.AllowManagedStackUnderFips);
        var transport = new UdpSocketTransport(options.ListenEndPoint);
        try
        {
            return ValueTask.FromResult(new ManagedQuicListener(transport, options.ConnectionOptionsCallback, options.HandshakeTimeout, injected: false));
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts datagrams from the caller. Each reply is handed to <paramref name="send"/> before the creating call returns.
    /// The listener does not open a socket.
    /// </summary>
    /// <param name="options">The certificate callback and the FIPS switch. The listen endpoint is not bound.</param>
    /// <param name="local">The virtual local address. Path checks use this tuple.</param>
    /// <param name="send">Receives one outbound datagram and the virtual remote address.</param>
    /// <param name="maximumDatagramSize">The largest payload <paramref name="send"/> will be offered. A tunnel lowers this.</param>
    /// <returns>A listener that reads only through <see cref="HandleDatagramAsync"/>.</returns>
    public static ManagedQuicListener CreateInjected(
        ManagedQuicListenerOptions options,
        IPEndPoint local,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send,
        int maximumDatagramSize = 65507)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(options.ConnectionOptionsCallback);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDatagramSize, 1200);

        QuicConnectionSession.RejectFips(options.AllowManagedStackUnderFips);
        return new ManagedQuicListener(new CallbackTransport(local, send, maximumDatagramSize), options.ConnectionOptionsCallback, options.HandshakeTimeout, injected: true);
    }

    /// <summary>Feeds one datagram from a relay. Replies leave through the callback passed to <see cref="CreateInjected"/>.</summary>
    /// <param name="datagram">The UDP payload. The caller may reuse the buffer after this task completes.</param>
    /// <param name="remote">The virtual remote address.</param>
    /// <param name="cancellationToken">A token that cancels the certificate callback.</param>
    /// <returns>A task that completes after the replies for this datagram have been offered.</returns>
    public ValueTask HandleDatagramAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        if (!_injected)
        {
            throw new InvalidOperationException("This listener reads its own socket.");
        }

        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new byte[datagram.Length];
        datagram.Span.CopyTo(copy);
        return new ValueTask(OnDatagramAsync(copy, remote, cancellationToken));
    }

    /// <summary>Runs handshake and loss timers for an injected listener. The caller supplies the clock tick.</summary>
    /// <param name="cancellationToken">A token that cancels the flush.</param>
    /// <returns>A task that completes after the replies for this tick have been offered.</returns>
    public ValueTask DriveAsync(CancellationToken cancellationToken = default)
    {
        if (!_injected)
        {
            throw new InvalidOperationException("This listener reads its own socket.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(OnTimeoutAsync(TimeProvider.System.GetUtcNow()));
    }

    /// <summary>Waits for the next connection to finish its handshake.</summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>The accepted connection.</returns>
    public async ValueTask<ManagedQuicConnection> AcceptConnectionAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<ManagedQuicConnection> created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_ready.Count > 0)
            {
                return _ready.Dequeue();
            }

            _waiters.Enqueue(created);
        }

        try
        {
            return await created.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                var kept = new Queue<TaskCompletionSource<ManagedQuicConnection>>();
                while (_waiters.Count > 0)
                {
                    TaskCompletionSource<ManagedQuicConnection> waiter = _waiters.Dequeue();
                    if (!ReferenceEquals(waiter, created))
                    {
                        kept.Enqueue(waiter);
                    }
                }

                while (kept.Count > 0)
                {
                    _waiters.Enqueue(kept.Dequeue());
                }
            }

            throw;
        }
    }

    /// <summary>Stops the socket and the connections it accepted.</summary>
    /// <returns>A task that completes when the socket is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _transport.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        Tracked[] tracked;
        TaskCompletionSource<ManagedQuicConnection>[] waiters;
        lock (_gate)
        {
            tracked = new Tracked[_connections.Count];
            _connections.Values.CopyTo(tracked, 0);
            _connections.Clear();
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }

        for (int i = 0; i < waiters.Length; i++)
        {
            waiters[i].TrySetCanceled();
        }

        for (int i = 0; i < tracked.Length; i++)
        {
            tracked[i].Session.Dispose();
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                TimeSpan waitFor = NextDelay(now);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (waitFor <= TimeSpan.Zero)
                {
                    await OnTimeoutAsync(now).ConfigureAwait(false);
                    if (!_transport.DatagramPending)
                    {
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                else if (waitFor < TimeSpan.MaxValue)
                {
                    wait.CancelAfter(waitFor);
                }

                try
                {
                    UdpReceived received = await _transport.ReceiveAsync(buffer.AsMemory(0, 65536), wait.Token).ConfigureAwait(false);
                    var copy = new byte[received.Count];
                    buffer.AsSpan(0, received.Count).CopyTo(copy);
                    await OnDatagramAsync(copy, received.Remote, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await OnTimeoutAsync(TimeProvider.System.GetUtcNow()).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // An unreachable peer is reported on the next receive. The listener stays up.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task OnDatagramAsync(byte[] datagram, IPEndPoint remote, CancellationToken cancellationToken)
    {
        if (!TryDestination(datagram, out byte[] id, out bool initial))
        {
            await ReplyToUnknownVersionAsync(datagram, remote, cancellationToken).ConfigureAwait(false);
            return;
        }

        string key = Convert.ToHexString(id);
        Tracked? tracked;
        bool created = false;
        lock (_gate)
        {
            if (!_connections.TryGetValue(key, out tracked) && initial)
            {
                tracked = StartServer(id, remote);
                _connections.Add(key, tracked);
                created = true;
            }
        }

        if (tracked is null)
        {
            return;
        }

        if (created)
        {
            tracked.Session.SetRemote(remote);
        }

        PathInfo path = QuicConnectionSession.PathOf(LocalEndPoint, tracked.Session.RemoteEndPoint);
        tracked.Session.Handle(datagram, path, TimeProvider.System.GetUtcNow());
        if (tracked.Session.TakePendingHello(out string serverName))
        {
            await FinishHelloAsync(tracked, serverName, cancellationToken).ConfigureAwait(false);
        }

        await tracked.Session.FlushAsync().ConfigureAwait(false);
        tracked.Session.NotifyStreams();
        if (Expire(tracked))
        {
            await tracked.Session.FlushAsync().ConfigureAwait(false);
            return;
        }

        Publish(tracked);
    }

    private Tracked StartServer(byte[] localId, IPEndPoint remote)
    {
        var core = new QuicConnectionCore();
        core.StartServer(localId, null, QuicConnectionSession.Parameters(client: false, localId));
        var session = new QuicConnectionSession(core, _transport, LocalEndPoint, remote, ownsTransport: false);
        return new Tracked(session, new ManagedQuicConnection(session), localId, TimeProvider.System.GetUtcNow() + _handshakeTimeout);
    }

    private async Task FinishHelloAsync(Tracked tracked, string serverName, CancellationToken cancellationToken)
    {
        ManagedQuicServerConnectionOptions chosen;
        try
        {
            chosen = await _callback(tracked.Connection, new SslClientHelloInfo(serverName, SslProtocols.Tls13), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            tracked.Session.Close(0);
            await tracked.Session.FlushAsync().ConfigureAwait(false);
            Fail(tracked, new QuicException(QuicError.CallbackError, null, exception.Message));
            return;
        }

        try
        {
            QuicConnectionSession.RejectFips(chosen.AllowManagedStackUnderFips);
            X509Certificate2 certificate = Certificate(chosen);
            if (!tracked.Session.CompleteHello(certificate, QuicConnectionSession.ProtocolNames(chosen.ServerAuthenticationOptions?.ApplicationProtocols)))
            {
                Fail(tracked, new QuicException(QuicError.ConnectionAborted, null, "The handshake did not continue."));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(tracked, exception as QuicException ?? new QuicException(QuicError.CallbackError, null, exception.Message));
        }
    }

    private async Task OnTimeoutAsync(DateTimeOffset now)
    {
        Tracked[] tracked;
        lock (_gate)
        {
            tracked = new Tracked[_connections.Count];
            _connections.Values.CopyTo(tracked, 0);
        }

        for (int i = 0; i < tracked.Length; i++)
        {
            tracked[i].Session.Timeout(now);
            await tracked[i].Session.FlushAsync().ConfigureAwait(false);
            tracked[i].Session.NotifyStreams();
            if (Expire(tracked[i]))
            {
                await tracked[i].Session.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                Publish(tracked[i]);
            }
        }
    }

    private TimeSpan NextDelay(DateTimeOffset now)
    {
        TimeSpan waitFor = TimeSpan.MaxValue;
        lock (_gate)
        {
            foreach (Tracked tracked in _connections.Values)
            {
                if (tracked.Session.NextTimeout() is not DateTimeOffset due)
                {
                    continue;
                }

                TimeSpan delay = due - now;
                if (delay < waitFor)
                {
                    waitFor = delay;
                }
            }
        }

        return waitFor;
    }

    private bool Expire(Tracked tracked)
    {
        if (tracked.Published || tracked.Connection.HandshakeCompleted || TimeProvider.System.GetUtcNow() <= tracked.Deadline)
        {
            return false;
        }

        tracked.Session.Close(0);
        Fail(tracked, new QuicException(QuicError.ConnectionTimeout, null, "The handshake timed out."));
        return true;
    }

    private void Publish(Tracked tracked)
    {
        if (!tracked.Connection.HandshakeCompleted || tracked.Published)
        {
            if (tracked.Session.Handshake.IsFaulted && !tracked.Published)
            {
                tracked.Published = true;
                Exception error = tracked.Session.Handshake.Exception?.InnerException
                    ?? new QuicException(QuicError.ConnectionAborted, null, "The handshake failed.");
                Fail(tracked, error);
            }

            return;
        }

        tracked.Published = true;
        TaskCompletionSource<ManagedQuicConnection>? waiter = null;
        lock (_gate)
        {
            if (_waiters.Count > 0)
            {
                waiter = _waiters.Dequeue();
            }
            else
            {
                _ready.Enqueue(tracked.Connection);
            }
        }

        waiter?.TrySetResult(tracked.Connection);
    }

    private void Fail(Tracked tracked, Exception error)
    {
        tracked.Published = true;
        TaskCompletionSource<ManagedQuicConnection>? waiter;
        lock (_gate)
        {
            waiter = _waiters.Count > 0 ? _waiters.Dequeue() : null;
        }

        waiter?.TrySetException(error);
    }

    private static X509Certificate2 Certificate(ManagedQuicServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        X509Certificate? certificate = options.ServerAuthenticationOptions?.ServerCertificate
            ?? options.ServerAuthenticationOptions?.ServerCertificateContext?.TargetCertificate;
        if (certificate is not X509Certificate2 concrete)
        {
            throw new QuicException(QuicError.CallbackError, null, "The server certificate is missing.");
        }

        return concrete;
    }

    private async Task ReplyToUnknownVersionAsync(byte[] datagram, IPEndPoint remote, CancellationToken cancellationToken)
    {
        byte[] reply = new byte[64];
        if (!QuicDatagramReader.TryVersionNegotiation(datagram, reply, out int written))
        {
            return;
        }

        try
        {
            await _transport.SendAsync(reply.AsMemory(0, written), remote, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryDestination(ReadOnlySpan<byte> datagram, out byte[] id, out bool initial)
    {
        id = [];
        initial = false;
        if (datagram.IsEmpty)
        {
            return false;
        }

        if ((datagram[0] & 0x80) == 0)
        {
            Tracked? match = null;
            lock (_gate)
            {
                foreach (Tracked tracked in _connections.Values)
                {
                    if (datagram.Length > 1 + tracked.LocalId.Length
                        && datagram.Slice(1, tracked.LocalId.Length).SequenceEqual(tracked.LocalId)
                        && (match is null || tracked.LocalId.Length > match.LocalId.Length))
                    {
                        match = tracked;
                    }
                }
            }

            if (match is null)
            {
                return false;
            }

            id = match.LocalId;
            return true;
        }

        if (!QuicDatagramReader.TryParse(datagram, 0, out QuicPacket packet)
            || packet.DestinationConnectionId.IsEmpty)
        {
            return false;
        }

        id = packet.DestinationConnectionId.ToArray();
        initial = packet.Kind == QuicPacketKind.Initial;
        return true;
    }

    private sealed class Tracked
    {
        internal Tracked(QuicConnectionSession session, ManagedQuicConnection connection, byte[] localId, DateTimeOffset deadline)
        {
            Session = session;
            Connection = connection;
            LocalId = localId;
            Deadline = deadline;
        }

        internal byte[] LocalId { get; }

        internal DateTimeOffset Deadline { get; }

        internal QuicConnectionSession Session { get; }

        internal ManagedQuicConnection Connection { get; }

        internal bool Published { get; set; }
    }

    private sealed class CallbackTransport : IUdpPacketTransport
    {
        private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _send;

        internal CallbackTransport(IPEndPoint local, Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send, int maximumDatagramSize)
        {
            LocalEndpoint = local;
            _send = send;
            MaximumDatagramSize = maximumDatagramSize;
        }

        public IPEndPoint LocalEndpoint { get; }

        public int MaximumDatagramSize { get; }

        public bool DatagramPending => false;

        public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken cancellationToken)
        {
            if (datagram.Length > MaximumDatagramSize)
            {
                throw new ArgumentOutOfRangeException(nameof(datagram));
            }

            await _send(datagram, destination, cancellationToken).ConfigureAwait(false);
            return datagram.Length;
        }

        public async ValueTask<int> SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, CancellationToken cancellationToken)
        {
            int bytes = 0;
            for (int i = 0; i < datagrams.Length; i++)
            {
                bytes += await SendAsync(datagrams.Span[i], destination, cancellationToken).ConfigureAwait(false);
            }

            return bytes;
        }

        public ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An injected listener does not read a socket.");

        public void Dispose()
        {
        }
    }
}
