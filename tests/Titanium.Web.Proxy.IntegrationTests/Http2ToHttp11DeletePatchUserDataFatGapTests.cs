using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp11BridgeHandler</c> suite: DELETE, PATCH body, UserData,
///     and origin 418.
/// </summary>
[TestClass]
public class Http2ToHttp11DeletePatchUserDataFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H2ToH1(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    private static async Task<byte[]> ReadDataUntilEndAsync(Http2RawClient raw)
    {
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (frame.Type == Http2FrameType.Data)
            {
                ms.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    return ms.ToArray();
            }
            else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                return ms.ToArray();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Delete_RoundTrips()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Method;
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "DELETE"), (":scheme", "https"),
             (":authority", "localhost"), (":path", "/del")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("DELETE", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Patch_RoundTripsBody()
    {
        string? seenBody = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            seenBody = Encoding.UTF8.GetString(ms.ToArray());
            await ctx.Response.WriteAsync($"n={seenBody.Length}");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var body = Encoding.UTF8.GetBytes("patch-me");
        var block = raw.Connection.EncodeHeaders(
            [(":method", "PATCH"), (":scheme", "https"),
             (":authority", "localhost"), (":path", "/patch")],
            [("content-type", "application/merge-patch+json"),
             ("content-length", body.Length.ToString())]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, body);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var data = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual("n=8", Encoding.UTF8.GetString(data));
        Assert.AreEqual("patch-me", seenBody);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_UserData_SurvivesAfterResponse()
    {
        object? after = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        var (proxy, ep) = H2ToH1(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.UserData = "h2h1-ud";
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            after = e.UserData;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", "localhost"), (":path", "/ud")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        for (var i = 0; i < 50 && after is null; i++)
            await Task.Delay(50);
        Assert.AreEqual("h2h1-ud", after);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Origin418_SurfacesStatus()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            ctx.Response.StatusCode = 418;
            await ctx.Response.WriteAsync("teapot");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", "localhost"), (":path", "/tea")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("418", headers.Single(h => h.Name == ":status").Value);
        var data = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual("teapot", Encoding.UTF8.GetString(data));
    }
}
