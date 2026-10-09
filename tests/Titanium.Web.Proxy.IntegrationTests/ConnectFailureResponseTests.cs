using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class ConnectFailureResponseTests
{
    private static TestServer sharedServer = null!;
    private static readonly string[] separator = new[] { "\r\n" };

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Preconnect_DnsFailure_Returns_Http_Error_Before_Tls()
    {
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        var endPoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];

        endPoint.BeforeTunnelConnectRequest += async (_, e) =>
        {
            e.EstablishServerConnectionBeforeResponse = true;
            await Task.CompletedTask;
        };

        var customStatusSeen = false;
        endPoint.BeforeTunnelConnectFailure += async (_, e) =>
        {
            e.Response = new GenericResponse(HttpStatusCode.BadGateway)
            {
                HttpVersion = new Version(1, 1),
                Body = Encoding.UTF8.GetBytes("dns failed")
            };
            customStatusSeen = true;
            await Task.CompletedTask;
        };

        var responseText = await SendRawConnectAsync(proxy.ProxyEndPoints[0].Port,
            "no-such-host.invalid.example:443");

        Assert.IsTrue(customStatusSeen);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 502", StringComparison.Ordinal), responseText);
        Assert.IsTrue(responseText.Contains("dns failed"), responseText);
        Assert.IsFalse(responseText.Contains("200 Connection established", StringComparison.OrdinalIgnoreCase),
            "Client must not receive CONNECT 200 when preconnect fails.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Preconnect_TcpRefusal_Returns_Http_Error_Before_Tls()
    {
        // Bind and immediately close a listener so the port is refused.
        var refusedPort = GetRefusedPort();

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        var endPoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];

        endPoint.BeforeTunnelConnectRequest += async (_, e) =>
        {
            e.EstablishServerConnectionBeforeResponse = true;
            await Task.CompletedTask;
        };

        var responseText = await SendRawConnectAsync(proxy.ProxyEndPoints[0].Port,
            $"127.0.0.1:{refusedPort}");

        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 502", StringComparison.Ordinal), responseText);
        Assert.IsFalse(responseText.Contains("200 Connection established", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Preconnect_UpstreamProxyReject_Surfaces_Typed_Status_To_Client()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        using var upstreamProxy = new RejectingUpstreamProxy(403, "Forbidden", "access denied by upstream");
        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpsProxy = new ExternalProxy("localhost", upstreamProxy.Port)
        {
            UseDefaultCredentials = false
        };

        var endPoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        UpstreamProxyConnectException? typed = null;

        endPoint.BeforeTunnelConnectRequest += async (_, e) =>
        {
            e.EstablishServerConnectionBeforeResponse = true;
            await Task.CompletedTask;
        };
        endPoint.BeforeTunnelConnectFailure += async (_, e) =>
        {
            typed = e.Exception as UpstreamProxyConnectException
                    ?? e.Exception.InnerException as UpstreamProxyConnectException;
            await Task.CompletedTask;
        };

        var responseText = await SendRawConnectAsync(proxy.ProxyEndPoints[0].Port,
            $"localhost:{server.HttpsListeningPort}");

        Assert.IsNotNull(typed);
        Assert.AreEqual(403, typed.StatusCode);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 403", StringComparison.Ordinal), responseText);
        Assert.IsTrue(responseText.Contains("access denied by upstream"), responseText);
        Assert.IsFalse(responseText.Contains("200 Connection established", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Default_No_Preconnect_Still_Sends_200_For_Unreachable_Host()
    {
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        // EstablishServerConnectionBeforeResponse stays false (default).

        var responseText = await SendRawConnectAsync(proxy.ProxyEndPoints[0].Port,
            "no-such-host.invalid.example:443");

        Assert.IsTrue(
            responseText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ||
            responseText.StartsWith("HTTP/1.0 200", StringComparison.Ordinal),
            "Default behavior must still send CONNECT 200 without preconnect. Got: " + responseText);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    [TestCategory("Regression-2026-10-07")]
    public async Task OpaqueTunnel_Preconnect_DnsOrRefusal_Returns502_NotBareClose()
    {
        // Replay finding: opaque (DecryptSsl=false) tunnels answered CONNECT 200 and then silently closed on
        // DNS/refusal, which a browser reports as ERR_CONNECTION_CLOSED with no diagnosis.
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        var endPoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endPoint.BeforeTunnelConnectRequest += async (_, e) =>
        {
            e.DecryptSsl = false;
            e.EstablishServerConnectionBeforeResponse = !e.DecryptSsl;
            await Task.CompletedTask;
        };

        foreach (var authority in new[] { "no-such-host.invalid.example:443", $"127.0.0.1:{GetRefusedPort()}" })
        {
            var responseText = await SendRawConnectAsync(proxy.ProxyEndPoints[0].Port, authority);
            Assert.IsTrue(responseText.StartsWith("HTTP/1.1 502", StringComparison.Ordinal),
                authority + ": " + responseText);
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    [TestCategory("Regression-2026-10-07")]
    public async Task OpaqueTunnel_Preconnect_ReusesDialedConnection_AndRelaysBytes()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var accepted = 0;
        var echoed = 0;
        var echo = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await origin.AcceptTcpClientAsync(); }
                catch { return; }

                Interlocked.Increment(ref accepted);
                _ = Task.Run(async () =>
                {
                    using var cc = c;
                    var s = cc.GetStream();
                    var buf = new byte[256];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0)
                    {
                        Interlocked.Add(ref echoed, n);
                        await s.WriteAsync(buf.AsMemory(0, n));
                    }
                });
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        var endPoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endPoint.BeforeTunnelConnectRequest += async (_, e) =>
        {
            e.DecryptSsl = false;
            e.EstablishServerConnectionBeforeResponse = true;
            await Task.CompletedTask;
        };

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));

        var head = new byte[512];
        using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var read = await stream.ReadAsync(head, rcts.Token);
        Assert.IsTrue(Encoding.ASCII.GetString(head, 0, read).Contains(" 200 "));

        // A real TLS ClientHello is relayed verbatim to the (reused) pre-dialed connection. The echo
        // origin answers with the hello itself, so the handshake fails - only the relay matters here.
        using var ssl = new System.Net.Security.SslStream(stream, false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = "localhost"
            }, rcts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // expected: echo is not a TLS server
        }

        Assert.IsTrue(Volatile.Read(ref echoed) > 0, "ClientHello bytes must reach the origin");
        Assert.AreEqual(1, Volatile.Read(ref accepted), "origin must be dialed exactly once");        origin.Stop();
        await echo;
    }

    private static int GetRefusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> SendRawConnectAsync(int proxyPort, string authority)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();

        var request = Encoding.ASCII.GetBytes(
            $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        await stream.WriteAsync(request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        string text = string.Empty;
        while (ms.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            text = Encoding.ASCII.GetString(ms.ToArray());
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;

            var contentLength = 0;
            foreach (var line in text.Substring(0, headerEnd).Split(separator, StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var parsed))
                    contentLength = parsed;
            }

            var bodyReceived = ms.Length - (headerEnd + 4);
            if (bodyReceived >= contentLength) break;
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
