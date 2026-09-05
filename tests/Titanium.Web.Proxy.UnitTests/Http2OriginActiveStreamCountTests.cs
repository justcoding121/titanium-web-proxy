using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginActiveStreamCountTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public async Task ActiveStreamCount_TracksRegisterAndUnregister()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);

        Assert.AreEqual(0, connection.ActiveStreamCount);

        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, PrivateInstance, binder: null,
            args: [0L], culture: null)!;

        InvokeRegister(connection, 1, pending);
        Assert.AreEqual(1, connection.ActiveStreamCount);

        InvokeRegister(connection, 3, Activator.CreateInstance(pendingType, PrivateInstance, binder: null,
            args: [0L], culture: null)!);
        Assert.AreEqual(2, connection.ActiveStreamCount);

        Assert.IsTrue(InvokeTryUnregister(connection, 1));
        Assert.AreEqual(1, connection.ActiveStreamCount);

        Assert.IsFalse(InvokeTryUnregister(connection, 1));
        Assert.AreEqual(1, connection.ActiveStreamCount);

        Assert.IsTrue(InvokeTryUnregister(connection, 3));
        Assert.AreEqual(0, connection.ActiveStreamCount);
    }

    [TestMethod]
    public async Task ActiveStreamCount_DoesNotUseDictionaryCount()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await CreateShellAsync(proxy);

        var field = typeof(Http2OriginConnection).GetField("activeStreamCount", PrivateInstance)!;
        field.SetValue(connection, 7);
        Assert.AreEqual(7, connection.ActiveStreamCount);
    }

    [TestMethod]
    public async Task RentAsync_AtMaxAndSoftFull_DoesNotOpenAnother()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        var max = ProxyResourceLimits.Default.MaxOriginHttp2ConnectionsPerAuthority;
        var connections = new Http2OriginConnection[max];

        try
        {
            for (var i = 0; i < max; i++)
            {
                connections[i] = await CreateShellAsync(proxy);
                SetActive(connections[i], Http2OriginConnection.PoolGrowActiveStreamThreshold);
                pool.Offer("authority-at-max", connections[i]);
            }

            var opened = 0;
            var rented = await pool.RentAsync("authority-at-max", _ =>
            {
                opened++;
                return Task.FromResult(connections[0]);
            }, CancellationToken.None);

            Assert.AreEqual(0, opened, "CreationGate/open must not run when the authority is already at max.");
            Assert.IsNotNull(rented);
            Assert.IsTrue(Array.IndexOf(connections, rented) >= 0);
        }
        finally
        {
            await pool.DrainAsync();
        }
    }

    [TestMethod]
    public async Task RentAsync_UnderSoftCapacity_DoesNotOpen()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        var connection = await CreateShellAsync(proxy);
        // SoftStreamCapacity == SETTINGS/gate (SoftPick); SoftGrow is separate. active=0 is under SoftPick.
        SetActive(connection, 0);
        pool.Offer("authority-under", connection);

        try
        {
            var opened = 0;
            var rented = await pool.RentAsync("authority-under", _ =>
            {
                opened++;
                return Task.FromResult(connection);
            }, CancellationToken.None);

            Assert.AreEqual(0, opened);
            Assert.AreSame(connection, rented);
        }
        finally
        {
            await pool.DrainAsync();
        }
    }

    private static void SetActive(Http2OriginConnection connection, int value)
    {
        typeof(Http2OriginConnection).GetField("activeStreamCount", PrivateInstance)!
            .SetValue(connection, value);
    }

    private static void InvokeRegister(Http2OriginConnection connection, int streamId, object pending)
    {
        var method = typeof(Http2OriginConnection).GetMethod("RegisterOpenedStream", PrivateInstance)!;
        method.Invoke(connection, [streamId, pending]);
    }

    private static bool InvokeTryUnregister(Http2OriginConnection connection, int streamId)
    {
        var method = typeof(Http2OriginConnection).GetMethod("TryUnregisterStream", PrivateInstance)!;
        var args = new object?[] { streamId, null };
        return (bool)method.Invoke(connection, args)!;
    }

    private static async Task<Http2OriginConnection> CreateShellAsync(ProxyServer proxy)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await accept;
        accepted.Dispose();

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        var serverConn = new TcpServerConnection(proxy, client, stream, "origin.test", 443, true,
            default, HttpHeader.Version20, null, null, "h2-origin");

        var ctor = typeof(Http2OriginConnection).GetConstructor(PrivateInstance, null,
            [typeof(TcpServerConnection), typeof(Microsoft.Extensions.Logging.ILogger), typeof(long),
                typeof(ProxyResourceLimits)], null)!;
        return (Http2OriginConnection)ctor.Invoke([serverConn, NullLogger.Instance, 1024L * 1024L,
            ProxyResourceLimits.Default])!;
    }
}
