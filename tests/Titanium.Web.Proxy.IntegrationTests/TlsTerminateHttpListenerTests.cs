using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     TLS terminate must rewrite Host to the origin bind (not the public listen port).
///     HttpListener on Unix 404s when Host is 127.0.0.1:listenPort.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TlsTerminateHttpListenerTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task TlsTerminate_HttpListenerOrigin_DoesNot404OnListenHost()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var accept = Task.Run(() => ServeOneEchoAsync(origin, cts.Token), cts.Token);

        using var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();

        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var response = await client.GetAsync($"https://127.0.0.1:{endPoint.Port}/tls");
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            StringAssert.Contains(body, "echo:/tls");
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    private static (HttpListener Listener, int Port) BindLoopbackHttpListener()
    {
        Exception? last = null;
        for (var i = 0; i < 8; i++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind HttpListener.", last);
    }

    private static async Task ServeOneEchoAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var body = Encoding.UTF8.GetBytes($"echo:{path}:GET");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
