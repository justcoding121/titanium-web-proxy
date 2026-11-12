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
public class Http2OriginConnectionCreditWave42FatGapTests
{


    [TestMethod]
    public async Task OriginConnection_CreditFailAndUnusableSend()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var connection = await Wave42FatGapTestSupport.CreateShellAsync(proxy);

        typeof(Http2OriginConnection).GetField("concurrencyGateCapacity", Wave42FatGapTestSupport.PrivateInstance)!
            .SetValue(connection, 16);
        // SoftPick = SETTINGS/gate; TLS SoftGrow SoftPick SoftCap; cleartext SoftGrow=8.
        Assert.AreEqual(16, connection.SoftStreamCapacity);
        Assert.AreEqual(connection.SoftStreamCapacity, connection.PoolGrowThreshold);

        typeof(Http2OriginConnection).GetMethod("AttachExclusiveFrameWriter", Wave42FatGapTestSupport.PrivateInstance)!
            .Invoke(connection, null);

        var ack = typeof(Http2OriginConnection).GetMethod("SendSettingsAckAsync", Wave42FatGapTestSupport.PrivateInstance)!;
        await (Task)ack.Invoke(connection, [CancellationToken.None])!;
        var ping = typeof(Http2OriginConnection).GetMethod("SendPingAckAsync", Wave42FatGapTestSupport.PrivateInstance)!;
        await (Task)ping.Invoke(connection, [new byte[8], CancellationToken.None])!;
        var rst = typeof(Http2OriginConnection).GetMethod("ResetStreamAsync", Wave42FatGapTestSupport.PrivateInstance)!;
        await (Task)rst.Invoke(connection, [3, Http2ErrorCode.Cancel, CancellationToken.None])!;

        var complete = typeof(Http2OriginConnection).GetMethod("CompleteStream", Wave42FatGapTestSupport.PrivateInstance)!;
        complete.Invoke(connection, [99]);
        var failStream = typeof(Http2OriginConnection).GetMethod("FailStream", Wave42FatGapTestSupport.PrivateInstance)!;
        failStream.Invoke(connection, [99, new IOException("gone")]);

        var writeTunnel = typeof(Http2OriginConnection).GetMethod("WriteTunnelDataAsync", Wave42FatGapTestSupport.PrivateInstance)!;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await (Task)writeTunnel.Invoke(connection, [7, ReadOnlyMemory<byte>.Empty, false, CancellationToken.None])!);

        var grant = typeof(Http2OriginConnection).GetMethod("GrantReceiveCreditAsync", Wave42FatGapTestSupport.PrivateInstance)!;
        await (Task)grant.Invoke(connection, [1, 0, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, 16, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, Http2Helper.ReceiveCreditBatchThreshold, false, CancellationToken.None])!;
        await (Task)grant.Invoke(connection, [1, 0, true, CancellationToken.None])!;

        var fail = typeof(Http2OriginConnection).GetMethod("Fail", Wave42FatGapTestSupport.PrivateInstance)!;
        fail.Invoke(connection, [new IOException("peer closed"), true]);
        fail.Invoke(connection, [new IOException("HTTP/2 protocol error: bad settings"), true]);
        fail.Invoke(connection, [new ProxyHttpException("wrapped", new IOException("x"), null), false]);
        Assert.IsFalse(connection.IsUsable);

        await Assert.ThrowsExactlyAsync<Http2OriginGoAwayException>(() =>
            connection.SendAsync(new Request { Method = "GET" }, null, CancellationToken.None));

        var violation = typeof(Http2OriginConnection).GetMethod("IsHttp2ProtocolViolation", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsTrue((bool)violation.Invoke(null, [new IOException("HTTP/2 protocol error: x")])!);
        Assert.IsFalse((bool)violation.Invoke(null, [new IOException("reset")])!);
        connection.Retire();
    }
}
#pragma warning restore TWP001
