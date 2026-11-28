#pragma warning disable CA1416 // QuicException is the BCL error type. This library does not start the platform QUIC stack.
using System;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Quic;

/// <summary>A managed QUIC connection. Names follow <see cref="QuicConnection"/>.</summary>
public sealed class ManagedQuicConnection : IAsyncDisposable
{
    private readonly QuicConnectionSession _session;

    internal ManagedQuicConnection(QuicConnectionSession session) => _session = session;

    /// <summary>Gets the local UDP endpoint.</summary>
    public IPEndPoint LocalEndPoint => _session.LocalEndPoint;

    /// <summary>Gets the remote UDP endpoint.</summary>
    public IPEndPoint RemoteEndPoint => _session.RemoteEndPoint;

    internal bool HandshakeCompleted => _session.Handshake.IsCompletedSuccessfully;

    /// <summary>Connects to a server and waits for the handshake.</summary>
    /// <param name="options">The remote endpoint, the server name, and the FIPS switch.</param>
    /// <param name="cancellationToken">A token that cancels the handshake.</param>
    /// <returns>The connected client.</returns>
    public static async ValueTask<ManagedQuicConnection> ConnectAsync(ManagedQuicClientConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RemoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        QuicConnectionSession.RejectFips(options.AllowManagedStackUnderFips);
        IPEndPoint remote = options.RemoteEndPoint;
        IPEndPoint local = options.LocalEndPoint ?? new IPEndPoint(
            remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback,
            0);
        string serverName = options.ClientAuthenticationOptions?.TargetHost ?? string.Empty;
        if (serverName.Length == 0)
        {
            serverName = "localhost";
        }

        if (options.MaxInboundBidirectionalStreams < 0 || options.MaxInboundUnidirectionalStreams < 0
            || (ulong)options.MaxInboundBidirectionalStreams > QuicTransportParametersCodec.MaxStreamsLimit
            || (ulong)options.MaxInboundUnidirectionalStreams > QuicTransportParametersCodec.MaxStreamsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A stream limit is outside the range this connection can advertise.");
        }

        if (options.IdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The idle timeout is negative.");
        }

        if (options.KeepAliveInterval < TimeSpan.Zero && options.KeepAliveInterval != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The keep-alive interval is negative.");
        }

        if (options.DefaultStreamErrorCode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The stream error code is negative.");
        }

        TimeSpan budget = options.HandshakeTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : options.HandshakeTimeout;
        ulong idleMs = options.IdleTimeout <= TimeSpan.Zero ? 0UL : (ulong)options.IdleTimeout.TotalMilliseconds;
        var transport = new UdpSocketTransport(local);
        var core = new QuicConnectionCore();
        var session = new QuicConnectionSession(core, transport, transport.LocalEndpoint, remote, ownsTransport: true);
        try
        {
            byte[] localId = QuicConnectionSession.NewConnectionId();
            byte[] remoteId = QuicConnectionSession.NewConnectionId();
            PathInfo path = QuicConnectionSession.PathOf(transport.LocalEndpoint, remote);
            if (options.KeepAliveInterval > TimeSpan.Zero && options.KeepAliveInterval != Timeout.InfiniteTimeSpan)
            {
                core.SetKeepAlive(options.KeepAliveInterval);
            }

            session.DefaultStreamErrorCode = options.DefaultStreamErrorCode;
            core.StartClient(
                path,
                localId,
                remoteId,
                QuicConnectionSession.Parameters(
                    client: true,
                    localId,
                    (ulong)options.MaxInboundBidirectionalStreams,
                    (ulong)options.MaxInboundUnidirectionalStreams,
                    idleMs),
                default,
                serverName,
                QuicConnectionSession.ProtocolNames(options.ClientAuthenticationOptions?.ApplicationProtocols));
            session.StartClientLoop();
            var connection = new ManagedQuicConnection(session);
            using var timeout = new CancellationTokenSource(budget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                await session.Handshake.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new QuicException(QuicError.ConnectionTimeout, null, "The handshake timed out.");
            }

            return connection;
        }
        catch
        {
            await session.StopAsync().ConfigureAwait(false);
            session.Dispose();
            throw;
        }
    }

    /// <summary>Opens a locally initiated stream. Bidirectional ids are 0, 4, 8, and so on for a client. Unidirectional ids are 2, 6, 10, and so on.</summary>
    /// <param name="type">The stream type.</param>
    /// <param name="cancellationToken">A token that cancels the open.</param>
    /// <returns>The new stream.</returns>
    public ValueTask<ManagedQuicStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandshakeCompleted)
        {
            throw new QuicException(QuicError.InternalError, null, "The handshake is not complete.");
        }

        if (!_session.TryOpenLocal(type, out long id))
        {
            throw new QuicException(QuicError.InternalError, null, "The peer's stream limit is exhausted.");
        }

        var stream = new ManagedQuicStream(_session, id, type);
        _session.AddStream(stream);
        return ValueTask.FromResult(stream);
    }

    /// <summary>Accepts the next remotely initiated bidirectional stream.</summary>
    /// <param name="cancellationToken">A token that cancels the accept.</param>
    /// <returns>The accepted stream.</returns>
    public ValueTask<ManagedQuicStream> AcceptInboundStreamAsync(CancellationToken cancellationToken = default) =>
        AcceptRemoteAsync(QuicStreamType.Bidirectional, cancellationToken);

    /// <summary>Accepts the next remotely initiated unidirectional stream.</summary>
    /// <param name="cancellationToken">A token that cancels the accept.</param>
    /// <returns>The accepted stream.</returns>
    public ValueTask<ManagedQuicStream> AcceptInboundUnidirectionalStreamAsync(CancellationToken cancellationToken = default) =>
        AcceptRemoteAsync(QuicStreamType.Unidirectional, cancellationToken);

    private ValueTask<ManagedQuicStream> AcceptRemoteAsync(QuicStreamType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandshakeCompleted)
        {
            throw new QuicException(QuicError.InternalError, null, "The handshake is not complete.");
        }

        if (!_session.TryAcceptRemote(type, out long id))
        {
            throw new QuicException(QuicError.InternalError, null, "The local stream limit is exhausted.");
        }

        var stream = new ManagedQuicStream(_session, id, type);
        _session.AddStream(stream);
        return ValueTask.FromResult(stream);
    }

    /// <summary>Sends a ping so a quiet peer still receives a packet.</summary>
    /// <param name="cancellationToken">A token that cancels the send.</param>
    /// <returns>A task that completes when the ping has been sent.</returns>
    public async ValueTask PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session.Ping();
        await _session.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Sends <c>CONNECTION_CLOSE</c> with the application error code.</summary>
    /// <param name="errorCode">The application close code. Zero is no error.</param>
    /// <param name="cancellationToken">A token that cancels the send.</param>
    /// <returns>A task that completes when the close datagram has been sent.</returns>
    public async ValueTask CloseAsync(long errorCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session.Close(errorCode);
        await _session.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the socket and releases the connection.</summary>
    /// <returns>A task that completes when the connection has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _session.StopAsync().ConfigureAwait(false);
        _session.Dispose();
    }
}
