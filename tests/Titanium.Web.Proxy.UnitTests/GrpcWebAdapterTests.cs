using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Grpc;
using Titanium.Web.Proxy.Http;

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
}
