using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;

namespace Titanium.Web.Proxy.Masque;

/// <summary>Builds the certificate options for one terminated CONNECT-UDP target.</summary>
internal delegate ValueTask<ManagedQuicServerConnectionOptions> ConnectUdpQuicOptions(
    string host, SslClientHelloInfo hello, CancellationToken cancellationToken);

/// <summary>The managed-stack switch for one CONNECT-UDP relay. Null keeps the datagram on the socket.</summary>
internal sealed class ConnectUdpQuicSetup
{
    internal ConnectUdpQuicSetup(bool allowManagedStackUnderFips, ConnectUdpQuicOptions options)
    {
        AllowManagedStackUnderFips = allowManagedStackUnderFips;
        Options = options;
    }

    internal bool AllowManagedStackUnderFips { get; }

    internal ConnectUdpQuicOptions Options { get; }
}

/// <summary>One injected listener for the target of a CONNECT-UDP tunnel.</summary>
internal sealed class ConnectUdpQuic : IAsyncDisposable
{
    private readonly IPEndPoint _target;
    private readonly string _host;
    private readonly ConnectUdpQuicSetup _setup;
    private readonly IPEndPoint _remote = new(IPAddress.Loopback, 1);
    private ManagedQuicListener? _listener;

    internal ConnectUdpQuic(IPEndPoint target, ConnectUdpQuicSetup setup)
    {
        _target = target;
        _host = target.Address.ToString();
        _setup = setup;
    }

    internal bool Owns(ReadOnlySpan<byte> payload) =>
        _listener is not null || (payload.Length >= 1200 && (payload[0] & 0x80) != 0);

    internal async ValueTask HandleAsync(ReadOnlyMemory<byte> payload, Action<ReadOnlyMemory<byte>> reply, CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            _listener = ManagedQuicListener.CreateInjected(
                new ManagedQuicListenerOptions
                {
                    ListenEndPoint = _target,
                    AllowManagedStackUnderFips = _setup.AllowManagedStackUnderFips,
                    ConnectionOptionsCallback = (_, hello, token) => _setup.Options(_host, hello, token),
                },
                _target,
                (datagram, _, _) =>
                {
                    reply(datagram);
                    return ValueTask.CompletedTask;
                },
                maximumDatagramSize: 2000);
        }

        await _listener.HandleDatagramAsync(payload, _remote, cancellationToken).ConfigureAwait(false);
        await _listener.DriveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
    }
}
