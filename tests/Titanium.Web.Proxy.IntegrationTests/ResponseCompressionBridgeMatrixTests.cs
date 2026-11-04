using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Titanium.Web.Proxy.IntegrationTests.ProtocolMatrixTests;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     P4.0 matrix scaffold: clients (H1/H2/H3) x origins (H1/H2/H3) x modes (buffered/streaming).
///     H1-origin rows are covered by <see cref="ResponseCompressionIntegrationTests" /> and
///     <see cref="StreamingResponseCompressionTests" />; this class names the remaining rows.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ResponseCompressionBridgeMatrixTests
{
    public enum CompressionMode
    {
        Buffered,
        Streaming
    }

    public enum OriginKind
    {
        H1,
        H2,
        H3
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext, OriginKind.H2, CompressionMode.Buffered)]
    [DataRow(ClientKind.H1Cleartext, OriginKind.H2, CompressionMode.Streaming)]
    [DataRow(ClientKind.H2Tls, OriginKind.H2, CompressionMode.Buffered)]
    [DataRow(ClientKind.H2Tls, OriginKind.H2, CompressionMode.Streaming)]
    public void Matrix_H2Origin_SkipUntilBridgeFixture(ClientKind _, OriginKind origin, CompressionMode __)
    {
        Assert.AreEqual(OriginKind.H2, origin);
        Assert.Inconclusive("P4.1/P4.2: integration matrix needs a dedicated HTTP/2 origin fixture");
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext, OriginKind.H3, CompressionMode.Buffered)]
    [DataRow(ClientKind.H2Tls, OriginKind.H3, CompressionMode.Streaming)]
    public void Matrix_H3Origin_SkipUntilP43(ClientKind _, OriginKind origin, CompressionMode __)
    {
        Assert.AreEqual(OriginKind.H3, origin);
        Assert.Inconclusive("P4.3: HTTP/3 origin compression matrix row");
    }

    [TestMethod]
    public void Matrix_H3Client_SkipUntilP43()
    {
        Assert.Inconclusive("P4.3: HTTP/3 client compression matrix row");
    }
}
