#pragma warning disable CA1416 // QuicException is the BCL error type. This library does not start the platform QUIC stack.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Tls;

namespace Titanium.Quic;

/// <summary>One connection, its socket sends, and the single wait that drives loss and idle.</summary>
internal sealed class QuicConnectionSession : IDisposable
{
    internal const int ConnectionIdLength = 8;

    private readonly object _gate = new();

    private readonly QuicConnectionCore _core;

    private readonly IUdpPacketTransport _transport;

    private readonly bool _ownsTransport;

    private readonly CancellationTokenSource _stop = new();

    private readonly TaskCompletionSource<bool> _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<ManagedQuicStream> _streams = [];

    private Task? _loop;

    private bool _disposed;

    internal QuicConnectionSession(QuicConnectionCore core, IUdpPacketTransport transport, IPEndPoint local, IPEndPoint remote, bool ownsTransport)
    {
        _core = core;
        _transport = transport;
        LocalEndPoint = local;
        RemoteEndPoint = remote;
        _ownsTransport = ownsTransport;
    }

    internal IPEndPoint LocalEndPoint { get; }

    internal IPEndPoint RemoteEndPoint { get; private set; }

    internal Task Handshake => _handshake.Task;

    internal bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _core.IsClosed;
            }
        }
    }

    internal void StartClientLoop() => _loop = RunClientAsync(_stop.Token);

    internal void SetRemote(IPEndPoint remote) => RemoteEndPoint = remote;

    internal void Handle(ReadOnlySpan<byte> datagram, in PathInfo path, DateTimeOffset now)
    {
        lock (_gate)
        {
            _core.HandleDatagram(now, datagram, path);
            ObserveHandshake();
        }
    }

    internal bool TakePendingHello(out string serverName)
    {
        lock (_gate)
        {
            serverName = _core.PendingServerName;
            return _core.PendingHello;
        }
    }

    internal bool CompleteHello(X509Certificate2 certificate, string[]? applicationProtocols = null)
    {
        lock (_gate)
        {
            bool completed = _core.CompleteHello(certificate, applicationProtocols);
            ObserveHandshake();
            return completed;
        }
    }

    internal void Timeout(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_core.NextTimeout() is DateTimeOffset due && due <= now)
            {
                _core.HandleTimeout(now);
                ObserveHandshake();
            }
        }
    }

    internal DateTimeOffset? NextTimeout()
    {
        lock (_gate)
        {
            return _core.NextTimeout();
        }
    }

    internal void Close(long errorCode)
    {
        lock (_gate)
        {
            _core.Close(errorCode < 0 ? 0 : (ulong)errorCode);
        }
    }

    internal int CopyReceived(int offset, Span<byte> destination)
    {
        lock (_gate)
        {
            byte[] all = _core.ReceivedStream;
            if (offset >= all.Length || destination.IsEmpty)
            {
                return 0;
            }

            int count = Math.Min(destination.Length, all.Length - offset);
            all.AsSpan(offset, count).CopyTo(destination);
            return count;
        }
    }

    internal void Finish(ulong streamId)
    {
        lock (_gate)
        {
            _core.FinishStream(streamId);
        }
    }

    internal void Send(ulong streamId, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_core.IsClosed)
            {
                string why = _core.ClosedByStatelessReset
                    ? "a stateless reset"
                    : "error 0x" + _core.CloseError.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
                throw new QuicException(QuicError.ConnectionAborted, (long)_core.CloseError, "The connection closed with " + why + ".");
            }

            int queued = _core.SendStream(streamId, data);
            if (queued != data.Length)
            {
                throw new QuicException(QuicError.InternalError, null, "The stream did not accept the write.");
            }
        }
    }

    internal async ValueTask FlushAsync()
    {
        byte[][] packets = Drain();
        if (packets.Length == 0)
        {
            return;
        }

        if (packets.Length == 1)
        {
            await _transport.SendAsync(packets[0], RemoteEndPoint, _stop.Token).ConfigureAwait(false);
            return;
        }

        var batch = new ReadOnlyMemory<byte>[packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            batch[i] = packets[i];
        }

        await _transport.SendBatchAsync(batch, RemoteEndPoint, _stop.Token).ConfigureAwait(false);
    }

    internal void AddStream(ManagedQuicStream stream)
    {
        lock (_gate)
        {
            _streams.Add(stream);
        }
    }

    internal void NotifyStreams()
    {
        ManagedQuicStream[] streams;
        lock (_gate)
        {
            streams = _streams.ToArray();
        }

        for (int i = 0; i < streams.Length; i++)
        {
            streams[i].Notify();
        }
    }

    internal void SignalClosedHandshake()
    {
        if (_core.IsClosed && !_core.HandshakeComplete)
        {
            _handshake.TrySetException(new QuicException(QuicError.ConnectionAborted, (long)_core.CloseError, "The connection closed before the handshake completed."));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        DisposeTransport();
        _core.Dispose();
        _stop.Dispose();
    }

    internal async Task StopAsync()
    {
        _stop.Cancel();
        DisposeTransport();
        if (_loop is not null)
        {
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
            catch (SocketException)
            {
            }
        }
    }

    private bool _transportDisposed;

    private void DisposeTransport()
    {
        if (_ownsTransport && !_transportDisposed)
        {
            _transportDisposed = true;
            _transport.Dispose();
        }
    }

    private byte[][] Drain()
    {
        lock (_gate)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            var packets = new List<byte[]>();
            try
            {
                while (true)
                {
                    int count = _core.PollTransmit(TimeProvider.System.GetUtcNow(), buffer, out _);
                    if (count <= 0)
                    {
                        break;
                    }

                    var copy = new byte[count];
                    buffer.AsSpan(0, count).CopyTo(copy);
                    packets.Add(copy);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            return packets.ToArray();
        }
    }

    private void ObserveHandshake()
    {
        if (_core.HandshakeComplete)
        {
            _handshake.TrySetResult(true);
        }
        else if (_core.IsClosed)
        {
            _handshake.TrySetException(new QuicException(QuicError.ConnectionAborted, (long)_core.CloseError, "The connection closed before the handshake completed."));
        }
    }

    private async Task RunClientAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            await FlushAsync().ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                NotifyStreams();
                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                DateTimeOffset? due = NextTimeout();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (due is DateTimeOffset deadline)
                {
                    TimeSpan delay = deadline - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        Timeout(now);
                        await FlushAsync().ConfigureAwait(false);
                        if (!_transport.DatagramPending)
                        {
                            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        wait.CancelAfter(delay);
                    }
                }

                try
                {
                    UdpReceived received = await _transport.ReceiveAsync(buffer.AsMemory(0, 65536), wait.Token).ConfigureAwait(false);
                    PathInfo path = PathOf(LocalEndPoint, RemoteEndPoint);
                    Handle(buffer.AsSpan(0, received.Count), path, TimeProvider.System.GetUtcNow());
                    NotifyStreams();
                    await FlushAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Timeout(TimeProvider.System.GetUtcNow());
                    await FlushAsync().ConfigureAwait(false);
                }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested)
                {
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
            SignalClosedHandshake();
        }
    }

    internal static PathInfo PathOf(IPEndPoint local, IPEndPoint remote)
    {
        return new PathInfo(local.Address.GetAddressBytes(), local.Port, remote.Address.GetAddressBytes(), remote.Port);
    }

    internal static byte[] Parameters(bool client, byte[] localId)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = localId,
            InitialMaxData = 1 << 20,
            InitialMaxStreamDataBidiLocal = 1 << 20,
            InitialMaxStreamDataBidiRemote = 1 << 20,
            InitialMaxStreamDataUni = 1 << 20,
            InitialMaxStreamsBidi = 16,
            InitialMaxStreamsUni = 16,
        };
        if (!client)
        {
            parameters.OriginalDestinationConnectionId = localId;
        }

        byte[] buffer = new byte[512];
        int written = QuicTransportParametersCodec.Write(parameters, buffer);
        return buffer[..written];
    }

    internal static byte[] NewConnectionId()
    {
        byte[] id = new byte[ConnectionIdLength];
        RandomNumberGenerator.Fill(id);
        return id;
    }

    internal static string[]? ProtocolNames(IList<SslApplicationProtocol>? protocols)
    {
        if (protocols is null || protocols.Count == 0)
        {
            return null;
        }

        var names = new string[protocols.Count];
        for (int i = 0; i < protocols.Count; i++)
        {
            names[i] = protocols[i].ToString();
        }

        return names;
    }

    internal static void RejectFips(bool allow)
    {
        if (FipsEnvironment.IsEnabled() && !allow && !FipsEnvironment.OverrideIsSet())
        {
            throw new QuicException(
                QuicError.ConnectionRefused,
                null,
                "The managed QUIC stack is disabled while FIPS mode is on. Set AllowManagedStackUnderFips or TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1.");
        }
    }
}
