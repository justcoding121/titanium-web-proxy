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
public class ProxyServerTryRejectLoopedViaWave42FatGapTests
{


    [TestMethod]
    public void TryRejectLoopedVia_CoversFastPathAndLoop()
    {
        using var proxy = new ProxyServer(false, false, false) { ViaHeaderPseudonym = "twp-test" };
        var reject = typeof(ProxyServer).GetMethod("TryRejectLoopedVia", Wave42FatGapTestSupport.PrivateInstance)!;

        using var fast = Wave42FatGapTestSupport.MakeSession(proxy);
        fast.IsFastPath = true;
        Assert.IsFalse((bool)reject.Invoke(proxy, [fast])!);

        using var unnamed = new ProxyServer(false, false, false);
        using var emptyName = Wave42FatGapTestSupport.MakeSession(unnamed);
        Assert.IsFalse((bool)reject.Invoke(unnamed, [emptyName])!);

        using var ok = Wave42FatGapTestSupport.MakeSession(proxy);
        Assert.IsFalse((bool)reject.Invoke(proxy, [ok])!);
        Assert.IsNotNull(ok.HttpClient.Request.Headers.GetHeaderValueOrNull("Via"));

        using var looped = Wave42FatGapTestSupport.MakeSession(proxy);
        looped.HttpClient.Request.Headers.AddHeader("Via", "2.0 twp-test");
        Assert.IsTrue((bool)reject.Invoke(proxy, [looped])!);
        Assert.AreEqual(508, looped.HttpClient.Response.StatusCode);
    }
}
#pragma warning restore TWP001
