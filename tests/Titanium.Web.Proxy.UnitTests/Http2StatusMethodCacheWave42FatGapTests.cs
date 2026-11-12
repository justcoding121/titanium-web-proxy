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
public class Http2StatusMethodCacheWave42FatGapTests
{


    [TestMethod]
    public void StatusAndMethodCaches_CoverSwitchFallbacks()
    {
        var status = typeof(Http2Helper).GetMethod("StatusCodeBytes", Wave42FatGapTestSupport.PrivateStatic)!;
        foreach (var code in new[] { 200, 204, 206, 301, 302, 304, 400, 404, 500, 502, 201 })
        {
            var bytes = (ByteString)status.Invoke(null, [code])!;
            Assert.AreEqual(code.ToString(), bytes.GetString());
        }

        var method = typeof(Http2Helper).GetMethod("MethodBytes", Wave42FatGapTestSupport.PrivateStatic)!;
        foreach (var name in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "CONNECT", "TRACE" })
        {
            var bytes = (ByteString)method.Invoke(null, [name])!;
            Assert.AreEqual(name, bytes.GetString());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http2OriginConnectionPool + shell connection bookkeeping
    // ─────────────────────────────────────────────────────────────────────────
}
#pragma warning restore TWP001
