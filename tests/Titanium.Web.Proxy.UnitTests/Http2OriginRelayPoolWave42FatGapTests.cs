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
public class Http2OriginRelayPoolWave42FatGapTests
{


    [TestMethod]
    public async Task Http2OriginRelayPool_AssignReleaseAndFailedOpen()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var shell = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
        await using var relay = new Http2OriginRelayPool(
            shell.ServerConnection,
            _ => throw new IOException("cannot open another origin"),
            ProxyResourceLimits.Default,
            NullLogger.Instance,
            new SemaphoreSlim(1, 1));

        Assert.AreEqual(1, relay.LegCount);
        Assert.IsNotNull(relay.PrimaryLeg);
        Assert.AreEqual(1, relay.SnapshotLegs().Count);

        var first = await relay.AssignStreamAsync(1, CancellationToken.None);
        var again = await relay.AssignStreamAsync(1, CancellationToken.None);
        Assert.AreEqual(first.OriginStreamId, again.OriginStreamId);
        Assert.IsTrue(relay.TryGetAssignment(1, out var found));
        Assert.AreEqual(first.OriginStreamId, found.OriginStreamId);

        typeof(Http2OriginRelayPool.OriginLeg).GetField("ActiveStreams")!
            .SetValue(relay.PrimaryLeg, 10_000);
        var second = await relay.AssignStreamAsync(3, CancellationToken.None);
        Assert.IsTrue(second.OriginStreamId > 0);

        relay.ReleaseStream(1);
        relay.ReleaseStream(99);
        Assert.IsFalse(relay.TryGetAssignment(1, out _));
    }
}
#pragma warning restore TWP001
