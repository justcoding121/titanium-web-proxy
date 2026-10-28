using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Masque;

/// <summary>
///     One UDP flow inside an HTTP tunnel. <see cref="CapsuleDatagramChannel" /> speaks RFC 9297.
///     A future QUIC datagram channel implements the same three methods when .NET exposes datagrams
///     (dotnet/runtime#53533). The relay never calls MsQuic.
/// </summary>
internal interface IHttpDatagramChannel
{
    ValueTask RunClientToTargetAsync(DatagramSend send, CancellationToken cancellationToken);

    bool TryQueueToClient(ReadOnlySpan<byte> payload);

    ValueTask WriteQueuedAsync(CancellationToken cancellationToken);
}
