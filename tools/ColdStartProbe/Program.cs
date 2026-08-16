using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

// Measures proxy CONNECT cold-start the way a browser does: the ClientHello offers h2 ALPN, which is
// what makes the proxy resolve the origin's HTTP/2 capability before it can finish the handshake
// with us. tls_ms (CONNECT accepted -> client handshake complete) is the metric this tool exists for.
//
// Read the output carefully: what gets measured after the handshake depends on the negotiated ALPN.
// Issuing a request over h2 needs full framing, which is out of scope here, so an h2 result stops at
// the handshake and reports measured=handshake, while an http/1.1 result goes on to send a GET and
// reports measured=handshake+get. total_ms is therefore NOT comparable between the two - only compare
// tls_ms across configurations, and only compare total_ms within the same measured= mode.
//
// Usage:
//   ColdStartProbe <proxyHost> <proxyPort> <httpsUrl> [--warmup <url>]
// Prints: code=... connect_ms=... tls_ms=... ttfb_ms=... total_ms=... alpn=... measured=...

if (args.Length < 3)
{
    await Console.Error.WriteLineAsync("Usage: ColdStartProbe <proxyHost> <proxyPort> <httpsUrl> [--warmup <url>]");
    return 2;
}

var proxyHost = args[0];
var proxyPort = int.Parse(args[1]);
var url = new Uri(args[2]);
string? warmupUrl = null;
for (var i = 3; i < args.Length - 1; i++)
{
    if (args[i] == "--warmup")
        warmupUrl = args[i + 1];
}

if (warmupUrl != null)
    await FetchThroughProxyAsync(proxyHost, proxyPort, new Uri(warmupUrl), discard: true);

var result = await FetchThroughProxyAsync(proxyHost, proxyPort, url, discard: false);
await Console.Out.WriteLineAsync(
    $"code={result.StatusCode} connect_ms={result.ConnectMs:F1} tls_ms={result.TlsMs:F1} " +
    $"ttfb_ms={result.TtfbMs:F1} total_ms={result.TotalMs:F1} alpn={result.Alpn} measured={result.Measured}");
return result.StatusCode is >= 200 and < 500 ? 0 : 1;

static async Task<ProbeResult> FetchThroughProxyAsync(string proxyHost, int proxyPort, Uri url, bool discard)
{
    var totalSw = Stopwatch.StartNew();
    using var tcp = new TcpClient();
    var connectSw = Stopwatch.StartNew();
    await tcp.ConnectAsync(proxyHost, proxyPort);
    connectSw.Stop();

    await using var network = tcp.GetStream();
    var connectReq =
        $"CONNECT {url.Host}:{url.Port} HTTP/1.1\r\nHost: {url.Host}:{url.Port}\r\n\r\n";
    var connectBytes = Encoding.ASCII.GetBytes(connectReq);
    await network.WriteAsync(connectBytes);

    // Read CONNECT response headers
    var headerBuffer = new MemoryStream();
    var b = new byte[1];
    while (true)
    {
        var n = await network.ReadAsync(b);
        if (n == 0) throw new IOException("Proxy closed during CONNECT");
        headerBuffer.WriteByte(b[0]);
        if (headerBuffer.Length >= 4)
        {
            var arr = headerBuffer.GetBuffer();
            var len = (int)headerBuffer.Length;
            if (arr[len - 4] == '\r' && arr[len - 3] == '\n' && arr[len - 2] == '\r' && arr[len - 1] == '\n')
                break;
        }

        if (headerBuffer.Length > 64 * 1024)
            throw new IOException("CONNECT response too large");
    }

    var connectResponse = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0, (int)headerBuffer.Length);
    if (!connectResponse.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) &&
        !connectResponse.StartsWith("HTTP/1.0 200", StringComparison.Ordinal))
        throw new IOException("CONNECT failed: " + connectResponse.Split('\r')[0]);

    using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
    var tlsSw = Stopwatch.StartNew();
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = url.Host,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ApplicationProtocols =
        [
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11
        ],
        RemoteCertificateValidationCallback = static (_, _, _, _) => true
    });
    tlsSw.Stop();

    var alpn = ssl.NegotiatedApplicationProtocol.ToString();
    if (string.IsNullOrEmpty(alpn))
        alpn = "http/1.1";

    // Sending a request over h2 would require the connection preface and HPACK-encoded HEADERS
    // frames, which this tool deliberately does not implement - the handshake is the cold-start gate
    // it is measuring. So an h2 result stops here and says so via measured=handshake; see the header
    // comment for why total_ms must not be compared across the two modes.
    double ttfbMs;
    int status;
    if (alpn.Contains("h2", StringComparison.OrdinalIgnoreCase))
    {
        ttfbMs = tlsSw.Elapsed.TotalMilliseconds;
        status = 200;
        totalSw.Stop();
        if (!discard)
            return new ProbeResult(status, connectSw.Elapsed.TotalMilliseconds, tlsSw.Elapsed.TotalMilliseconds,
                ttfbMs, totalSw.Elapsed.TotalMilliseconds, alpn, "handshake");
        return new ProbeResult(0, 0, 0, 0, 0, alpn, "handshake");
    }

    var path = string.IsNullOrEmpty(url.PathAndQuery) ? "/" : url.PathAndQuery;
    var req = $"GET {path} HTTP/1.1\r\nHost: {url.Host}\r\nConnection: close\r\n\r\n";
    var reqBytes = Encoding.ASCII.GetBytes(req);
    var ttfbSw = Stopwatch.StartNew();
    await ssl.WriteAsync(reqBytes);

    var respBuf = new byte[4096];
    var read = await ssl.ReadAsync(respBuf);
    ttfbSw.Stop();
    totalSw.Stop();

    var respText = Encoding.ASCII.GetString(respBuf, 0, Math.Max(read, 0));
    status = 0;
    var firstLine = respText.Split('\r')[0];
    var parts = firstLine.Split(' ');
    if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
        status = code;

    if (discard)
        return new ProbeResult(0, 0, 0, 0, 0, alpn, "handshake+get");

    return new ProbeResult(status, connectSw.Elapsed.TotalMilliseconds, tlsSw.Elapsed.TotalMilliseconds,
        ttfbSw.Elapsed.TotalMilliseconds, totalSw.Elapsed.TotalMilliseconds, alpn, "handshake+get");
}

readonly record struct ProbeResult(
    int StatusCode, double ConnectMs, double TlsMs, double TtfbMs, double TotalMs, string Alpn, string Measured);
