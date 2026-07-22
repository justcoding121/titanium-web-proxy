„-
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\CertificateManagerTests.cs·,using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class CertificateManagerTests
    {
        private static readonly string[] hostNames
            = { "facebook.com", "youtube.com", "google.com", "bing.com", "yahoo.com" };


        [TestMethod]
        public async Task Simple_BC_Create_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, new Lazy<ExceptionHandler>(() => e =>
            {
                Debug.WriteLine(e.ToString());
                Debug.WriteLine(e.InnerException?.ToString());
            }).Value)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            mgr.ClearIdleCertificates();
            for (var i = 0; i < 5; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    // get the connection
                    var certificate = mgr.CreateCertificate(host, false);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleCertificates();
        }

        // uncomment this to compare WinCert maker performance with BC (BC takes more time for same test above)
        //[TestMethod]
        public async Task Simple_Create_Win_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, new Lazy<ExceptionHandler>(() => e =>
                {
                    Debug.WriteLine(e.ToString());
                    Debug.WriteLine(e.InnerException?.ToString());
                }).Value)
                { CertificateEngine = CertificateEngine.DefaultWindows };

            mgr.CreateRootCertificate();
            mgr.TrustRootCertificate(true);
            mgr.ClearIdleCertificates();

            for (var i = 0; i < 5; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    // get the connection
                    var certificate = mgr.CreateCertificate(host, false);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());
            mgr.RemoveTrustedRootCertificate(true);
            mgr.StopClearIdleCertificates();
        }

        [TestMethod]
        public async Task Create_Server_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, new Lazy<ExceptionHandler>(() => e =>
                {
                    Debug.WriteLine(e.ToString());
                    Debug.WriteLine(e.InnerException?.ToString());
                }).Value)
                { CertificateEngine = CertificateEngine.BouncyCastleFast };

            mgr.SaveFakeCertificates = true;

            for (var i = 0; i < 500; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    var certificate = mgr.CreateServerCertificate(host);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());
        }

        [TestMethod]
        public async Task CreateServerCertificate_ExpiredCachedCertificate_IsRegenerated()
        {
            var mgr = new CertificateManager(null, null, false, false, false, new Lazy<ExceptionHandler>(() => e =>
                {
                    Debug.WriteLine(e.ToString());
                    Debug.WriteLine(e.InnerException?.ToString());
                }).Value)
                { CertificateEngine = CertificateEngine.BouncyCastleFast };

            const string host = "expired.test";

            // build an already-expired self-signed certificate and inject it into the in-memory cache
            X509Certificate2 expiredCert;
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                expiredCert = request.CreateSelfSigned(
                    DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));
            }

            var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField);
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField.GetValue(mgr);
            cache[host] = new CachedCertificate(expiredCert) { LastAccess = DateTime.UtcNow };

            // capture before the call: the expired cert is evicted and disposed by the fix
            var expiredThumbprint = expiredCert.Thumbprint;

            var result = await mgr.CreateServerCertificate(host);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.NotAfter > DateTime.Now, "regenerated certificate should be valid");
            Assert.AreNotEqual(expiredThumbprint, result.Thumbprint,
                "expired cached certificate should have been replaced");
        }
    }
}ParseOptions.0.json£4
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ConnectionCacheKeyTests.cs°3using System.Collections.Generic;
using System.Net.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ConnectionCacheKeyTests
    {
        private static TcpConnectionFactory CreateFactory()
        {
            return new TcpConnectionFactory(new ProxyServer());
        }

        private static ExternalProxy HttpProxy(string user, string password)
        {
            return new ExternalProxy("proxy.example", 8080)
            {
                ProxyType = ExternalProxyType.Http,
                UserName = user,
                Password = password
            };
        }

        [TestMethod]
        public void CacheKey_DifferentExplicitCredentials_ProduceDifferentKeys()
        {
            var factory = CreateFactory();
            try
            {
                var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, HttpProxy("alice", "pw1"));
                var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, HttpProxy("bob", "pw2"));

                Assert.AreNotEqual(key1, key2);
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_DefaultCredentialsFlag_ProducesDifferentKey()
        {
            var factory = CreateFactory();
            try
            {
                // explicit credentials (setting UserName/Password forces UseDefaultCredentials = false)
                var explicitCreds = factory.GetConnectionCacheKey("example.com", 443, true, null, null,
                    HttpProxy("alice", "pw1"));

                // default (Windows) credentials mode
                var defaultCreds = factory.GetConnectionCacheKey("example.com", 443, true, null, null,
                    new ExternalProxy("proxy.example", 8080)
                        { ProxyType = ExternalProxyType.Http, UseDefaultCredentials = true });

                Assert.AreNotEqual(explicitCreds, defaultCreds);
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_ProxyVsDirect_ProduceDifferentKeys()
        {
            var factory = CreateFactory();
            try
            {
                var direct = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null);
                var viaProxy = factory.GetConnectionCacheKey("example.com", 443, true, null, null,
                    HttpProxy("alice", "pw1"));

                Assert.AreNotEqual(direct, viaProxy);
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_ProxyDnsRequestsToggle_ProduceDifferentKeys()
        {
            var factory = CreateFactory();
            try
            {
                var socksLocal = new ExternalProxy("proxy.example", 1080)
                    { ProxyType = ExternalProxyType.Socks5, ProxyDnsRequests = false };
                var socksRemote = new ExternalProxy("proxy.example", 1080)
                    { ProxyType = ExternalProxyType.Socks5, ProxyDnsRequests = true };

                var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, socksLocal);
                var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, socksRemote);

                Assert.AreNotEqual(key1, key2);
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void EffectiveUpstreamProxy_BypassLocalhost_ReturnsNullForLocalHost()
        {
            var proxy = new ExternalProxy("proxy.example", 8080) { BypassLocalhost = true, ProxyDnsRequests = true };

            var forLocal = TcpConnectionFactory.GetEffectiveUpstreamProxy(proxy, "127.0.0.1", 443);
            var forRemote = TcpConnectionFactory.GetEffectiveUpstreamProxy(proxy, "example.com", 443);

            Assert.IsNull(forLocal, "local destination should bypass the proxy");
            Assert.AreSame(proxy, forRemote, "remote destination should keep the proxy");
        }

        [TestMethod]
        public void EffectiveUpstreamProxy_ProxyEqualsDestination_ReturnsNull()
        {
            var proxy = new ExternalProxy("example.com", 443);

            var effective = TcpConnectionFactory.GetEffectiveUpstreamProxy(proxy, "example.com", 443);

            Assert.IsNull(effective);
        }

        [TestMethod]
        public void NegotiatedProtocolCompatible_Rules()
        {
            // no requested protocols => always compatible
            Assert.IsTrue(TcpConnectionFactory.IsNegotiatedProtocolCompatible(SslApplicationProtocol.Http11, null));

            // default negotiated (plain/unknown) => compatible
            Assert.IsTrue(TcpConnectionFactory.IsNegotiatedProtocolCompatible(default,
                new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 }));

            // negotiated matches requested => compatible
            Assert.IsTrue(TcpConnectionFactory.IsNegotiatedProtocolCompatible(SslApplicationProtocol.Http2,
                new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 }));

            // negotiated http/1.1 but request requires http/2 => incompatible
            Assert.IsFalse(TcpConnectionFactory.IsNegotiatedProtocolCompatible(SslApplicationProtocol.Http11,
                new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 }));
        }

        [TestMethod]
        public void CredentialFingerprint_IsUnambiguousAndStable()
        {
            Assert.AreEqual(string.Empty, TcpConnectionFactory.GetCredentialFingerprint(null, null));

            // stable for identical inputs
            Assert.AreEqual(
                TcpConnectionFactory.GetCredentialFingerprint("user", "pass"),
                TcpConnectionFactory.GetCredentialFingerprint("user", "pass"));

            // no ambiguity between ("ab","c") and ("a","bc")
            Assert.AreNotEqual(
                TcpConnectionFactory.GetCredentialFingerprint("ab", "c"),
                TcpConnectionFactory.GetCredentialFingerprint("a", "bc"));
        }
    }
}
ParseOptions.0.json 
eD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\HpackRegressionTests.csÀusing System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class HpackRegressionTests
    {
        [TestMethod]
        public void Decode_FragmentedStringLiteral_EmitsCompleteHeader()
        {
            var encodedHeader = new byte[]
            {
                0x00, // literal header without indexing, new name
                0x03, (byte)'f', (byte)'o', (byte)'o',
                0x03, (byte)'b', (byte)'a', (byte)'r'
            };
            var listener = new RecordingHeaderListener();
            var decoder = new Decoder(8192, 4096);

            using (var stream = new FragmentedReadStream(encodedHeader))
            using (var reader = new BinaryReader(stream))
            {
                decoder.Decode(reader, listener);
                decoder.EndHeaderBlock();
            }

            Assert.AreEqual(1, listener.Headers.Count);
            Assert.AreEqual("foo", listener.Headers[0].Item1);
            Assert.AreEqual("bar", listener.Headers[0].Item2);
        }

        [TestMethod]
        public void DynamicTable_WrappedEntriesSurviveCapacityChangeInIndexOrder()
        {
            var table = new DynamicTable(68);
            table.Add(new HttpHeader("a", "1"));
            table.Add(new HttpHeader("b", "2"));

            // Adding the third same-sized entry evicts the oldest and wraps the circular queue.
            table.Add(new HttpHeader("c", "3"));
            table.SetCapacity(100);

            Assert.AreEqual(2, table.Length());
            Assert.AreEqual("c", table.GetEntry(1).Name);
            Assert.AreEqual("3", table.GetEntry(1).Value);
            Assert.AreEqual("b", table.GetEntry(2).Name);
            Assert.AreEqual("2", table.GetEntry(2).Value);
        }

        private sealed class RecordingHeaderListener : IHeaderListener
        {
            internal List<Tuple<string, string>> Headers { get; } = new List<Tuple<string, string>>();

            public void AddHeader(ByteString name, ByteString value, bool sensitive)
            {
                Headers.Add(Tuple.Create(name.ToString(), value.ToString()));
            }
        }

        private sealed class FragmentedReadStream : MemoryStream
        {
            internal FragmentedReadStream(byte[] buffer) : base(buffer)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, Math.Min(1, count));
            }
        }
    }
}
ParseOptions.0.jsonˇ
mD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\HttpModelAndProxySocketTests.cs¯using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.ProxySocket;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class HttpModelAndProxySocketTests
    {
        [TestMethod]
        public void NewHttpModels_HaveNonNullMethodDefaults()
        {
            var request = new Request();
            var response = new Response();

            Assert.AreEqual(string.Empty, request.Method);
            Assert.AreEqual(string.Empty, response.RequestMethod);
            Assert.AreEqual(string.Empty, response.StatusDescription);
        }

        [TestMethod]
        public void BeginConnect_InvalidProxyTypeWithProxyEndpoint_Throws()
        {
            using (var socket = CreateSocket())
            {
                socket.ProxyEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
                socket.ProxyType = (ProxyTypes)int.MaxValue;

                var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, 80), null, null));

                StringAssert.Contains(exception.Message, "Unsupported proxy type");
            }
        }

        [TestMethod]
        public async Task BeginConnect_InvalidProxyTypeWithoutProxyEndpoint_ConnectsDirectly()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var acceptTask = listener.AcceptSocketAsync();

                using (var socket = CreateSocket())
                {
                    socket.ProxyType = (ProxyTypes)int.MaxValue;
                    socket.ProxyEndPoint = null;

                    var result = socket.BeginConnect(endpoint, null, null);
                    socket.EndConnect(result);

                    using (var accepted = await acceptTask)
                    {
                        Assert.IsTrue(socket.Connected);
                        Assert.IsTrue(accepted.Connected);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static Titanium.Web.Proxy.ProxySocket.ProxySocket CreateSocket()
        {
            return new Titanium.Web.Proxy.ProxySocket.ProxySocket(
                AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }
}
ParseOptions.0.json∂
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\Properties\AssemblyInfo.cs¥using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyDescription("")]
[assembly: AssemblyCopyright("Copyright ¬© Titanium 2015-2019")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("b517e3d0-d03b-436f-ab03-34ba0d5321af")]ParseOptions.0.jsonº
aD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ProxyServerTests.cs¡using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ProxyServerTests
    {
        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToAddress_WhenAddingNewEndpointToExistingAddress_ThenExceptionIsThrown()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            var firstIpAddress = IPAddress.Parse("127.0.0.1");
            var secondIpAddress = IPAddress.Parse("127.0.0.1");
            proxy.AddEndPoint(new ExplicitProxyEndPoint(firstIpAddress, port, false));

            // Act
            try
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(secondIpAddress, port, false));
            }
            catch (Exception exc)
            {
                // Assert
                StringAssert.Contains(exc.Message, "Cannot add another endpoint to same port");
                return;
            }

            Assert.Fail("An exception should be thrown by now");
        }

        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToAddress_WhenAddingNewEndpointToExistingAddress_ThenTwoEndpointsExists()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            var firstIpAddress = IPAddress.Parse("127.0.0.1");
            var secondIpAddress = IPAddress.Parse("192.168.1.1");
            proxy.AddEndPoint(new ExplicitProxyEndPoint(firstIpAddress, port, false));

            // Act
            proxy.AddEndPoint(new ExplicitProxyEndPoint(secondIpAddress, port, false));

            // Assert
            Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        }

        [TestMethod]
        public void GivenOneEndpointIsAlreadyAddedToPort_WhenAddingNewEndpointToExistingPort_ThenExceptionIsThrown()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Act
            try
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));
            }
            catch (Exception exc)
            {
                // Assert
                StringAssert.Contains(exc.Message, "Cannot add another endpoint to same port");
                return;
            }

            Assert.Fail("An exception should be thrown by now");
        }

        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToZeroPort_WhenAddingNewEndpointToExistingPort_ThenTwoEndpointsExists()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 0;
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Act
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Assert
            Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        }
    }
}ParseOptions.0.jsonÑ
gD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ResponseKeepAliveTests.csÉusing System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ResponseKeepAliveTests
    {
        private static readonly Version Http10 = new Version(1, 0);
        private static readonly Version Http11 = new Version(1, 1);
        private static readonly Version Http20 = new Version(2, 0);

        [TestMethod]
        public void Http11_NoConnectionHeader_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http11 };
            Assert.IsTrue(response.KeepAlive);
        }

        [TestMethod]
        public void Http11_ConnectionClose_IsNotKeepAlive()
        {
            var response = new Response { HttpVersion = Http11 };
            response.Headers.AddHeader("Connection", "close");
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_NoConnectionHeader_IsNotKeepAlive()
        {
            // HTTP/1.0 defaults to close: such a connection must not be pooled/reused.
            var response = new Response { HttpVersion = Http10 };
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_ConnectionKeepAlive_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http10 };
            response.Headers.AddHeader("Connection", "keep-alive");
            Assert.IsTrue(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_ConnectionClose_IsNotKeepAlive()
        {
            var response = new Response { HttpVersion = Http10 };
            response.Headers.AddHeader("Connection", "close");
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http2_NoConnectionHeader_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http20 };
            Assert.IsTrue(response.KeepAlive);
        }
    }
}
ParseOptions.0.jsonø
tD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\StreamAndCertificateRegressionTests.cs±using System.IO;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class StreamAndCertificateRegressionTests
    {
        [TestMethod]
        public async Task WriteAsync_ReadOnlyMemory_WritesOnlyRequestedBytes()
        {
            var destination = new MemoryStream();
            var stream = new HttpStream(
                new ProxyServer(),
                destination,
                new DefaultBufferPool(),
                CancellationToken.None,
                true);
            var data = new byte[] { 1, 2, 3 };

            await stream.WriteAsync(new System.ReadOnlyMemory<byte>(data), CancellationToken.None);

            CollectionAssert.AreEqual(data, destination.ToArray());
        }

        [TestMethod]
        public async Task ReadLineAsync_LineWithoutTrailingNewlineAtEof_ReturnsContent()
        {
            // Regression guard: the pooled buffer must not be returned before the final
            // string is built when the stream ends without a trailing '\n'.
            var payload = System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1");
            var source = new MemoryStream(payload);
            var stream = new HttpStream(
                new ProxyServer(),
                source,
                new DefaultBufferPool(),
                CancellationToken.None,
                false);

            var line = await stream.ReadLineAsync(CancellationToken.None);

            Assert.AreEqual("GET / HTTP/1.1", line);
        }

        [TestMethod]
        public async Task ReadLineAsync_MultipleLinesWithCrLf_ReturnsEachLine()
        {
            var payload = System.Text.Encoding.ASCII.GetBytes("first\r\nsecond\r\n");
            var source = new MemoryStream(payload);
            var stream = new HttpStream(
                new ProxyServer(),
                source,
                new DefaultBufferPool(),
                CancellationToken.None,
                false);

            var first = await stream.ReadLineAsync(CancellationToken.None);
            var second = await stream.ReadLineAsync(CancellationToken.None);

            Assert.AreEqual("first", first);
            Assert.AreEqual("second", second);
        }

        [TestMethod]
        public void CertificateCallbacks_NullSessionUseSafeDefaultsWithoutInvocation()
        {
            var validationInvoked = false;
            var selectionInvoked = false;
            var proxy = new ProxyServer();
            proxy.ServerCertificateValidationCallback += (sender, args) =>
            {
                validationInvoked = true;
                return Task.CompletedTask;
            };
            proxy.ClientCertificateSelectionCallback += (sender, args) =>
            {
                selectionInvoked = true;
                return Task.CompletedTask;
            };

            var valid = proxy.ValidateServerCertificate(
                proxy, null, null, null, SslPolicyErrors.None);
            var invalid = proxy.ValidateServerCertificate(
                proxy, null, null, null, SslPolicyErrors.RemoteCertificateNotAvailable);
            var selected = proxy.SelectClientCertificate(
                proxy, null, "example.test", null, null, null);

            Assert.IsTrue(valid);
            Assert.IsFalse(invalid);
            Assert.IsNull(selected);
            Assert.IsFalse(validationInvoked);
            Assert.IsFalse(selectionInvoked);
        }
    }
}
ParseOptions.0.json˚2
`D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\SystemProxyTest.csÅ2using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class SystemProxyTest
    {
        [TestMethod]
        public void CompareProxyAddressReturnedByWebProxyAndWinHttpProxyResolver()
        {
            var proxyManager = new SystemProxyManager();

            try
            {
                CompareUrls();

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Http);
                CompareUrls();

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Https);
                CompareUrls();

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.AllHttp);
                CompareUrls();

                // for this test you need to add a proxy.pac file to a local webserver
                //function FindProxyForURL(url, host)
                //{
                //    if (shExpMatch(host, "google.com"))
                //    {
                //        return "PROXY 127.0.0.1:8888";
                //    }

                //    return "DIRECT";
                //}

                //proxyManager.SetAutoProxyUrl("http://localhost/proxy.pac");
                //CompareUrls();

                proxyManager.SetProxyOverride("<-loopback>");
                CompareUrls();

                proxyManager.SetProxyOverride("<local>");
                CompareUrls();

                proxyManager.SetProxyOverride("yahoo.com");
                CompareUrls();

                proxyManager.SetProxyOverride("*.local");
                CompareUrls();

                proxyManager.SetProxyOverride("http://*.local");
                CompareUrls();

                proxyManager.SetProxyOverride("<-loopback>;*.local");
                CompareUrls();

                proxyManager.SetProxyOverride("<-loopback>;*.local;<local>");
                CompareUrls();
            }
            finally
            {
                proxyManager.RestoreOriginalSettings();
            }
        }

        [TestMethod]
        public void SystemProxySettingsMergeExistingRulesAndProxyLoopback()
        {
            var settings = new SystemProxySettings
            {
                ProxyLoopback = true
            };
            settings.BypassRules.Add("*.example.com");
            settings.BypassRules.Add("<local>");

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("<-loopback>;*.internal;<local>;*.example.com", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsReplaceExistingRules()
        {
            var settings = new SystemProxySettings
            {
                BypassRuleMode = SystemProxyBypassRuleMode.Replace
            };
            settings.BypassRules.Add("*.example.com");

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("*.example.com", proxyOverride);
        }

        [TestMethod]
        public void DefaultSystemProxySettingsPreserveExistingRules()
        {
            var settings = new SystemProxySettings();

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("*.internal;<local>", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsPlacesLoopbackRuleLastWhenRequested()
        {
            var settings = new SystemProxySettings
            {
                ProxyLoopback = true,
                ProxyLoopbackPlacement = SystemProxyLoopbackPlacement.Last
            };
            settings.BypassRules.Add("*.example.com");

            var proxyOverride = settings.BuildProxyOverride(null);

            Assert.AreEqual("*.example.com;<-loopback>", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsValidateThrowsForMalformedRules()
        {
            var settings = new SystemProxySettings();
            settings.BypassRules.Add("*.example.com;*.other.com");

            Assert.ThrowsException<ArgumentException>(() => settings.Validate());
        }

        private void CompareUrls()
        {
            var webProxy = WebRequest.GetSystemWebProxy();

            var resolver = new WinHttpWebProxyFinder();
            resolver.LoadFromIe();

            CompareProxy(webProxy, resolver, "http://127.0.0.1");
            CompareProxy(webProxy, resolver, "https://127.0.0.1");
            CompareProxy(webProxy, resolver, "http://localhost");
            CompareProxy(webProxy, resolver, "https://localhost");

            string hostName = null;
            try
            {
                hostName = Dns.GetHostName();
            }
            catch
            {
            }

            if (hostName != null)
            {
                CompareProxy(webProxy, resolver, "http://" + hostName);
                CompareProxy(webProxy, resolver, "https://" + hostName);
            }

            CompareProxy(webProxy, resolver, "http://google.com");
            CompareProxy(webProxy, resolver, "https://google.com");
            CompareProxy(webProxy, resolver, "http://bing.com");
            CompareProxy(webProxy, resolver, "https://bing.com");
            CompareProxy(webProxy, resolver, "http://yahoo.com");
            CompareProxy(webProxy, resolver, "https://yahoo.com");
            CompareProxy(webProxy, resolver, "http://test.local");
            CompareProxy(webProxy, resolver, "https://test.local");
        }

        private void CompareProxy(IWebProxy webProxy, WinHttpWebProxyFinder resolver, string url)
        {
            var uri = new Uri(url);

            var expectedProxyUri = webProxy.GetProxy(uri);

            var proxy = resolver.GetProxy(uri);

            if (expectedProxyUri == uri)
            {
                // no proxy
                Assert.AreEqual(proxy, null);
                return;
            }

            Assert.AreEqual(expectedProxyUri.ToString(), $"http://{proxy.HostName}:{proxy.Port}/");
        }
    }
}ParseOptions.0.json√
]D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\WinAuthTests.csÃusing System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.WinAuth;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class WinAuthTests
    {
        [TestMethod]
        public void Test_Acquire_Client_Token()
        {
            var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com", "NTLM", new InternalDataStore());
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void Test_Acquire_Upstream_Proxy_Client_Token()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows SSPI is required.");

            var token =
                WinAuthHandler.GetInitialProxyAuthToken("proxy.example.com", "NTLM", new InternalDataStore());

            StringAssert.StartsWith(token, " ");
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ProxyAuth_ReusesConnection()
        {
            // 407 (upstream proxy auth) must reuse the same connection regardless of the WinAuth flag.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, false));
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ServerWinAuth401_ReusesConnection()
        {
            // 401 handled by NTLM/Negotiate is connection-oriented and must reuse the same connection.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_NonAuthReRequest_UsesFreshConnection()
        {
            // A user-initiated re-request (not an auth handshake) may target a different destination.
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(200, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(302, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, false));
        }
    }
}ParseOptions.0.json˝
êD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\obj\Release\net48\.NETFramework,Version=v4.8.AssemblyAttributes.cs“// <autogenerated />
using System;
using System.Reflection;
[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
ParseOptions.0.jsonÀ	
åD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\obj\Release\net48\Titanium.Web.Proxy.UnitTests.AssemblyInfo.cs§//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

using System;
using System.Reflection;

[assembly: System.Reflection.AssemblyCompanyAttribute("Titanium.Web.Proxy.UnitTests")]
[assembly: System.Reflection.AssemblyConfigurationAttribute("Release")]
[assembly: System.Reflection.AssemblyFileVersionAttribute("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0+d3f1bf609a3eb2e6e273820f305bb4f6cb5ddb25")]
[assembly: System.Reflection.AssemblyProductAttribute("Titanium.Web.Proxy.UnitTests")]
[assembly: System.Reflection.AssemblyTitleAttribute("Titanium.Web.Proxy.UnitTests")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]

// Generated by the MSBuild WriteCodeFragment class.

ParseOptions.0.json