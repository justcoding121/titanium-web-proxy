using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class PooledSocketLivenessTests
{
    private static (Socket Client, Socket Server) ConnectedPair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        return (client, listener.Accept());
    }

    [TestMethod]
    public void IsGoodConnection_IdleOpenConnection_IsGood()
    {
        var (client, server) = ConnectedPair();
        using (client)
        using (server)
        {
            Assert.IsTrue(client.IsGoodConnection());
        }
    }

    [TestMethod]
    public void IsGoodConnection_PeerSentFin_IsNotGood()
    {
        var (client, server) = ConnectedPair();
        using (client)
        {
            // Origin idle-closes its keep-alive connection while the proxy still holds it in the pool.
            server.Shutdown(SocketShutdown.Both);
            server.Close();
            Thread.Sleep(100);

            Assert.IsFalse(client.IsGoodConnection(),
                "A connection the origin already closed must not be handed out for a request that cannot be replayed.");
        }
    }

    [TestMethod]
    public void IsGoodConnection_PeerHasUnreadData_StaysGood()
    {
        var (client, server) = ConnectedPair();
        using (client)
        using (server)
        {
            // e.g. a TLS 1.3 session ticket not consumed yet: not a closed connection.
            server.Send(new byte[] { 1, 2, 3 });
            Thread.Sleep(100);

            Assert.IsTrue(client.IsGoodConnection());
        }
    }
}
