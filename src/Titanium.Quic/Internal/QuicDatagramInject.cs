using System;

namespace Titanium.Quic;

/// <summary>Sees one outbound datagram. The path is the virtual tuple the core used for amplification and validation.</summary>
internal interface IQuicOutbound
{
    void OnDatagram(ReadOnlySpan<byte> datagram, PathInfo path);
}

/// <summary>Feeds one datagram into a connection and hands each reply to the callback. Relays pass a virtual path, not the socket's own tuple.</summary>
internal static class QuicDatagramInject
{
    internal static int Deliver<T>(QuicConnectionCore connection, DateTimeOffset now, ReadOnlySpan<byte> datagram, in PathInfo path, Span<byte> outbound, ref T callback)
        where T : IQuicOutbound
    {
        connection.HandleDatagram(now, datagram, path);
        int replies = 0;
        while (true)
        {
            int count = connection.PollTransmit(now, outbound, out PathInfo replyPath);
            if (count <= 0)
            {
                return replies;
            }

            callback.OnDatagram(outbound[..count], replyPath);
            replies++;
        }
    }
}
