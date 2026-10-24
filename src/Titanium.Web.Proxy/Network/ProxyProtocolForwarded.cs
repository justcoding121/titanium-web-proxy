using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Titanium.Web.Proxy.Network;

/// <summary>Hands a parsed PROXY client address from the accept loop to the connection.</summary>
internal static class ProxyProtocolForwarded
{
    private static int _count;
    private static readonly ConditionalWeakTable<Socket, StrongBox<IPEndPoint>> Table = new();

    public static void Remember(Socket socket, IPEndPoint client)
    {
        Table.Add(socket, new StrongBox<IPEndPoint>(client));
        Interlocked.Increment(ref _count);
    }

    public static bool TryTake(Socket socket, out IPEndPoint? client)
    {
        client = null;
        if (Volatile.Read(ref _count) == 0)
        {
            return false;
        }

        if (!Table.TryGetValue(socket, out var box))
        {
            return false;
        }

        Table.Remove(socket);
        Interlocked.Decrement(ref _count);
        client = box.Value;
        return client is not null;
    }
}
