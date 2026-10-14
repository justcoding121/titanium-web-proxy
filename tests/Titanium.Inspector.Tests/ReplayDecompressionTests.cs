using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ReplayDecompressionTests
{
    [TestMethod]
    public async Task ReplayAsync_GzipResponse_WithAcceptEncoding_ReturnsDecodedJson()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var origin = new HttpListener();
        origin.Prefixes.Add($"http://127.0.0.1:{port}/");
        origin.Start();
        try
        {
            var served = Task.Run(async () =>
            {
                var ctx = await origin.GetContextAsync();
                var json = Encoding.UTF8.GetBytes("{\"gzip\":true}");
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    gzip.Write(json);
                }

                var payload = compressed.ToArray();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers["Content-Encoding"] = "gzip";
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.Close();
            });

            var snap = new SessionSnapshot
            {
                Method = "GET",
                Url = $"http://127.0.0.1:{port}/gzip",
                RequestHeadersText = "Accept: */*\nAccept-Encoding: gzip, deflate, br\n",
            };

            var result = await ReplayService.ReplayAsync(snap);

            await served;
            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(200, result.StatusCode);
            StringAssert.Contains(result.ResponseBody, "{\"gzip\":true}");
        }
        finally
        {
            origin.Stop();
        }
    }
}
