using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
///     SIGTERM stops new accepts and lets an in-flight response finish inside the drain window.
/// </summary>
[TestClass]
public class CliSigtermDrainE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // The CLI may still have the config file mapped for a moment.
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(60_000)]
    public async Task Sigterm_CompletesInFlightRequest_ThenRefusesAndExits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SIGTERM drain is a Unix signal. Windows process kill does not drain.");
        }

        const int drainSeconds = 8;
        using var origin = new SlowOrigin(TimeSpan.FromSeconds(2));
        var cfg = Path.Combine(_tempDir, "drain.yaml");
        CliProcessHarness? harness = null;
        var listen = 0;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            listen = CliProcessHarness.GetFreePort();
            await File.WriteAllTextAsync(cfg, $"""
                schemaVersion: "7.0"
                listeners:
                  - host: "127.0.0.1"
                    port: {listen}
                    decryptSsl: false
                    forwardHost: "127.0.0.1"
                    forwardPort: {origin.Port}
                server:
                  drainTimeoutSeconds: {drainSeconds}
                """);
            harness?.Dispose();
            harness = new CliProcessHarness();
            try
            {
                await harness.StartRunAsync(cfg);
                break;
            }
            catch (InvalidOperationException ex) when (attempt < 5 && AddressInUse(ex))
            {
                // The free port was taken between the probe and the CLI bind. Try another.
            }
        }

        using var running = harness ?? throw new AssertFailedException("CLI did not start.");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var inflight = http.GetAsync($"http://127.0.0.1:{listen}/drain");
        await origin.WaitForRequestAsync();
        var signalled = DateTime.UtcNow;
        running.SendSigterm();

        using var response = await inflight;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, listen);
        var sent = Encoding.ASCII.GetBytes("GET /after HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await probe.GetStream().WriteAsync(sent);
        var buffer = new byte[64];
        var read = await probe.GetStream().ReadAsync(buffer);
        Assert.AreEqual(0, read, "a connection accepted during drain must be closed without a response");

        var deadline = signalled.AddSeconds(drainSeconds + 4);
        while (running.ExitCode is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.AreEqual(0, running.ExitCode, running.StdOut + running.StdErr);
        Assert.IsTrue(DateTime.UtcNow <= deadline, "process outlived the drain timeout plus margin");
    }

    private static bool AddressInUse(InvalidOperationException ex)
    {
        var text = ex.Message;
        return text.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || text.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SlowOrigin : IDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly TimeSpan delay;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowOrigin(TimeSpan delay)
        {
            this.delay = delay;
            (listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
            _ = AcceptAsync();
        }

        public int Port { get; }

        public async Task WaitForRequestAsync()
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public void Dispose()
        {
            cts.Cancel();
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // Already stopped.
            }

            cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                started.TrySetResult();
                try
                {
                    await Task.Delay(delay, cts.Token);
                    var body = "ok"u8.ToArray();
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch
                {
                    try
                    {
                        ctx.Response.Abort();
                    }
                    catch
                    {
                        // The client is already gone.
                    }
                }
            }
        }
    }
}
