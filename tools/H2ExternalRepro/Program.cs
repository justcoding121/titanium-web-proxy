using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

// Repro for the external-site H2 passthrough stall: GET a small and a large resource
// through a decrypting explicit proxy with no handlers (compressed passthrough path).

var url = args.Length > 0 ? args[0] : "https://www.google.com/";

using var proxy = new ProxyServer(false, false, false);
proxy.EnableHttp2 = true;

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0);
proxy.AddEndPoint(endPoint);
proxy.Start();

var port = proxy.ProxyEndPoints[0].Port;
Console.WriteLine($"proxy on {port}, fetching {url}");

var handler = new SocketsHttpHandler
{
    Proxy = new WebProxy($"http://localhost:{port}"),
    UseProxy = true,
    SslOptions =
    {
        RemoteCertificateValidationCallback = (_, cert, _, errors) => true
    }
};

// Diagnostic knob: TWP_REPRO_WINDOW=big raises the client's advertised INITIAL_WINDOW_SIZE so we can
// tell flow-control starvation (fetch succeeds) apart from an unrelated relay deadlock (still stalls).
if (Environment.GetEnvironmentVariable("TWP_REPRO_WINDOW") == "big")
{
    handler.InitialHttp2StreamWindowSize = 1024 * 1024;
    Console.WriteLine("client stream window: 1 MiB");
}

using var client = new HttpClient(handler)
{
    DefaultRequestVersion = new Version(2, 0),
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    Timeout = TimeSpan.FromSeconds(15)
};

try
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    Console.WriteLine($"headers: {(int)response.StatusCode} after {sw.ElapsedMilliseconds} ms " +
                      $"(content-length: {response.Content.Headers.ContentLength?.ToString() ?? "unknown"})");
    var body = await response.Content.ReadAsByteArrayAsync();
    Console.WriteLine($"body: {body.Length} bytes after {sw.ElapsedMilliseconds} ms");
    Console.WriteLine("SUCCESS");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.GetBaseException().Message}");
    Environment.ExitCode = 1;
}
finally
{
    proxy.Stop();
}
