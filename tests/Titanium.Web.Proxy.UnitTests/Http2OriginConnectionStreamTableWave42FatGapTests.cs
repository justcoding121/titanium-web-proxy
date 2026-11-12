#pragma warning disable TWP001
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginConnectionStreamTableWave42FatGapTests
{


    [TestMethod]
    public async Task OriginConnection_StreamTableGrowLookupAndDuplicateRegister()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        object Pending() => Activator.CreateInstance(pendingType, Wave42FatGapTestSupport.PrivateInstance, binder: null,
            args: [], culture: null)!;

        var register = typeof(Http2OriginConnection).GetMethod("RegisterOpenedStream", Wave42FatGapTestSupport.PrivateInstance)!;
        var tryGet = typeof(Http2OriginConnection).GetMethod("TryGetStream", Wave42FatGapTestSupport.PrivateInstance)!;
        var contains = typeof(Http2OriginConnection).GetMethod("StreamTableContains", Wave42FatGapTestSupport.PrivateInstance)!;
        var unregister = typeof(Http2OriginConnection).GetMethod("TryUnregisterStream", Wave42FatGapTestSupport.PrivateInstance)!;
        var enumerate = typeof(Http2OriginConnection).GetMethod("EnumerateLiveStreams", Wave42FatGapTestSupport.PrivateInstance)!;
        var ensure = typeof(Http2OriginConnection).GetMethod("EnsureStreamTable", Wave42FatGapTestSupport.PrivateInstance)!;

        // Force growth past the default 64-slot table (idx = streamId >> 1).
        ensure.Invoke(connection, [200]);
        register.Invoke(connection, [401, Pending()]);
        Assert.AreEqual(1, connection.ActiveStreamCount);
        Assert.IsTrue((bool)contains.Invoke(connection, [401])!);
        Assert.IsFalse((bool)contains.Invoke(connection, [403])!);

        var getArgs = new object?[] { 401, null };
        Assert.IsTrue((bool)tryGet.Invoke(connection, getArgs)!);
        Assert.IsNotNull(getArgs[1]);
        getArgs = [99999, null];
        Assert.IsFalse((bool)tryGet.Invoke(connection, getArgs)!);

        var unregArgs = new object?[] { 99999, null };
        Assert.IsFalse((bool)unregister.Invoke(connection, unregArgs)!);
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            register.Invoke(connection, [401, Pending()]));

        var live = new List<object>();
        foreach (var item in (IEnumerable)enumerate.Invoke(connection, null)!)
            live.Add(item!);
        Assert.AreEqual(1, live.Count);

        unregArgs = [401, null];
        Assert.IsTrue((bool)unregister.Invoke(connection, unregArgs)!);
        Assert.AreEqual(0, connection.ActiveStreamCount);

        connection.AcquireLease();
        connection.ReleaseLease();
        connection.Retire();
    }
}
#pragma warning restore TWP001
