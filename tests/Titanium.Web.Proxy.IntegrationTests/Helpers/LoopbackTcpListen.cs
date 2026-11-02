using System;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     TCP listeners on <c>127.0.0.1</c> and <c>::1</c> at one port.
///     The parameterless port factory binds every interface, and Windows Firewall prompts for that.
/// </summary>
internal static class LoopbackTcpListen
{
    public static TcpListener[] Start()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
            v6.Start();
            var port = ((IPEndPoint)v6.LocalEndpoint).Port;
            var v4 = new TcpListener(IPAddress.Loopback, port);
            try
            {
                v4.Start();
                return new[] { v6, v4 };
            }
            catch (SocketException)
            {
                v6.Stop();
                v4.Stop();
            }
        }

        throw new InvalidOperationException("Could not bind 127.0.0.1 and ::1 to the same port.");
    }
}
