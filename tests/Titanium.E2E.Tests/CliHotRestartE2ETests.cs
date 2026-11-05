using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
///     Two real CLI processes sharing one TCP listener (SO_REUSEPORT) through <c>server.hotRestart</c>. Linux only:
///     the feature itself is Linux only, so every other OS reports Inconclusive with that reason.
/// </summary>
[TestClass]
public class CliHotRestartE2ETests
{
    private const int HandoverSeconds = 30;
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twp-e2e-hotrestart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A CLI may still hold the files for a moment.
        }
    }

    private string PidFile => Path.Combine(_dir, "titanium.pid");

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("server.hotRestart (SO_REUSEPORT plus SIGUSR2 handover) is supported on Linux only.");
        }
    }

    private string WriteConfig(int listenPort, int originPort, string name = "hot.yaml")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            server:
              hotRestart:
                enabled: true
                pidFile: "{PidFile}"
                handoverTimeoutSeconds: {HandoverSeconds}
            """);
        return path;
    }

    private async Task<int> WaitForPidFileAsync(Func<int, bool> accept, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (int.TryParse((await File.ReadAllTextAsync(PidFile)).Trim(), out var pid) && accept(pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // Being replaced right now.
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("pid file never reached the expected value; content: " +
                                   (File.Exists(PidFile) ? File.ReadAllText(PidFile) : "<missing>"));
    }

    private static async Task WaitForExitAsync(CliProcessHarness harness, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (harness.ExitCode is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsNotNull(harness.ExitCode, "process did not exit. " + harness.StdOut + harness.StdErr);
    }

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.Zero,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static async Task<HttpStatusCode> GetAsync(HttpClient http, int port, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        request.Headers.ConnectionClose = true;
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>NOT RUN LOCALLY (Windows author); runs in the Linux CI job.</summary>
    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task Handover_KeepsServing_FinishesInFlightOnTheOldProcess_ThenOldExits()
    {
        RequireLinux();

        using var origin = new Origin(inFlightDelay: TimeSpan.FromSeconds(10));
        var listenPort = CliProcessHarness.GetFreePort();
        var cfg = WriteConfig(listenPort, origin.Port);

        using var old = new CliProcessHarness();
        await old.StartRunAsync(cfg);
        var oldPid = await WaitForPidFileAsync(pid => pid == old.ProcessId, TimeSpan.FromSeconds(15));

        using var http = NewClient();
        Assert.AreEqual(HttpStatusCode.OK, await GetAsync(http, listenPort, "/fast"));

        // A slow response that is still in flight when the handover happens.
        var inFlight = GetAsync(http, listenPort, "/slow");
        await origin.WaitForSlowRequestAsync();

        // Steady new connections across the whole handover. A failed attempt is retried once, the way any client of a
        // restarting service behaves (a connection that sat in the old socket's accept queue at the instant it
        // closed is reset by the kernel); both counts are reported.
        using var stopLoad = new CancellationTokenSource();
        var firstAttemptFailures = 0;
        var failuresAfterRetry = 0;
        var requests = 0;
        var load = Task.Run(async () =>
        {
            using var loadClient = NewClient();
            while (!stopLoad.IsCancellationRequested)
            {
                Interlocked.Increment(ref requests);
                try
                {
                    await GetAsync(loadClient, listenPort, "/fast");
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref firstAttemptFailures);
                    try
                    {
                        await GetAsync(loadClient, listenPort, "/fast");
                    }
                    catch (HttpRequestException)
                    {
                        Interlocked.Increment(ref failuresAfterRetry);
                    }
                }

                await Task.Delay(10);
            }
        });

        using var next = new CliProcessHarness();
        await next.StartRunAsync(cfg);
        var newPid = await WaitForPidFileAsync(pid => pid == next.ProcessId, TimeSpan.FromSeconds(30));
        Assert.AreNotEqual(oldPid, newPid, "the pid file must now name the new process");

        // The old process finishes what it accepted; the new one serves everything new.
        Assert.AreEqual(HttpStatusCode.OK, await inFlight, "the in-flight download must finish");
        Assert.IsNull(old.ExitCode, "the old process must stay alive until the in-flight request is done");
        Assert.AreEqual(HttpStatusCode.OK, await GetAsync(http, listenPort, "/fast"));

        await WaitForExitAsync(old, TimeSpan.FromSeconds(HandoverSeconds + 10));
        Assert.AreEqual(0, old.ExitCode, old.StdOut + old.StdErr);

        await stopLoad.CancelAsync();
        await load;
        Assert.IsTrue(requests > 20, $"the load loop barely ran ({requests} requests)");
        Assert.AreEqual(0, failuresAfterRetry,
            $"{failuresAfterRetry} of {requests} requests failed even after a retry (first-attempt failures: {firstAttemptFailures}).");
        Assert.IsNull(next.ExitCode, "the new process must still be running. " + next.StdOut + next.StdErr);
        Assert.AreEqual(newPid, await WaitForPidFileAsync(_ => true, TimeSpan.FromSeconds(1)), "the old process must not delete the new pid file");
    }

    /// <summary>NOT RUN LOCALLY (Windows author); runs in the Linux CI job.</summary>
    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task StalePidFile_IsIgnored_AndOverwritten()
    {
        RequireLinux();

        using var origin = new Origin(inFlightDelay: TimeSpan.Zero);
        var listenPort = CliProcessHarness.GetFreePort();
        var cfg = WriteConfig(listenPort, origin.Port);

        // A pid that is certainly not a running titanium process.
        await File.WriteAllTextAsync(PidFile, "2147483646");

        using var cli = new CliProcessHarness();
        await cli.StartRunAsync(cfg);
        await WaitForPidFileAsync(pid => pid == cli.ProcessId, TimeSpan.FromSeconds(15));

        using var http = NewClient();
        Assert.AreEqual(HttpStatusCode.OK, await GetAsync(http, listenPort, "/fast"));
        Assert.IsNull(cli.ExitCode);
    }

    /// <summary>NOT RUN LOCALLY (Windows author); runs in the Linux CI job.</summary>
    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task SuccessorThatFailsBeforeReady_LeavesTheOldProcessServing()
    {
        RequireLinux();

        using var origin = new Origin(inFlightDelay: TimeSpan.Zero);
        var listenPort = CliProcessHarness.GetFreePort();
        var cfg = WriteConfig(listenPort, origin.Port);

        using var old = new CliProcessHarness();
        await old.StartRunAsync(cfg);
        var oldPid = await WaitForPidFileAsync(pid => pid == old.ProcessId, TimeSpan.FromSeconds(15));

        // Same hot-restart section, but a route that cannot load: the process exits before it is ready.
        var broken = Path.Combine(_dir, "broken.yaml");
        await File.WriteAllTextAsync(broken, (await File.ReadAllTextAsync(cfg)) + "\nroutes:\n  - this: [is not a route\n");
        using var failed = new CliProcessHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.StartRunAsync(broken));

        Assert.IsNull(old.ExitCode, "the old process must not be stopped by a failed successor");
        Assert.AreEqual(oldPid, await WaitForPidFileAsync(_ => true, TimeSpan.FromSeconds(1)));
        using var http = NewClient();
        Assert.AreEqual(HttpStatusCode.OK, await GetAsync(http, listenPort, "/fast"));
    }

    /// <summary>NOT RUN LOCALLY (Windows author); runs in the Linux CI job.</summary>
    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task RestartCommand_StartsASuccessor_AndTheOldProcessExits()
    {
        RequireLinux();

        using var origin = new Origin(inFlightDelay: TimeSpan.Zero);
        var listenPort = CliProcessHarness.GetFreePort();
        var cfg = WriteConfig(listenPort, origin.Port);

        using var old = new CliProcessHarness();
        await old.StartRunAsync(cfg);
        var oldPid = await WaitForPidFileAsync(pid => pid == old.ProcessId, TimeSpan.FromSeconds(15));

        using var helper = new CliProcessHarness();
        var (exit, stdout, stderr) = await helper.RunOnceAsync(["restart", "-c", cfg], TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, exit, stdout + stderr);
        StringAssert.Contains(stdout, "is ready and owns");

        var newPid = await WaitForPidFileAsync(pid => pid != oldPid, TimeSpan.FromSeconds(5));
        try
        {
            await WaitForExitAsync(old, TimeSpan.FromSeconds(HandoverSeconds + 10));
            Assert.AreEqual(0, old.ExitCode, old.StdOut + old.StdErr);

            using var http = NewClient();
            Assert.AreEqual(HttpStatusCode.OK, await GetAsync(http, listenPort, "/fast"));
        }
        finally
        {
            // The successor was started by the helper (detached), so the harness does not own it.
            KillQuietly(newPid);
        }
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Plain HTTP origin: <c>/fast</c> answers at once, <c>/slow</c> after <c>inFlightDelay</c>.</summary>
    private sealed class Origin : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource _slowSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Origin(TimeSpan inFlightDelay)
        {
            _delay = inFlightDelay;
            (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
            _ = AcceptAsync();
        }

        public int Port { get; }

        public Task WaitForSlowRequestAsync() => _slowSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Already stopped.
            }

            _cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(context));
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.Url?.AbsolutePath == "/slow")
                {
                    _slowSeen.TrySetResult();
                    await Task.Delay(_delay, _cts.Token);
                }

                var body = "ok"u8.ToArray();
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, _cts.Token);
                context.Response.Close();
            }
            catch
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // The client is gone.
                }
            }
        }
    }
}
