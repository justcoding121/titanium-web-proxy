using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class GrpcHealthProbeTests
{
    [TestMethod]
    public void EncodeRequest_EmptyService_IsZeroLengthMessage()
    {
        var frame = GrpcHealthProbe.EncodeRequest(null);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0 }, frame);
    }

    [TestMethod]
    public void EncodeRequest_NonEmptyService_WrapsProtobufFieldOne()
    {
        var frame = GrpcHealthProbe.EncodeRequest("my.service");
        var message = GrpcHealthProbe.EncodeHealthCheckRequestMessage("my.service");
        CollectionAssert.AreEqual(GrpcHealthProbe.WrapGrpcFrame(message), frame);
        Assert.AreEqual(0x0A, message[0]);
    }

    [TestMethod]
    public void EncodeRequest_255ByteService_FitsOneByteLength()
    {
        var service = new string('s', 255);
        var message = GrpcHealthProbe.EncodeHealthCheckRequestMessage(service);
        Assert.AreEqual(258, message.Length);
        Assert.AreEqual(0x0A, message[0]);
        Assert.AreEqual(255, message[1]);
        Assert.AreEqual(0x01, message[2]);
    }

    [TestMethod]
    public void TryParseResponse_ServingWithTrailerZero_IsHealthy()
    {
        var body = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServing);
        Assert.IsTrue(GrpcHealthProbe.TryParseResponse(body, 0, out var serving));
        Assert.IsTrue(serving);
    }

    [TestMethod]
    public void TryParseResponse_NotServing_IsFailure()
    {
        var body = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusNotServing);
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(body, 0, out _));
    }

    [TestMethod]
    public void TryParseResponse_ServiceUnknown_IsFailure()
    {
        var body = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServiceUnknown);
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(body, 0, out _));
    }

    [TestMethod]
    public void TryParseResponse_MissingTrailer_IsFailure()
    {
        var body = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServing);
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(body, null, out _));
    }

    [TestMethod]
    public void TryParseResponse_NonZeroTrailer_IsFailure()
    {
        var body = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServing);
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(body, 13, out _));
    }

    [TestMethod]
    public void TryParseResponse_CompressedFlag_IsFailure()
    {
        var inner = GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServing);
        inner[0] = 1;
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(inner, 0, out _));
    }

    [TestMethod]
    public void TryParseResponse_TruncatedLength_IsFailure()
    {
        var body = new byte[] { 0, 0, 0, 0, 10, 0x08, 0x01 };
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(body, 0, out _));
    }
}
