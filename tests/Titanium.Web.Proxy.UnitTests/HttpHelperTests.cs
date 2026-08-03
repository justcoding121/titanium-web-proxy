using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class HttpHelperTests
{
    [TestMethod]
    public void GetEncodingFromContentType_Null_ReturnsDefault()
    {
        Assert.AreEqual(HttpHeader.DefaultEncoding, HttpHelper.GetEncodingFromContentType(null));
    }

    [TestMethod]
    public void GetEncodingFromContentType_CharsetUtf8_Quoted()
    {
        var encoding = HttpHelper.GetEncodingFromContentType("text/html; charset=\"utf-8\"");
        Assert.AreEqual(Encoding.UTF8.WebName, encoding.WebName);
    }

    [TestMethod]
    public void GetEncodingFromContentType_XUserDefined_FallsBackToDefault()
    {
        Assert.AreEqual(HttpHeader.DefaultEncoding,
            HttpHelper.GetEncodingFromContentType("text/plain; charset=x-user-defined"));
    }

    [TestMethod]
    public void GetEncodingFromContentType_InvalidCharset_FallsBackToDefault()
    {
        Assert.AreEqual(HttpHeader.DefaultEncoding,
            HttpHelper.GetEncodingFromContentType("text/plain; charset=not-a-real-encoding-zzz"));
    }

    [TestMethod]
    public void GetBoundaryFromContentType_QuotedAndUnquoted()
    {
        var quoted = HttpHelper.GetBoundaryFromContentType("multipart/form-data; boundary=\"----abc\"");
        Assert.AreEqual("----abc", quoted.ToString());

        var bare = HttpHelper.GetBoundaryFromContentType("multipart/form-data; boundary=xyz");
        Assert.AreEqual("xyz", bare.ToString());

        Assert.IsTrue(HttpHelper.GetBoundaryFromContentType(null).IsEmpty);
    }

    [TestMethod]
    public void GetWildCardDomainName_Rules()
    {
        Assert.AreEqual("127.0.0.1", HttpHelper.GetWildCardDomainName("127.0.0.1", false));
        Assert.AreEqual("example.com", HttpHelper.GetWildCardDomainName("example.com", false));
        Assert.AreEqual("*.google.com", HttpHelper.GetWildCardDomainName("www.google.com", false));
        Assert.AreEqual("pay.vn.ua", HttpHelper.GetWildCardDomainName("pay.vn.ua", false));
        Assert.AreEqual("foo-bar.example.com", HttpHelper.GetWildCardDomainName("foo-bar.example.com", false));
        Assert.AreEqual("www.google.com", HttpHelper.GetWildCardDomainName("www.google.com", true));
    }

    [TestMethod]
    public async Task GetMethod_RecognizesCommonVerbs()
    {
        Assert.AreEqual(KnownMethod.Get, await PeekMethod("GET / HTTP/1.1\r\n"));
        Assert.AreEqual(KnownMethod.Post, await PeekMethod("POST / HTTP/1.1\r\n"));
        Assert.AreEqual(KnownMethod.Connect, await PeekMethod("CONNECT host:443 HTTP/1.1\r\n"));
        Assert.AreEqual(KnownMethod.Pri, await PeekMethod("PRI * HTTP/2.0\r\n"));
        Assert.AreEqual(KnownMethod.Invalid, await PeekMethod("!!\r\n"));
    }

    private static async Task<KnownMethod> PeekMethod(string preface)
    {
        var pool = new ArrayPoolBufferPool();
        var reader = new PeekStream(Encoding.ASCII.GetBytes(preface));
        return await HttpHelper.GetMethod(reader, pool);
    }

    private sealed class ArrayPoolBufferPool : IBufferPool
    {
        public int BufferSize => 8192;
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }

    private sealed class PeekStream : IPeekStream
    {
        private readonly byte[] data;
        public PeekStream(byte[] data) => this.data = data;

        public ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
            CancellationToken cancellationToken = default)
        {
            if (index >= data.Length) return ValueTask.FromResult(0);
            var n = Math.Min(count, data.Length - index);
            Buffer.BlockCopy(data, index, buffer, offset, n);
            return ValueTask.FromResult(n);
        }

        public ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(index < data.Length ? data[index] : -1);

        public byte PeekByteFromBuffer(int index) => data[index];
    }
}
