using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Grpc;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class GrpcWebAdapterTests
{
    [TestMethod]
    public void EncodeAndReadFrame_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var framed = GrpcWebAdapter.EncodeFrame(payload);
        Assert.IsTrue(GrpcWebAdapter.TryReadFrame(framed, out var compressed, out var read, out var consumed));
        Assert.IsFalse(compressed);
        Assert.AreEqual(5 + payload.Length, consumed);
        CollectionAssert.AreEqual(payload, read.ToArray());
    }

    [TestMethod]
    public void IsGrpcWeb_DetectsContentType()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ContentType, "application/grpc-web+proto");
        Assert.IsTrue(GrpcWebAdapter.IsGrpcWeb(headers));
        Assert.IsFalse(GrpcWebAdapter.IsGrpcWebText(headers));
    }

    [DataTestMethod]
    [DataRow("application/grpc-web", true)]
    [DataRow("application/grpc-web+proto", true)]
    [DataRow("Application/GRPC-Web+Proto", true)]
    [DataRow("application/grpc-web+proto; charset=utf-8", true)]
    [DataRow("application/grpc-web-text", false)]
    [DataRow("application/grpc-web-text+proto", false)]
    [DataRow("application/grpc-web+json", false)]
    [DataRow("application/grpc", false)]
    [DataRow("application/json", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsGrpcWebBinary_AcceptsOnlyTheBinaryTypes(string? contentType, bool expected)
    {
        Assert.AreEqual(expected, GrpcWebAdapter.IsGrpcWebBinary(contentType));
    }

    [DataTestMethod]
    [DataRow("application/grpc", true)]
    [DataRow("application/grpc+proto", true)]
    [DataRow("application/grpc; charset=utf-8", true)]
    [DataRow("application/grpc-web", false)]
    [DataRow("application/grpc-web+proto", false)]
    [DataRow("text/html", false)]
    [DataRow(null, false)]
    public void IsNativeGrpc_ExcludesGrpcWeb(string? contentType, bool expected)
    {
        Assert.AreEqual(expected, GrpcWebAdapter.IsNativeGrpc(contentType));
    }

    [TestMethod]
    public void EncodeFrame_WithFlags_SetsTheFlagByte()
    {
        var framed = GrpcWebAdapter.EncodeFrame(new byte[] { 9 }, GrpcWebAdapter.TrailerFrameFlag);

        CollectionAssert.AreEqual(new byte[] { 0x80, 0, 0, 0, 1, 9 }, framed);
    }

    [TestMethod]
    public void EncodeTrailerFrame_WritesLowercaseHeaderLinesWithTheTrailerFlag()
    {
        var trailers = new HeaderCollection();
        trailers.AddHeader("Grpc-Status", "0");
        trailers.AddHeader("grpc-message", "fine");
        trailers.AddHeader(":pseudo", "skipped");

        var framed = GrpcWebAdapter.EncodeTrailerFrame(trailers);

        Assert.AreEqual(0x80, framed[0]);
        Assert.IsTrue(GrpcWebAdapter.TryReadFrame(framed, out _, out var payload, out var consumed));
        Assert.AreEqual(framed.Length, consumed);
        Assert.AreEqual("grpc-status: 0\r\ngrpc-message: fine\r\n", Encoding.UTF8.GetString(payload));
    }

    [TestMethod]
    public void EncodeTrailerFrame_WithNoTrailers_IsAnEmptyTrailerFrame()
    {
        var framed = GrpcWebAdapter.EncodeTrailerFrame(new List<HttpHeader>());

        CollectionAssert.AreEqual(new byte[] { 0x80, 0, 0, 0, 0 }, framed);
    }

    [TestMethod]
    public void PromoteTrailersToHeaders_CopiesOnlyGrpcFields()
    {
        var trailers = new HeaderCollection();
        trailers.AddHeader("grpc-status", "5");
        trailers.AddHeader("grpc-message", "missing");
        trailers.AddHeader("x-other", "no");
        var headers = new HeaderCollection();

        GrpcWebAdapter.PromoteTrailersToHeaders(trailers, headers);

        Assert.AreEqual("5", headers.GetFirstHeader("grpc-status")?.Value);
        Assert.AreEqual("missing", headers.GetFirstHeader("grpc-message")?.Value);
        Assert.IsNull(headers.GetFirstHeader("x-other"));
    }
}
