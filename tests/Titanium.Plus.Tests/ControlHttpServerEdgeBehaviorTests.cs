using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.ControlPlane;

namespace Titanium.Plus.Tests;

/// <summary>
///     Leftover ControlHttpServer branches: chunked bodies, Expect: 100-continue, TLS accept, and query edge cases.
/// </summary>
[TestClass]
public class ControlHttpServerEdgeBehaviorTests
{
    private static async Task<string> SendRawAsync(int port, string raw, int maxResponseBytes = 8192)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        tcp.Client.Shutdown(SocketShutdown.Send);

        var buf = new byte[maxResponseBytes];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(total))) > 0)
        {
            total += read;
            if (total >= buf.Length)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buf, 0, total);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ChunkedPost_WithTrailer_IsDecoded()
    {
        // Bug class: Transfer-Encoding: chunked bodies are dropped or truncated before the handler runs.
        string? seen = null;
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _)
        {
            seen = req.BodyText();
            return Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok"));
        }

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        var raw =
            "POST /c HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n" +
            "X-Trailer: 1\r\n" +
            "\r\n";
        var response = await SendRawAsync(server.Port, raw);
        StringAssert.StartsWith(response, "HTTP/1.1 200");
        Assert.AreEqual("hello world", seen);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ExpectContinue_Writes100BeforeBody()
    {
        // Bug class: Expect: 100-continue hangs because the server never sends 100 Continue.
        string? seen = null;
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _)
        {
            seen = req.BodyText();
            return Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok"));
        }

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        var head = "POST /e HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\nExpect: 100-continue\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

        var buf = new byte[256];
        var n = await stream.ReadAsync(buf);
        var interim = Encoding.ASCII.GetString(buf, 0, n);
        StringAssert.StartsWith(interim, "HTTP/1.1 100");

        await stream.WriteAsync("body"u8.ToArray());
        tcp.Client.Shutdown(SocketShutdown.Send);

        var total = 0;
        while ((n = await stream.ReadAsync(buf.AsMemory(total))) > 0)
        {
            total += n;
            if (total >= buf.Length)
            {
                break;
            }
        }

        var final = Encoding.UTF8.GetString(buf, 0, total);
        StringAssert.Contains(final, "HTTP/1.1 200");
        Assert.AreEqual("body", seen);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task TlsListener_ServesRequestOverSslStream()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var cert = X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);

        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _) =>
            Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "secure:" + req.Path));

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, cert, Handler);
        server.Start();
        Assert.IsTrue(server.IsTls);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("127.0.0.1");
        await ssl.WriteAsync("GET /tls HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"u8.ToArray());
        tcp.Client.Shutdown(SocketShutdown.Send);

        var buf = new byte[1024];
        var total = 0;
        int read;
        while ((read = await ssl.ReadAsync(buf.AsMemory(total))) > 0)
        {
            total += read;
            if (total >= buf.Length)
            {
                break;
            }
        }

        var response = Encoding.UTF8.GetString(buf, 0, total);
        StringAssert.StartsWith(response, "HTTP/1.1 200");
        StringAssert.Contains(response, "secure:/tls");
    }

    [TestMethod]
    public void QueryValue_DecodesPlusAndMissingEquals()
    {
        var req = new ControlHttpRequest
        {
            Method = "GET",
            Path = "/",
            Query = "a=b+c&flag&x=%2F",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        Assert.AreEqual("b c", req.QueryValue("a"));
        Assert.AreEqual("", req.QueryValue("flag"));
        Assert.AreEqual("/", req.QueryValue("x"));
        Assert.IsNull(req.QueryValue("missing"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task MalformedContentLength_Returns400()
    {
        using var server = new ControlHttpServer(
            IPAddress.Loopback,
            0,
            null,
            static (_, _) => Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok")));
        server.Start();
        var response = await SendRawAsync(server.Port, "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: no\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ChunkedPost_WithChunkExtension_IsDecoded()
    {
        string? seen = null;
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _)
        {
            seen = req.BodyText();
            return Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok"));
        }

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        var raw =
            "POST /ext HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "4;ext=1\r\ndata\r\n" +
            "0\r\n\r\n";
        var response = await SendRawAsync(server.Port, raw);
        StringAssert.StartsWith(response, "HTTP/1.1 200");
        Assert.AreEqual("data", seen);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ChunkedPost_BadSize_Returns400()
    {
        using var server = new ControlHttpServer(
            IPAddress.Loopback,
            0,
            null,
            static (_, _) => Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok")));
        server.Start();

        var response = await SendRawAsync(
            server.Port,
            "POST /bad HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task TruncatedBody_Returns400()
    {
        // Bug class: short bodies are treated as complete and reach the handler.
        using var server = new ControlHttpServer(
            IPAddress.Loopback,
            0,
            null,
            static (_, _) => Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok")));
        server.Start();

        var response = await SendRawAsync(
            server.Port,
            "POST /short HTTP/1.1\r\nHost: x\r\nContent-Length: 8\r\n\r\nabc");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task HeaderWithoutColon_Returns400()
    {
        using var server = new ControlHttpServer(
            IPAddress.Loopback,
            0,
            null,
            static (_, _) => Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok")));
        server.Start();

        var response = await SendRawAsync(
            server.Port,
            "GET / HTTP/1.1\r\nHost: x\r\nBadHeader\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DuplicateHeaders_KeepFirstValue()
    {
        string? host = null;
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _)
        {
            host = req.Header("Host");
            return Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok"));
        }

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        var response = await SendRawAsync(
            server.Port,
            "GET / HTTP/1.1\r\nHost: first\r\nHost: second\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 200");
        Assert.AreEqual("first", host);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Response_DoesNotDuplicateSecurityHeaders_AndStripsCRLFFromCustomHeaders()
    {
        Task<ControlHttpResponse> Handler(ControlHttpRequest _, CancellationToken __) =>
            Task.FromResult(ControlHttpResponse.Text(
                204,
                "text/plain",
                "",
                new KeyValuePair<string, string>("Cache-Control", "private"),
                new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
                new KeyValuePair<string, string>("X-Trace", "a\r\nInjected: yes")));

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        var response = await SendRawAsync(server.Port, "GET /h HTTP/1.1\r\nHost: x\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 204");
        Assert.AreEqual(1, CountOccurrences(response, "Cache-Control:"));
        Assert.AreEqual(1, CountOccurrences(response, "X-Content-Type-Options:"));
        StringAssert.Contains(response, "Cache-Control: private");
        Assert.IsFalse(response.Contains("\r\nInjected:", StringComparison.Ordinal));
        StringAssert.Contains(response, "X-Trace: a  Injected: yes");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
        {
            count++;
        }

        return count;
    }
}
