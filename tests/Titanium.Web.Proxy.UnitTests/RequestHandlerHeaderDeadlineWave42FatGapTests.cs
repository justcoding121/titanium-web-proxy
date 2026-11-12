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
public class RequestHandlerHeaderDeadlineWave42FatGapTests
{


    [TestMethod]
    public void RequestHandler_ThrowIfHeaderDeadlineTimedOut_CoversFiredAndIdle()
    {
        var throwIf = typeof(ProxyServer).GetMethod("ThrowIfHeaderDeadlineTimedOut", Wave42FatGapTestSupport.PrivateStatic)!;
        var registry = new DeadlineRegistry();
        var idle = registry.Start(CancellationToken.None, null, ProxyTimeoutKind.ClientHeader);
        throwIf.Invoke(null, [idle]); // no-op

        using var cts = new CancellationTokenSource();
        var fired = registry.Start(cts.Token, TimeSpan.FromMilliseconds(5), ProxyTimeoutKind.ClientHeader);
        Assert.IsTrue(SpinWait.SpinUntil(() => fired.Token.IsCancellationRequested, TimeSpan.FromSeconds(2)));
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() => throwIf.Invoke(null, [fired]));
        Assert.IsInstanceOfType<ProxyTimeoutException>(ex.InnerException);
    }
}
#pragma warning restore TWP001
