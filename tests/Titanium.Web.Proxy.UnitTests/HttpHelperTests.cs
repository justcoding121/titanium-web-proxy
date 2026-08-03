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
    public void ContentTypeHelpers_MissingOrEmptyParameters_ReturnDefaults()
    {
        Assert.AreEqual(HttpHeader.DefaultEncoding,
            HttpHelper.GetEncodingFromContentType("text/plain; format=flowed"));
        Assert.AreEqual(HttpHeader.DefaultEncoding,
            HttpHelper.GetEncodingFromContentType("text/plain; charset="));
        Assert.IsTrue(HttpHelper.GetBoundaryFromContentType("multipart/form-data; name=value").IsEmpty);
        Assert.IsTrue(HttpHelper.GetBoundaryFromContentType("multipart/form-data; boundary=").IsEmpty);
    }

    [TestMethod]
    public void ContentTypeHelpers_HandleTrailingSeparatorsAndUnquotedCharset()
    {
        var encoding = HttpHelper.GetEncodingFromContentType("text/plain;; charset=us-ascii;");
        Assert.AreEqual(Encoding.ASCII.WebName, encoding.WebName);

        var boundary = HttpHelper.GetBoundaryFromContentType("multipart/form-data;; boundary=abc;");
        Assert.AreEqual("abc", boundary.ToString());
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

    [DataTestMethod]
    [DataRow("PUT /", (int)KnownMethod.Put)]
    [DataRow("HEAD /", (int)KnownMethod.Head)]
    [DataRow("TRACE /", (int)KnownMethod.Trace)]
    [DataRow("DELETE /", (int)KnownMethod.Delete)]
    [DataRow("OPTIONS /", (int)KnownMethod.Options)]
    [DataRow("PATCH /", (int)KnownMethod.Unknown)]
    [DataRow("GOT /", (int)KnownMethod.Unknown)]
    [DataRow("PRY /", (int)KnownMethod.Unknown)]
    public async Task GetMethod_CoversKnownAndUnknownAlphabeticVerbs(string request, int expected)
    {
        Assert.AreEqual((KnownMethod)expected, await PeekMethod(request));
    }

    [DataTestMethod]
    [DataRow("GE /")]
    [DataRow("GET\t/")]
    [DataRow("GET1 /")]
    [DataRow("ABCDEFGHIJKLMNOPQRST")]
    public async Task GetMethod_RejectsMalformedOrUnterminatedMethods(string request)
    {
        Assert.AreEqual(KnownMethod.Invalid, await PeekMethod(request));
    }

    [TestMethod]
    public async Task GetMethod_BufferTooSmall_ThrowsArgumentException()
    {
        var pool = new FixedBufferPool(19);
        var reader = new PeekStream(Encoding.ASCII.GetBytes("GET /"));

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await HttpHelper.GetMethod(reader, pool));

        StringAssert.Contains(exception.Message, "Minimum size is 20");
    }

    [TestMethod]
    public async Task GetMethod_PartialPeekReads_AreAccumulated()
    {
        var pool = new ArrayPoolBufferPool();
        var reader = new PeekStream(Encoding.ASCII.GetBytes("DELETE /"), maxBytesPerPeek: 1);

        Assert.AreEqual(KnownMethod.Delete, await HttpHelper.GetMethod(reader, pool));
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

    private sealed class FixedBufferPool : IBufferPool
    {
        public FixedBufferPool(int bufferSize) => BufferSize = bufferSize;
        public int BufferSize { get; }
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }

    private sealed class PeekStream : IPeekStream
    {
        private readonly byte[] data;
        private readonly int maxBytesPerPeek;

        public PeekStream(byte[] data, int maxBytesPerPeek = int.MaxValue)
        {
            this.data = data;
            this.maxBytesPerPeek = maxBytesPerPeek;
        }

        public ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
            CancellationToken cancellationToken = default)
        {
            if (index >= data.Length) return ValueTask.FromResult(0);
            var n = Math.Min(Math.Min(count, data.Length - index), maxBytesPerPeek);
            Buffer.BlockCopy(data, index, buffer, offset, n);
            return ValueTask.FromResult(n);
        }

        public ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(index < data.Length ? data[index] : -1);

        public byte PeekByteFromBuffer(int index) => data[index];
    }
}
