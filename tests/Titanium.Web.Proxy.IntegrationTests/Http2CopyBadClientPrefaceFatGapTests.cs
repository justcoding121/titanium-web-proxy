using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: malformed client connection preface must fail fast
///     without wedging the listener.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyBadClientPrefaceFatGapTests
{
    /// <summary>
    ///     Bug class: invalid HTTP/2 preface leaves the connection open and blocks the next client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BadPreface_ClosesSocket_SecondClientStillWorks()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok-after-bad-preface"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var port = proxy.ProxyEndPoints[0].Port;

        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync("127.0.0.1", port);
            await using var stream = bad.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("NOT H2 PREFACE\r\n"));
            await Task.Delay(200);
        }

        using var client = suite.GetClient(proxy);
        var body = await client.GetStringAsync(suite.GetServer().ListeningHttpUrl);
        Assert.AreEqual("ok-after-bad-preface", body);
    }
}
