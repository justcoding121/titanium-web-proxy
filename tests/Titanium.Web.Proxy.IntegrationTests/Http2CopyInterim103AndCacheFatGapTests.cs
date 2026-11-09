using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: interim 103 Early Hints,
///     cache-control on final responses, and chained 100→103→200.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyInterim103AndCacheFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: interim 103 Early Hints are dropped and the final 200 never arrives.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Interim103_Then200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var early = conn.EncodeHeaders(
                    [(":status", "103"), ("link", "</style.css>; rel=preload")], []);
                await conn.WriteHeaderBlockAsync(streamId, early, endStream: false);
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", "2")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/103")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var statuses = new System.Collections.Generic.List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && (statuses.Count == 0 || statuses[^1] is not "200"))
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var status = raw.Connection.DecodeHeaders(frame.Payload)
                .FirstOrDefault(h => h.Name == ":status").Value;
            if (string.IsNullOrEmpty(status))
                continue;
            if (statuses.Count == 0 || statuses[^1] != status)
                statuses.Add(status);
        }

        CollectionAssert.Contains(statuses, "103");
        CollectionAssert.Contains(statuses, "200");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_CacheControl_On200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [(":status", "200"),
                     ("cache-control", "max-age=120"),
                     ("content-length", "1")],
                    []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, [(byte)'a']);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/cc")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var cc = raw.Connection.DecodeHeaders(frame.Payload)
                .FirstOrDefault(h => h.Name == "cache-control").Value;
            if (string.IsNullOrEmpty(cc))
                continue;
            Assert.AreEqual("max-age=120", cc);
            return;
        }

        Assert.Fail("No cache-control in final response.");
    }

    /// <summary>
    ///     Bug class: multiple interim 1xx before the final response are collapsed or skipped.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Interim100Then103_Then200_AllReachClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var c100 = conn.EncodeHeaders([(":status", "100")], []);
                await conn.WriteHeaderBlockAsync(streamId, c100, endStream: false);
                var c103 = conn.EncodeHeaders([(":status", "103")], []);
                await conn.WriteHeaderBlockAsync(streamId, c103, endStream: false);
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", "0")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/chain")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var statuses = new System.Collections.Generic.List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && (statuses.Count == 0 || statuses[^1] is not "200"))
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var status = raw.Connection.DecodeHeaders(frame.Payload)
                .FirstOrDefault(h => h.Name == ":status").Value;
            if (string.IsNullOrEmpty(status))
                continue;
            if (statuses.Count == 0 || statuses[^1] != status)
                statuses.Add(status);
        }

        CollectionAssert.AreEqual(new[] { "100", "103", "200" }, statuses);
    }
}
