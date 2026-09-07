using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.AccessLog;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class JsonAccessLogIntegrationTests
{
    [TestMethod]
    public async Task AfterResponse_WritesNdjson_WithoutBufferingBody()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "twp-access-int-" + Guid.NewGuid().ToString("N") + ".ndjson");
        var origin = new HttpListener();
        var originPort = 0;
        try
        {
            for (var i = 0; i < 8; i++)
            {
                originPort = GetFreePort();
                origin = new HttpListener();
                origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
                try
                {
                    origin.Start();
                    break;
                }
                catch
                {
                    origin.Close();
                }
            }

            _ = origin.GetContextAsync().ContinueWith(async t =>
            {
                var ctx = await t;
                var bytes = Encoding.UTF8.GetBytes("origin-ok");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });

            using var proxy = new ProxyServer(userTrustRootCertificate: false);
            proxy.EnableHttpInterception = true;
            proxy.EnableRequestTimingCapture = true;
            using var writer = new JsonAccessLogWriter(logPath, sampleRate: 1);
            proxy.AfterResponse += (_, e) =>
            {
                writer.TryWrite(e);
                return Task.CompletedTask;
            };

            var listenPort = GetFreePort();
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, listenPort, false));
            proxy.Start();

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listenPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.GetAsync($"http://127.0.0.1:{originPort}/hello-access");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            await Task.Delay(200);
            var text = await File.ReadAllTextAsync(logPath);
            Assert.IsTrue(text.Contains("hello-access", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("\"status\":200", StringComparison.Ordinal));
        }
        finally
        {
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { File.Delete(logPath); } catch { /* ignore */ }
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
