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
public class Http2OriginPoolAuthorityWave42FatGapTests
{


    [TestMethod]
    public async Task Http2OriginPool_AuthorityEntrySnapshotPruneAndCapacity()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        var entryType = typeof(Http2OriginConnectionPool).GetNestedType("AuthorityEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, nonPublic: true)!;
        var connections = (System.Collections.IList)entryType
            .GetField("Connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(entry)!;

        using var usable = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
        using var retired = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
        retired.Retire();
        connections.Add(usable);
        connections.Add(retired);

        var snapshot = typeof(Http2OriginConnectionPool).GetMethod("SnapshotMembers", Wave42FatGapTestSupport.PrivateStatic)!;
        var snap = (Http2OriginConnection[])snapshot.Invoke(null, [entry])!;
        Assert.AreEqual(1, snap.Length);
        Assert.AreSame(usable, snap[0]);

        var limits = ProxyResourceLimits.Default.WithMaxOriginHttp2ConnectionsPerAuthority(4);
        var canOpen = typeof(Http2OriginConnectionPool).GetMethod("CanOpenAnother", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsTrue((bool)canOpen.Invoke(null, [entry, limits])!);
        Assert.IsFalse((bool)canOpen.Invoke(null, [entry, ProxyResourceLimits.Default])!); // default max=1

        for (var i = connections.Count; i < 4; i++)
            connections.Add(await Wave42FatGapTestSupport.CreateShellAsync(proxy));
        Assert.IsFalse((bool)canOpen.Invoke(null, [entry, limits])!);

        var tryAny = typeof(Http2OriginConnectionPool).GetMethod("TryPickAnyUsable", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsNotNull(tryAny.Invoke(null, [entry]));

        var prune = typeof(Http2OriginConnectionPool).GetMethod("PruneUnusableUnderLock", Wave42FatGapTestSupport.PrivateStatic)!;
        typeof(Http2OriginConnection).GetField("lastStreamId", Wave42FatGapTestSupport.PrivateInstance)!.SetValue(usable, int.MaxValue - 1);
        lock (entryType.GetField("Gate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                  .GetValue(entry)!)
            prune.Invoke(null, [entry]);
        Assert.AreEqual(3, connections.Count); // exhausted usable pruned

        await pool.DrainAsync();
    }
}
#pragma warning restore TWP001
