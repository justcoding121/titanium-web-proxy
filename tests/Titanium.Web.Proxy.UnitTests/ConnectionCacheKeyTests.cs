using System.Collections.Generic;
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

        // --- ForwardHost / connectHost tests (Bug 1 fix) ---

        /// <summary>
        /// A fixed-forward endpoint changes the actual TCP destination while keeping the original
        /// host for TLS. The cache key must include connectHost so that connections forwarded to
        /// different back-ends are never mistakenly pooled together.
        /// </summary>
        [TestMethod]
        public void CacheKey_WithConnectHost_DiffersFromWithout()
        {
            var factory = CreateFactory();
            try
            {
                var direct    = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null);
                var forwarded = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward.example.com", connectPort: 443);

                Assert.AreNotEqual(direct, forwarded,
                    "A key with connectHost must differ from a key without one.");
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_SameConnectHost_ProducesSameKey()
        {
            var factory = CreateFactory();
            try
            {
                var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward.example.com", connectPort: 443);
                var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward.example.com", connectPort: 443);

                Assert.AreEqual(key1, key2, "Identical connectHost/Port must produce identical keys.");
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_DifferentConnectHosts_ProduceDifferentKeys()
        {
            var factory = CreateFactory();
            try
            {
                var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward1.example.com", connectPort: 443);
                var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward2.example.com", connectPort: 443);

                Assert.AreNotEqual(key1, key2, "Different connectHost values must produce different keys.");
            }
            finally
            {
                factory.Dispose();
            }
        }

        [TestMethod]
        public void CacheKey_DifferentConnectPorts_ProduceDifferentKeys()
        {
            var factory = CreateFactory();
            try
            {
                var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward.example.com", connectPort: 8443);
                var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                    connectHost: "forward.example.com", connectPort: 9443);

                Assert.AreNotEqual(key1, key2, "Different connectPort values must produce different keys.");
            }
            finally
            {
                factory.Dispose();
            }
        }
    }
}
