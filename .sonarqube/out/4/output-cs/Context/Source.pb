ã-
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\CertificateManagerTests.csá,using System;
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
}ParseOptions.0.json•<
dD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ChunkedTrailerTests.cs—;using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="ChunkedTrailerHelper" />, the strict, size-bounded reader/writer shared by
///     every chunked-trailer read/write code path (<c>HttpStream.CopyBodyChunkedAsync</c>,
///     <c>HttpStream.HandleBodyWrite</c>, <c>LimitedStream</c>, <c>BodyStreamWriter</c>).
/// </summary>
[TestClass]
public class ChunkedTrailerTests
{
    private static HttpStream MakeReader(string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        return new HttpStream(new ProxyServer(), new MemoryStream(bytes), new DefaultBufferPool(),
            CancellationToken.None, false);
    }

    private static (HttpStream writer, MemoryStream destination) MakeWriter()
    {
        var destination = new MemoryStream();
        var writer = new HttpStream(new ProxyServer(), destination, new DefaultBufferPool(),
            CancellationToken.None, true);
        return (writer, destination);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_NoTrailers_ConsumesOnlyTheBlankLineAndLeavesCollectionEmpty()
    {
        // Terminating blank line with nothing after it - and something following in the stream, to prove
        // we stop exactly at the blank line rather than over-consuming.
        using var reader = MakeReader("\r\nGET / HTTP/1.1\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        Assert.IsFalse(trailers.GetEnumerator().MoveNext());

        var nextLine = await reader.ReadLineAsync();
        Assert.AreEqual("GET / HTTP/1.1", nextLine);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_SingleTrailer_IsParsedIntoCollection()
    {
        using var reader = MakeReader("X-Trailer: trailer-value\r\n\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        Assert.AreEqual("trailer-value", trailers.GetFirstHeader("X-Trailer")?.Value);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_MultipleTrailerLines_AreAllParsedAndRawLinesCapturedInOrder()
    {
        using var reader = MakeReader("X-First: one\r\nX-Second: two\r\nX-Third: three\r\n\r\n");
        var trailers = new HeaderCollection();
        var rawLines = new List<string>();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, rawLines);

        Assert.AreEqual("one", trailers.GetFirstHeader("X-First")?.Value);
        Assert.AreEqual("two", trailers.GetFirstHeader("X-Second")?.Value);
        Assert.AreEqual("three", trailers.GetFirstHeader("X-Third")?.Value);

        CollectionAssert.AreEqual(
            new[] { "X-First: one", "X-Second: two", "X-Third: three" }, rawLines);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_DuplicateHeaderName_KeepsBothAsNonUniqueHeader()
    {
        using var reader = MakeReader("X-Trailer: one\r\nX-Trailer: two\r\n\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        var values = trailers.GetHeaders("X-Trailer")!.Select(h => h.Value).ToArray();
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, values);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_MalformedLineWithoutColon_ThrowsProxyHttpException()
    {
        using var reader = MakeReader("this-is-not-a-valid-header-line\r\n\r\n");
        var trailers = new HeaderCollection();

        await Assert.ThrowsExceptionAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_TooManyLines_ThrowsProxyHttpException()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ChunkedTrailerHelper.MaxTrailerHeaderCount + 1; i++)
            sb.Append($"X-{i}: v\r\n");
        sb.Append("\r\n");

        using var reader = MakeReader(sb.ToString());
        var trailers = new HeaderCollection();

        await Assert.ThrowsExceptionAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_OversizedBlock_ThrowsProxyHttpException()
    {
        var hugeValue = new string('a', ChunkedTrailerHelper.MaxTrailerHeaderBlockSize + 1);
        using var reader = MakeReader($"X-Trailer: {hugeValue}\r\n\r\n");
        var trailers = new HeaderCollection();

        await Assert.ThrowsExceptionAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_NullCollection_WritesOnlyTheBlankTerminator()
    {
        var (writer, destination) = MakeWriter();

        await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, null);

        Assert.AreEqual("\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_WithHeaders_WritesEachLineThenBlankTerminator()
    {
        var (writer, destination) = MakeWriter();
        var trailers = new HeaderCollection();
        trailers.AddHeader("X-Checksum", "abc123");

        await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, trailers);

        Assert.AreEqual("X-Checksum: abc123\r\n\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_ForbiddenField_ThrowsProxyHttpExceptionAndDoesNotSilentlyDrop()
    {
        var (writer, _) = MakeWriter();
        var trailers = new HeaderCollection();
        trailers.AddHeader(KnownHeaders.ContentLength.String, "5");

        await Assert.ThrowsExceptionAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, trailers));
    }

    [TestMethod]
    public async Task WriteRawTrailingLinesAsync_PreservesExactLineTextAndOrder()
    {
        var (writer, destination) = MakeWriter();
        // Deliberately non-normalized spacing to prove raw lines are forwarded byte-for-byte rather than
        // re-serialized through a parsed HeaderCollection (which would trim/normalize the value).
        var rawLines = new List<string> { "X-Trailer:   spaced-value  ", "X-Other:v2" };

        await ChunkedTrailerHelper.WriteRawTrailingLinesAsync(writer, rawLines);

        Assert.AreEqual("X-Trailer:   spaced-value  \r\nX-Other:v2\r\n\r\n",
            Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteRawTrailingLinesAsync_NullList_WritesOnlyTheBlankTerminator()
    {
        var (writer, destination) = MakeWriter();

        await ChunkedTrailerHelper.WriteRawTrailingLinesAsync(writer, null);

        Assert.AreEqual("\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }
}
ParseOptions.0.json£4
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ConnectionCacheKeyTests.cs¡3using System.Collections.Generic;
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
ParseOptions.0.jsonß,
fD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\HeaderCollectionTests.csß+using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="HeaderCollection" />.
    ///     HeaderCollection underpins the <c>TrailingHeaders</c> property, so its core
    ///     unique/non-unique/add/remove/set semantics need a documented baseline.
    /// </summary>
    [TestClass]
    public class HeaderCollectionTests
    {
        [TestMethod]
        public void AddHeader_SameNameTwice_MovesBothValuesToNonUniqueCollection()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Test", "1");
            headers.AddHeader("X-Test", "2");

            Assert.IsFalse(headers.Headers.ContainsKey("X-Test"));
            Assert.IsTrue(headers.NonUniqueHeaders.ContainsKey("X-Test"));

            var all = headers.GetHeaders("X-Test");
            Assert.IsNotNull(all);
            Assert.AreEqual(2, all.Count);
            CollectionAssert.AreEquivalent(new[] { "1", "2" }, all.ConvertAll(h => h.Value));
        }

        [TestMethod]
        public void AddHeader_SingleName_StaysInUniqueCollection()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Test", "1");

            Assert.IsTrue(headers.Headers.ContainsKey("X-Test"));
            Assert.IsFalse(headers.NonUniqueHeaders.ContainsKey("X-Test"));
        }

        [TestMethod]
        public void GetFirstHeader_ForNonUniqueHeaders_ReturnsFirstAddedValue()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("Set-Cookie", "a=1");
            headers.AddHeader("Set-Cookie", "b=2");

            var first = headers.GetFirstHeader("Set-Cookie");

            Assert.IsNotNull(first);
            Assert.AreEqual("a=1", first.Value);
        }

        [TestMethod]
        public void GetHeaders_ForMissingName_ReturnsNull()
        {
            var headers = new HeaderCollection();

            Assert.IsNull(headers.GetHeaders("X-Missing"));
            Assert.IsNull(headers.GetFirstHeader("X-Missing"));
        }

        [TestMethod]
        public void RemoveHeader_ByName_RemovesFromEitherCollectionAndReportsWhetherItExisted()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Single", "1");
            headers.AddHeader("X-Multi", "a");
            headers.AddHeader("X-Multi", "b");

            Assert.IsTrue(headers.RemoveHeader("X-Single"));
            Assert.IsTrue(headers.RemoveHeader("X-Multi"));
            Assert.IsFalse(headers.HeaderExists("X-Single"));
            Assert.IsFalse(headers.HeaderExists("X-Multi"));
            Assert.IsFalse(headers.RemoveHeader("X-DoesNotExist"));
        }

        [TestMethod]
        public void HeaderExists_And_Lookups_AreCaseInsensitive()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("Content-Type", "text/plain");

            Assert.IsTrue(headers.HeaderExists("content-type"));
            Assert.IsTrue(headers.HeaderExists("CONTENT-TYPE"));
            Assert.IsNotNull(headers.GetFirstHeader("content-type"));
        }

        [TestMethod]
        public void Clear_RemovesAllUniqueAndNonUniqueHeaders()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("A", "1");
            headers.AddHeader("B", "1");
            headers.AddHeader("B", "2");

            headers.Clear();

            Assert.AreEqual(0, headers.GetAllHeaders().Count);
            Assert.IsFalse(headers.HeaderExists("A"));
            Assert.IsFalse(headers.HeaderExists("B"));
        }

        [TestMethod]
        public void SetOrAddHeaderValue_UpdatesExistingUniqueHeaderInPlaceRatherThanDuplicating()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ContentType, "text/plain");

            headers.SetOrAddHeaderValue(KnownHeaders.ContentType, "application/json");

            Assert.AreEqual("application/json", headers.GetHeaderValueOrNull(KnownHeaders.ContentType));
            Assert.AreEqual(1, headers.GetHeaders("Content-Type").Count);
        }

        [TestMethod]
        public void SetOrAddHeaderValue_NullValue_RemovesHeader()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ContentType, "text/plain");

            headers.SetOrAddHeaderValue(KnownHeaders.ContentType, (string)null);

            Assert.IsFalse(headers.HeaderExists("Content-Type"));
        }

        [TestMethod]
        public void FixProxyHeaders_MovesProxyConnectionValueOntoConnectionHeader()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ProxyConnection, "close");

            headers.FixProxyHeaders();

            Assert.IsFalse(headers.HeaderExists("Proxy-Connection"));
            Assert.AreEqual("close", headers.GetHeaderValueOrNull(KnownHeaders.Connection));
        }

        [TestMethod]
        public void FixProxyHeaders_WithoutProxyConnectionHeader_LeavesConnectionHeaderUntouched()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.Connection, "keep-alive");

            headers.FixProxyHeaders();

            Assert.AreEqual("keep-alive", headers.GetHeaderValueOrNull(KnownHeaders.Connection));
        }
    }
}
ParseOptions.0.jsonÊ
eD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\HpackRegressionTests.csËusing System;
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
ParseOptions.0.jsonõ'
gD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\Http2HpackEncoderTests.csô&// The Encoder type (like Http2Helper) only compiles for net6.0+ targets; this whole file is a no-op
// on net462/net48, matching that existing convention. It activates once the unit test project itself
// moves to net10.0 or is otherwise built against a net6.0+ target.
#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
// System.Text also defines abstract Encoder/Decoder types (for char<->byte transcoding); alias the HPACK
// ones explicitly so they win over those System.Text names brought in by the `using System.Text;` above.
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="Encoder" />, establishing two baselines:
    ///     1. The Encoder type itself supports dynamic-table reuse correctly when the *same instance* is used
    ///        across calls.
    ///     2. Two independent Encoder instances can never benefit from cross-instance indexing - this is an
    ///        inherent property of HPACK's per-connection-direction dynamic table, not a bug.
    ///     <c>Http2Helper.SendHeader</c> reuses one <c>Encoder</c> per connection direction (stored on the
    ///     shared <c>Http2Settings</c> instance) instead of constructing a fresh one on every call, so
    ///     production traffic benefits from baseline 1 across streams on the same HTTP/2 connection. See
    ///     <c>Http2Tests.Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests</c>
    ///     in the integration test suite for an end-to-end proof through the real relay.
    /// </summary>
    [TestClass]
    public class Http2HpackEncoderTests
    {
        [TestMethod]
        public void Encoder_ReusedInstance_IndexesRepeatedHeaderIntoDynamicTable()
        {
            var encoder = new Encoder(4096);
            var decoder = new Decoder(8192, 4096);
            var listener = new RecordingHeaderListener();

            var first = EncodeHeader(encoder, "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(encoder, "x-custom-header", "some-repeated-value");

            Assert.IsTrue(second.Length < first.Length,
                "A reused Encoder instance should emit a compact indexed reference for a header " +
                "it has already added to the dynamic table.");

            Decode(decoder, listener, first);
            Decode(decoder, listener, second);

            Assert.AreEqual(2, listener.Headers.Count);
            foreach (var (name, value) in listener.Headers)
            {
                Assert.AreEqual("x-custom-header", name);
                Assert.AreEqual("some-repeated-value", value);
            }
        }

        [TestMethod]
        public void Encoder_FreshInstancePerCall_NeverIndexesRepeatedHeader()
        {
            // Two independent Encoder instances have two independent (empty) dynamic tables, so neither can
            // ever emit an indexed reference into the other's table - this is inherent to HPACK, not something
            // any wiring change can affect. Http2Helper.SendHeader no longer constructs a fresh Encoder per
            // call; this test just pins the Encoder type's own behavior for the case where a caller
            // genuinely does use unrelated instances.
            var first = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");

            Assert.AreEqual(first.Length, second.Length,
                "Two independent Encoder instances/tables can never benefit from cross-instance indexing.");
        }

        private static byte[] EncodeHeader(Encoder encoder, string name, string value)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            return ms.ToArray();
        }

        private static void Decode(Decoder decoder, RecordingHeaderListener listener, byte[] encoded)
        {
            using var reader = new BinaryReader(new MemoryStream(encoded));
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();
        }

        private sealed class RecordingHeaderListener : IHeaderListener
        {
            internal List<(string, string)> Headers { get; } = new();

            public void AddHeader(Models.ByteString name, Models.ByteString value, bool sensitive)
            {
                Headers.Add((name.ToString(), value.ToString()));
            }
        }
    }
}
#endif
ParseOptions.0.json˜"
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\Http2HpackEvictionTests.cs–!#if NET6_0_OR_GREATER
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression coverage for HPACK dynamic-table eviction: a persistent encoder/decoder pair (as used per
///     connection direction by <c>Http2Helper</c>) must keep producing correctly-decodable output as the
///     dynamic table fills and entries get evicted, both for many distinct headers and for the
///     Kestrel-shaped repeated-header case that originally exposed the encoder's <c>COMPRESSION_ERROR</c>
///     bug (see the end-to-end coverage in <c>Titanium.Web.Proxy.IntegrationTests.Http2Tests</c>).
/// </summary>
[TestClass]
public class Http2HpackEvictionTests
{
    [TestMethod]
    public void Encoder_ManyDistinctHeaders_ForcingEviction_StillDecodesCorrectly()
    {
        var encoder = new Encoder(4096);
        var decoder = new Decoder(8192, 4096);
        var listener = new RecordingHeaderListener();

        for (var i = 0; i < 200; i++)
        {
            var name = $"x-header-{i % 7}";
            var value = $"value-{i}-" + new string('v', 60);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            var encoded = ms.ToArray();

            using var reader = new BinaryReader(new MemoryStream(encoded));
            listener.Headers.Clear();
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();

            Assert.AreEqual(1, listener.Headers.Count, $"iteration {i}: expected exactly one decoded header");
            Assert.AreEqual(name, listener.Headers[0].Item1, $"iteration {i}: name mismatch");
            Assert.AreEqual(value, listener.Headers[0].Item2, $"iteration {i}: value mismatch");
        }
    }

    [TestMethod]
    public void Encoder_KestrelLikeResponseHeaders_RepeatedAcrossManyResponses_StillDecodesCorrectly()
    {
        var encoder = new Encoder(4096);
        var decoder = new Decoder(8192, 4096);
        const string repeatedValue =
            "a-fairly-long-repeated-header-value-used-to-exercise-http2-hpack-dynamic-table-reuse-across-requests";

        for (var i = 0; i < 10; i++)
        {
            var headers = new (string name, string value)[]
            {
                (":status", "200"),
                ("date", $"Wed, 22 Jul 2026 18:{i:D2}:00 GMT"),
                ("content-type", "text/plain"),
                ("server", "Kestrel"),
                ("x-custom-repeated", repeatedValue),
            };

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            foreach (var (name, value) in headers)
                encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            var encoded = ms.ToArray();

            var listener = new RecordingHeaderListener();
            using var reader = new BinaryReader(new MemoryStream(encoded));
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();

            Assert.AreEqual(headers.Length, listener.Headers.Count, $"iteration {i}: header count mismatch");
            for (var h = 0; h < headers.Length; h++)
            {
                Assert.AreEqual(headers[h].name, listener.Headers[h].Item1, $"iteration {i}, header {h}: name mismatch");
                Assert.AreEqual(headers[h].value, listener.Headers[h].Item2, $"iteration {i}, header {h}: value mismatch");
            }
        }
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        internal System.Collections.Generic.List<(string, string)> Headers { get; } = new();

        public void AddHeader(Models.ByteString name, Models.ByteString value, bool sensitive)
        {
            Headers.Add((name.ToString(), value.ToString()));
        }
    }
}
#endif
ParseOptions.0.jsonÿ
mD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\HttpModelAndProxySocketTests.csøusing System;
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
ParseOptions.0.json¶
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\Properties\AssemblyInfo.cs´using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyDescription("")]
[assembly: AssemblyCopyright("Copyright Â© Titanium 2015-2019")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("b517e3d0-d03b-436f-ab03-34ba0d5321af")]ParseOptions.0.json¼
aD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ProxyServerTests.csÁusing System;
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
}ParseOptions.0.jsonß)
iD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\RequestResponseBaseTests.csÜ(using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="RequestResponseBase" /> (via <see cref="Response" />):
    ///     body assignment, chunked/content-length bookkeeping, and the compression helper used when relaying a
    ///     buffered, modified body back onto the wire.
    /// </summary>
    [TestClass]
    public class RequestResponseBaseTests
    {
        [TestMethod]
        public void SettingBody_NonChunked_UpdatesContentLengthHeaderToMatchByteLength()
        {
            var response = new Response();

            response.Body = Encoding.ASCII.GetBytes("hello world");

            Assert.AreEqual(11, response.ContentLength);
            Assert.AreEqual("11", response.Headers.GetHeaderValueOrNull(KnownHeaders.ContentLength));
        }

        [TestMethod]
        public void IsChunked_True_ClearsContentLengthAndAddsTransferEncodingHeader()
        {
            var response = new Response();

            response.IsChunked = true;

            Assert.AreEqual(-1, response.ContentLength);
            Assert.IsTrue(response.Headers.HeaderExists("Transfer-Encoding"));

            response.IsChunked = false;

            Assert.IsFalse(response.Headers.HeaderExists("Transfer-Encoding"));
        }

        [TestMethod]
        public void ContentLength_SetToNegative_RemovesContentLengthHeader()
        {
            var response = new Response();
            response.Body = Encoding.ASCII.GetBytes("hello");
            Assert.IsTrue(response.Headers.HeaderExists("Content-Length"));

            response.ContentLength = -1;

            Assert.IsFalse(response.Headers.HeaderExists("Content-Length"));
        }

        [TestMethod]
        public void BodyString_DecodesBodyUsingContentTypeEncoding()
        {
            var response = new Response(Encoding.UTF8.GetBytes("hÃ©llo"))
            {
                ContentType = "text/plain; charset=utf-8"
            };

            Assert.AreEqual("hÃ©llo", response.BodyString);
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_Gzip_ProducesDecodableOutputAndUpdatesContentLength()
        {
            var original = Encoding.ASCII.GetBytes(
                "the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog.");
            var response = new Response(original);
            response.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");

            var compressed = response.CompressBodyAndUpdateContentLength();

            Assert.IsNotNull(compressed);
            Assert.AreEqual(compressed.Length, response.ContentLength);

            using (var compressedStream = new MemoryStream(compressed))
            using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
            using (var decompressed = new MemoryStream())
            {
                gzip.CopyTo(decompressed);
                CollectionAssert.AreEqual(original, decompressed.ToArray());
            }
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_Chunked_SetsContentLengthToUnknown()
        {
            var original = Encoding.ASCII.GetBytes("streamed body content");
            var response = new Response(original) { IsChunked = true };
            response.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");

            response.CompressBodyAndUpdateContentLength();

            Assert.AreEqual(-1, response.ContentLength);
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_NoBodyAndNotRead_ReturnsNull()
        {
            var response = new Response();

            var result = response.CompressBodyAndUpdateContentLength();

            Assert.IsNull(result);
        }

        [TestMethod]
        public void TrailingHeaders_DefaultsToEmptyAndHasTrailingHeadersReflectsContent()
        {
            var response = new Response();

            // HasTrailingHeaders must not force the lazy allocation that the public getter performs.
            Assert.IsFalse(response.HasTrailingHeaders);

            Assert.IsNotNull(response.TrailingHeaders);
            Assert.IsFalse(response.TrailingHeaders.GetEnumerator().MoveNext());
            Assert.IsFalse(response.HasTrailingHeaders, "An empty collection was allocated but nothing was added.");

            response.TrailingHeaders.AddHeader("X-Checksum", "abc123");

            Assert.IsTrue(response.HasTrailingHeaders);
            Assert.AreEqual("abc123", response.TrailingHeaders.GetFirstHeader("X-Checksum")?.Value);
        }

        [TestMethod]
        public void TrailingHeaders_SameInstanceReturnedOnRepeatedAccess()
        {
            var request = new Request();

            var first = request.TrailingHeaders;
            var second = request.TrailingHeaders;

            Assert.AreSame(first, second);
        }
    }
}
ParseOptions.0.json„
gD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\ResponseKeepAliveTests.csƒusing System;
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
ParseOptions.0.json¿
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
ParseOptions.0.jsonÒ?
`D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\SystemProxyTest.csØ>using System;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    // SystemProxyManager and WinHttpWebProxyFinder are both [SupportedOSPlatform("windows")]; this whole
    // test class exercises Windows-only system-proxy-registration APIs (matching CI, which runs on
    // windows-latest), so it is annotated the same way to satisfy the platform-compatibility analyzer.
    [SupportedOSPlatform("windows")]
    [TestClass]
    public class SystemProxyTest
    {
        // This used to cross-check WinHttpWebProxyFinder against WebRequest.GetSystemWebProxy() for every
        // change made below. That approach turned out to be fundamentally unreliable on modern .NET:
        //   1. WebRequest.GetSystemWebProxy() returns an IWebProxy (System.Net.Http.HttpWindowsProxy) that
        //      is cached for the lifetime of the process; repeated calls within the same process do not
        //      reliably observe rapid, successive registry + InternetSetOption changes the way the old
        //      .NET Framework WinInet-backed implementation did (verified empirically - a second call
        //      after changing ProxyServer in the registry, even with a following InternetSetOption
        //      refresh, kept returning the first-observed value).
        //   2. HttpWindowsProxy also has hardcoded bypass behavior for loopback (and, seemingly, the
        //      local machine's own hostname) that is independent of the configured bypass list - see
        //      https://github.com/dotnet/runtime's HttpWindowsProxy.GetMultiProxy ("This is optimization
        //      for loopback addresses.").
        // Neither of those is a bug in Titanium: WinHttpWebProxyFinder intentionally reads the live
        // WinINet registry configuration on every LoadFromIe() call and applies only the bypass rules
        // that are actually configured (via System.Net.WebProxy.IsBypassed, which - on modern .NET - does
        // not hardcode a loopback exception the way HttpWindowsProxy does). So this test now asserts
        // WinHttpWebProxyFinder's own resolution directly against the settings SystemProxyManager just
        // wrote, instead of cross-checking against .NET's own (differently-behaved) system proxy resolver.
        [TestMethod]
        public void WinHttpWebProxyFinderResolvesConfiguredProxyAndBypassRules()
        {
            var proxyManager = new SystemProxyManager();

            try
            {
                proxyManager.DisableAllProxy();
                AssertNoProxy("http://google.com");
                AssertNoProxy("https://google.com");

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Http);
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertNoProxy("https://google.com");

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Https);
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertProxy("https://google.com", "127.0.0.1", 8000);

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.AllHttp);
                AssertProxy("http://bing.com", "127.0.0.1", 8000);
                AssertProxy("https://bing.com", "127.0.0.1", 8000);

                // A bare hostname bypass rule only matches that exact host; unrelated hosts still proxy.
                proxyManager.SetProxyOverride("yahoo.com");
                AssertNoProxy("http://yahoo.com");
                AssertNoProxy("https://yahoo.com");
                AssertProxy("http://google.com", "127.0.0.1", 8000);

                // A wildcard rule matches the whole subdomain but not unrelated hosts.
                proxyManager.SetProxyOverride("*.local");
                AssertNoProxy("http://test.local");
                AssertNoProxy("https://test.local");
                AssertProxy("http://google.com", "127.0.0.1", 8000);

                // <local> bypasses simple (no-dot) hostnames but not dotted ones.
                proxyManager.SetProxyOverride("<local>");
                AssertNoProxy("http://simplehostname");
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertProxy("http://test.local", "127.0.0.1", 8000);

                // Combining rules with ';' still leaves unrelated hosts proxied.
                proxyManager.SetProxyOverride("*.local;<local>");
                AssertNoProxy("http://test.local");
                AssertNoProxy("http://simplehostname");
                AssertProxy("http://google.com", "127.0.0.1", 8000);
            }
            finally
            {
                proxyManager.RestoreOriginalSettings();
            }
        }

        private static void AssertProxy(string url, string expectedHost, int expectedPort)
        {
            using var resolver = new WinHttpWebProxyFinder();
            resolver.LoadFromIe();

            var proxy = resolver.GetProxy(new Uri(url));

            Assert.IsNotNull(proxy, $"Expected a proxy to be resolved for '{url}' but got none.");
            Assert.AreEqual(expectedHost, proxy!.HostName);
            Assert.AreEqual(expectedPort, proxy.Port);
        }

        private static void AssertNoProxy(string url)
        {
            using var resolver = new WinHttpWebProxyFinder();
            resolver.LoadFromIe();

            var proxy = resolver.GetProxy(new Uri(url));

            Assert.IsNull(proxy,
                $"Expected no proxy to be resolved for '{url}' but got {proxy?.HostName}:{proxy?.Port}.");
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
    }
}ParseOptions.0.jsonÃ
]D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\WinAuthTests.csÌusing System;
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
}ParseOptions.0.jsonã
rC:\Users\runneradmin\.nuget\packages\microsoft.net.test.sdk\17.14.1\build\net8.0\Microsoft.NET.Test.Sdk.Program.cs×// <auto-generated> This file has been auto generated. </auto-generated>
using System;
[Microsoft.VisualStudio.TestPlatform.TestSDKAutoGeneratedCode]
class AutoGeneratedProgram {static void Main(string[] args){}}ParseOptions.0.jsonô
‘D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\obj\Release\net10.0\.NETCoreApp,Version=v10.0.AssemblyAttributes.csÈ// <autogenerated />
using System;
using System.Reflection;
[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]
ParseOptions.0.jsonÍ	
ŽD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.UnitTests\obj\Release\net10.0\Titanium.Web.Proxy.UnitTests.AssemblyInfo.cs¤//------------------------------------------------------------------------------
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
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0+c2a21211b4a7a84a0ed9d585154ffb3535e0a2a7")]
[assembly: System.Reflection.AssemblyProductAttribute("Titanium.Web.Proxy.UnitTests")]
[assembly: System.Reflection.AssemblyTitleAttribute("Titanium.Web.Proxy.UnitTests")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]

// Generated by the MSBuild WriteCodeFragment class.

ParseOptions.0.json