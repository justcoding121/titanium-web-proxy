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
public class Http2ResolveOriginAuthorityWave42FatGapTests
{


    [TestMethod]
    public void ResolveH2OriginAuthority_ParsesPortHostAndUriFallback()
    {
        var withPort = new Request { Authority = "origin.example:8443".GetByteString() };
        var parsed = ((string Host, int Port))Wave42FatGapTestSupport.BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [withPort])!;
        Assert.AreEqual("origin.example", parsed.Host);
        Assert.AreEqual(8443, parsed.Port);

        var bare = new Request { Authority = "origin.example".GetByteString() };
        parsed = ((string Host, int Port))Wave42FatGapTestSupport.BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [bare])!;
        Assert.AreEqual("origin.example", parsed.Host);
        Assert.AreEqual(443, parsed.Port);

        var fromUri = new Request { RequestUriString = "https://uri.example:9443/x" };
        parsed = ((string Host, int Port))Wave42FatGapTestSupport.BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [fromUri])!;
        Assert.AreEqual("uri.example", parsed.Host);
        Assert.AreEqual(9443, parsed.Port);

        var fromHost = new Request { RequestUriString = "/x" };
        fromHost.Headers.AddHeader("Host", "host.example:7443");
        parsed = ((string Host, int Port))Wave42FatGapTestSupport.BridgeMethod("ResolveH2OriginAuthority").Invoke(null, [fromHost])!;
        Assert.AreEqual("host.example", parsed.Host);
        Assert.AreEqual(7443, parsed.Port);
    }
}
#pragma warning restore TWP001
