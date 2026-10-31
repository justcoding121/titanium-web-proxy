using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliCompressionE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-gzip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task Run_ResponseCompression_ReturnsGzipForCompressibleBody()
    {
        using var origin = new CompressibleEchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = WriteCompression(_tempDir, listen, origin.Port);

        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{listen}/big");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            using var resp = await http.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            StringAssert.Contains(resp.Content.Headers.ContentEncoding.ToString(), "gzip");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static string WriteCompression(string dir, int listen, int origin)
    {
        var path = Path.Combine(dir, $"gzip-{listen}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listen}
                decryptSsl: false
                enableHttp2: false
                forwardHost: "127.0.0.1"
                forwardPort: {origin}
            server:
              responseCompression:
                enabled: true
            """);
        return path;
    }
}

/// <summary>Large repetitive plain text suitable for gzip.</summary>
internal sealed class CompressibleEchoOrigin : IDisposable
{
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }

    public CompressibleEchoOrigin()
    {
        (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            /* ignore */
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var body = Encoding.UTF8.GetBytes(new string('x', 8192));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }, ct);
        }
    }
}
