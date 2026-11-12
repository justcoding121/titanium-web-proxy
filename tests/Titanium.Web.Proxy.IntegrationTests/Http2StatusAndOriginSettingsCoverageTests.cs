using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining Copy.Headers :status validation (RFC 7540 §8.1.2.4) and
///     BeforeRequest Ok() short-circuit on native H2.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2StatusAndOriginSettingsCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<Http2RawFrame.Frame> ReadSignificantFrameAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await raw.Connection.ReadFrameAsync().WaitAsync(timeout);
        } while (frame.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate);

        return frame;
    }

    /// <summary>
    ///     Origin bad :status is rejected on the relay path; the client sees RST
    ///     PROTOCOL_ERROR or a connection GOAWAY (RST may target the origin leg).
    /// </summary>
    private static async Task AssertOriginBadStatusRejectedAsync(string statusValue, string path)
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var bad = conn.EncodeHeaders([(":status", statusValue)], []);
            await conn.WriteHeaderBlockAsync(streamId, bad, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", path)],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Invalid :status '{statusValue}' must RST or GOAWAY; got {frame.Type}.");
        // RST to the client carries PROTOCOL_ERROR; GOAWAY may be NO_ERROR teardown after the
        // origin-leg RST (see Http2Helper.Copy.Headers status-digit enforcement).
        if (frame.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
        }
    }

    /// <summary>
    ///     RFC 7540 §8.1.2.4: :status must be exactly three ASCII digits.
    ///     Bug class: two-digit :status is accepted / hung instead of RST.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Origin_StatusNotThreeDigits_TriggersRstProtocolError() =>
        AssertOriginBadStatusRejectedAsync("20", "/short-status");

    /// <summary>
    ///     Bug class: non-digit :status (e.g. "2OK") is relayed as a garbled response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Origin_StatusNonDigit_TriggersRstProtocolError() =>
        AssertOriginBadStatusRejectedAsync("2OK", "/bad-status");

    /// <summary>
    ///     Bug class: four-digit :status is accepted instead of PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Origin_StatusFourDigits_TriggersProtocolError() =>
        AssertOriginBadStatusRejectedAsync("2000", "/long-status");

    /// <summary>
    ///     Bug class: BeforeRequest Ok() on native H2 still contacts the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_BeforeRequest_Ok_DoesNotContactOrigin()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                originContacted = true;
            }
            catch { /* torn down */ }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("synthetic-ok", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(originContacted, "Synthetic Ok must not contact the origin.");
    }
}
