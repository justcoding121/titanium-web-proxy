using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;
using Titanium.Web.Proxy.Abstractions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private readonly Dictionary<string, ManagedQuicListener> _transparentQuic = new(StringComparer.Ordinal);

    private readonly object _transparentQuicGate = new();

    /// <summary>
    /// Feeds one redirected datagram to an injected listener. The local address is the origin the client addressed.
    /// The reply is handed to <paramref name="send"/>. The listener does not open a socket.
    /// </summary>
    internal async ValueTask HandleTransparentQuicDatagramAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint client,
        IPEndPoint originalDestination,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken = default)
    {
        if (!UseManagedQuicStack)
        {
            throw new ManagedStackRequiredException("Transparent UDP interception", nameof(UseManagedQuicStack));
        }

        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(originalDestination);
        ArgumentNullException.ThrowIfNull(send);
        ManagedQuicListener listener = TransparentListener(originalDestination, send);
        await listener.HandleDatagramAsync(datagram, client, cancellationToken).ConfigureAwait(false);
        await listener.DriveAsync(cancellationToken).ConfigureAwait(false);
    }

    private ManagedQuicListener TransparentListener(
        IPEndPoint originalDestination,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send)
    {
        string key = originalDestination.ToString();
        lock (_transparentQuicGate)
        {
            if (_transparentQuic.TryGetValue(key, out ManagedQuicListener? existing))
            {
                return existing;
            }

            ManagedQuicListener created = ManagedQuicListener.CreateInjected(
                new ManagedQuicListenerOptions
                {
                    ListenEndPoint = originalDestination,
                    AllowManagedStackUnderFips = AllowManagedStackUnderFips,
                    ConnectionOptionsCallback = (_, hello, token) => SocksQuicOptionsAsync(originalDestination.Address.ToString(), hello, token),
                },
                originalDestination,
                send,
                maximumDatagramSize: 2000);
            _transparentQuic.Add(key, created);
            return created;
        }
    }

    private void DisposeTransparentQuic()
    {
        ManagedQuicListener[] listeners;
        lock (_transparentQuicGate)
        {
            listeners = new ManagedQuicListener[_transparentQuic.Count];
            _transparentQuic.Values.CopyTo(listeners, 0);
            _transparentQuic.Clear();
        }

        foreach (ManagedQuicListener listener in listeners)
        {
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
