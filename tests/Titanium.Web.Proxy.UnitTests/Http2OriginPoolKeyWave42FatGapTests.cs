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
public class Http2OriginPoolKeyWave42FatGapTests
{


    [TestMethod]
    public void BuildPoolKey_DiffersByHttpsCleartextUpstreamAndConnect()
    {
        using var proxy = new ProxyServer(false, false, false);
        var explicitEp = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var cleartext = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardCleartext = true };
        var https = new TransparentProxyEndPoint(IPAddress.Loopback, 0, true);

        var explicitKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, explicitEp, null, null, "origin.test", 443, null, null);
        var clearKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, cleartext, null, null, "origin.test", 80, null, null);
        var httpsKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, https, null, null, "origin.test", 443, null, null);
        var upstreamKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, explicitEp, new ExternalProxy("up.example", 8080),
            new IPEndPoint(IPAddress.Loopback, 9), "origin.test", 443, "connect.host", 8443);

        Assert.AreNotEqual(explicitKey, clearKey);
        Assert.AreNotEqual(clearKey, httpsKey);
        Assert.AreNotEqual(explicitKey, upstreamKey);

        using var session = Wave42FatGapTestSupport.MakeSession(proxy, explicitEp);
        var sessionKey = Http2OriginConnectionPool.BuildPoolKey(
            proxy, session, "origin.test", 443, "connect.host", 8443);
        Assert.IsFalse(string.IsNullOrEmpty(sessionKey));
    }
}
#pragma warning restore TWP001
