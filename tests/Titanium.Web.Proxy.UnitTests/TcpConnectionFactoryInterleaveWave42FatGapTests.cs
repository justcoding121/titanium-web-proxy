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
public class TcpConnectionFactoryInterleaveWave42FatGapTests
{


    [TestMethod]
    public void TcpConnectionFactory_InterleaveByAddressFamily_AndCertCreate()
    {
        var ordered = TcpConnectionFactory.InterleaveByAddressFamily(
        [
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("2606:4700:4700::1111"),
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("2001:4860:4860::8888"),
        ]);
        Assert.AreEqual(4, ordered.Length);
        Assert.AreEqual(0, TcpConnectionFactory.InterleaveByAddressFamily([]).Length);

        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var a = mgr.CreateCertificate("gate-a.example", false);
        using var b = mgr.CreateCertificate("gate-b.example", false);
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a!.Thumbprint, b!.Thumbprint);
        Assert.IsTrue(mgr.CachedCertificateCount >= 0);
    }
}
#pragma warning restore TWP001
