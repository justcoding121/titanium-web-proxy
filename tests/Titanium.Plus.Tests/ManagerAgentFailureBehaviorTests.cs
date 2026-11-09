using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Manager;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

/// <summary>
///     Failure-path behavior for the Plus manager agent: unreachable / hostile managers, last-known-good boot, and
///     applier faults. Each test names the bug class it would catch.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ManagerAgentFailureBehaviorTests
{
    private const string ValidYaml = "schemaVersion: \"7.1\"\nlisteners: []\n";

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_ManagerDownAtBoot_AppliesLastKnownGood_ExactlyOnce()
    {
        // Bug class: a node restarted while the manager is down boots with no config, or re-applies the same
        // last-known-good on every failed poll.
        using var dir = new TempDir();
        WriteLastKnownGood(dir.Path, ValidYaml, "lkg-hash-1");
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(DeadUrl(), dir.Path, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => applier.Applied.Count >= 1, TimeSpan.FromSeconds(10));
        Assert.AreEqual("lkg-hash-1", applier.Applied[0].Hash);
        Assert.AreEqual(ValidYaml, applier.Applied[0].Yaml);
        StringAssert.Contains(logger.Text, "applying last-known-good");

        // The poll loop backs off for two seconds before the next failure; the same hash must not be re-applied.
        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        Assert.AreEqual(1, applier.Applied.Count);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Advisory_ManagerDown_NeverAppliesLastKnownGood()
    {
        // Bug class: advisory mode (report drift only) mutates the running config from disk.
        using var dir = new TempDir();
        WriteLastKnownGood(dir.Path, ValidYaml, "lkg-hash-2");
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(DeadUrl(), dir.Path, applier, "advisory", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => logger.Text.Contains("poll failed"), TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.AreEqual(0, applier.Attempts);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_LastKnownGood_MatchingTheRunningConfig_IsNotReapplied()
    {
        // Bug class: a restart re-applies the config the process already booted with (reload churn).
        using var dir = new TempDir();
        WriteLastKnownGood(dir.Path, ValidYaml, "same-hash");
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(DeadUrl(), dir.Path, applier, "managed", logger: logger, localConfigHash: "same-hash");
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => logger.Text.Contains("poll failed"), TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.AreEqual(0, applier.Attempts);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Unauthorized_BacksOff_InsteadOfFloodingTheManager()
    {
        // Bug class: a 401 returns instantly from the long poll and the loop re-asks with no pause, flooding the
        // manager with requests while the token is wrong.
        await using var manager = FakeManager.Start(ctx => ctx.Respond(401, "{}"));
        using var dir = new TempDir();
        var logger = new RecordingLogger();

        using var agent = Start(manager.Url, dir.Path, new RecordingApplier(), "managed", logger: logger);
        Assert.IsNotNull(agent);

        await Task.Delay(1500);
        var requests = manager.Count("/v1/agent/config");
        Assert.IsTrue(requests >= 1, "the agent must ask at least once");
        Assert.IsTrue(requests <= 4, $"a rejected token must not be retried in a tight loop (saw {requests} requests in 1.5s)");
        StringAssert.Contains(logger.Text, "unauthorized");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ServerError_BacksOff_ThenRecoversWhenTheManagerReturns()
    {
        // Bug class: a manager outage (5xx) either kills the poll loop for good or spins on it.
        var healthy = false;
        await using var manager = FakeManager.Start(ctx =>
        {
            if (ctx.Path == "/v1/agent/config")
            {
                return healthy ? ctx.Respond(200, ConfigBody("h-recover", ValidYaml)) : ctx.Respond(500, "boom");
            }

            return ctx.Respond(204, "");
        });
        using var dir = new TempDir();
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(manager.Url, dir.Path, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await Task.Delay(1200);
        var during = manager.Count("/v1/agent/config");
        Assert.IsTrue(during <= 3, $"5xx must back off (saw {during} requests in 1.2s)");
        StringAssert.Contains(logger.Text, "manager returned 500");
        Assert.AreEqual(0, applier.Attempts);

        healthy = true;
        await WaitUntilAsync(() => applier.Applied.Count >= 1, TimeSpan.FromSeconds(10));
        Assert.AreEqual("h-recover", applier.Applied[0].Hash);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MalformedManagerBody_IsASoftFailure_NotAnApply()
    {
        // Bug class: a 200 with garbage (or a payload without hash/yaml) crashes the loop or applies an empty config.
        await using var manager = FakeManager.Start(ctx => ctx.Path == "/v1/agent/config"
            ? ctx.Respond(200, """{"hash":"only-a-hash"}""")
            : ctx.Respond(204, ""));
        using var dir = new TempDir();
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(manager.Url, dir.Path, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => logger.Text.Contains("poll failed"), TimeSpan.FromSeconds(10));
        StringAssert.Contains(logger.Text, "missing hash or yaml");
        Assert.AreEqual(0, applier.Attempts);
        Assert.IsTrue(ManagerAgent.LiveLoops >= 1, "the loop keeps running");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_ApplierThrows_IsNackedWithTheMessage_AndKeepsNoLastKnownGood()
    {
        // Bug class: an exception from the host applier escapes the loop (no nack, loop dead) or is recorded as a
        // successful apply.
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { v = 1 });
        using var dir = new TempDir();
        var applier = new RecordingApplier { ThrowMessage = "applier exploded" };
        var logger = new RecordingLogger();

        using var agent = Start(harness.BaseUrl.TrimEnd('/'), dir.Path, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => harness.Acks.Any(a => !a.Accepted), TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Acks.Any(a => !a.Accepted && a.Reason == "applier exploded"));
        StringAssert.Contains(logger.Text, "apply failed: applier exploded");
        Assert.IsFalse(ManagerAgent.TryReadLastKnownGood(dir.Path, out _, out _));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_AckRejectedByManager_StillAppliesAndPersists()
    {
        // Bug class: a failing ack endpoint rolls back (or never persists) a config that was applied successfully.
        await using var manager = FakeManager.Start(ctx => ctx.Path switch
        {
            "/v1/agent/config" => ctx.Respond(200, ConfigBody("h-ack", ValidYaml)),
            "/v1/agent/ack" => ctx.Respond(503, "later"),
            _ => ctx.Respond(404, ""),
        });
        using var dir = new TempDir();
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(manager.Url, dir.Path, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(
            () => ManagerAgent.TryReadLastKnownGood(dir.Path, out _, out var hash) && hash == "h-ack",
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => logger.Text.Contains("ack returned 503"), TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, applier.Applied.Count);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_RedeliveredIdenticalRevision_IsNotAppliedTwice()
    {
        // Bug class: a manager that ignores have= (or a retried response) causes repeated reloads of the same config.
        var deliveries = 0;
        await using var manager = FakeManager.Start(async ctx =>
        {
            if (ctx.Path != "/v1/agent/config")
            {
                await ctx.Respond(204, "");
                return;
            }

            if (Interlocked.Increment(ref deliveries) <= 3)
            {
                await ctx.Respond(200, ConfigBody("h-same", ValidYaml));
                return;
            }

            await Task.Delay(1500);
            await ctx.Respond(204, "");
        });
        using var dir = new TempDir();
        var applier = new RecordingApplier();

        using var agent = Start(manager.Url, dir.Path, applier, "managed");
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => manager.Count("/v1/agent/config") >= 4, TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, applier.Applied.Count, "the same hash is applied once");
        Assert.AreEqual(1, manager.Count("/v1/agent/ack"), "and acknowledged once");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Managed_LastKnownGoodNotWritable_DoesNotFailTheApply()
    {
        // Bug class: a state directory that cannot be written turns a good apply into a nack (and a retry loop).
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { v = 1 });
        using var dir = new TempDir();
        var blocker = Path.Combine(dir.Path, "state-is-a-file");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();

        using var agent = Start(harness.BaseUrl.TrimEnd('/'), blocker, applier, "managed", logger: logger);
        Assert.IsNotNull(agent);

        await WaitUntilAsync(() => harness.Acks.Any(a => a.Accepted), TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, applier.Applied.Count);
        StringAssert.Contains(logger.Text, "failed to write last-known-good");
    }

    // ---- AgentConfigPayload ---------------------------------------------------------------------------------------------

    [TestMethod]
    public void ConfigPayload_AcceptsEveryDocumentedSpelling()
    {
        // Bug class: a manager using snake_case / a nested config object is rejected as "missing yaml".
        Assert.IsTrue(AgentConfigPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"config_hash":"h1","config_yaml":"a: 1","revision_id":"r9"}"""), out var snake, out _));
        Assert.AreEqual(("h1", "a: 1", "r9"), (snake!.ConfigHash, snake.Yaml, snake.RevisionId));

        Assert.IsTrue(AgentConfigPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"hash":"h2","config":{"listeners":[]}}"""), out var nested, out _));
        Assert.AreEqual("h2", nested!.RevisionId, "revision falls back to the hash");
        Assert.AreEqual("""{"listeners":[]}""", nested.Yaml, "an object config is kept as raw JSON");

        Assert.IsTrue(AgentConfigPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"hash":"h3","config":"b: 2"}"""), out var text, out _));
        Assert.AreEqual("b: 2", text!.Yaml);
    }

    [TestMethod]
    public void ConfigPayload_RejectsBadShapes_WithoutThrowing()
    {
        // Bug class: hostile or truncated manager bodies throw out of the parser instead of a soft failure.
        foreach (var (body, expected) in new (string, string)[]
                 {
                     ("", "empty body"),
                     ("[1,2]", "root must be object"),
                     ("{not json", "json:"),
                     ("""{"hash":"  ","yaml":"a: 1"}""", "missing hash or yaml"),
                     ("""{"hash":"h","yaml":" "}""", "missing hash or yaml"),
                     ("""{"hash":"h","yaml":7}""", "missing hash or yaml"),
                 })
        {
            Assert.IsFalse(AgentConfigPayload.TryParse(Encoding.UTF8.GetBytes(body), out var parsed, out var error), body);
            Assert.IsNull(parsed);
            StringAssert.Contains(error!, expected, body);
        }

        Assert.IsFalse(AgentConfigPayload.TryParse(null!, out _, out var nullError));
        Assert.AreEqual("empty body", nullError);
    }

    [TestMethod]
    public void ConfigPayload_FuzzEntryPoint_ReportsNoViolationsForHostileBytes()
    {
        // Bug class: the fuzz invariant itself regresses (an input makes TryParse throw).
        foreach (var bytes in new[]
                 {
                     Array.Empty<byte>(),
                     new byte[] { 0xFF, 0xFE, 0x00 },
                     Encoding.UTF8.GetBytes("""{"hash":"h","yaml":"y"}"""),
                     Encoding.UTF8.GetBytes("null"),
                 })
        {
            Assert.AreEqual(0, AgentConfigPayload.Check(bytes).Count);
            AgentConfigPayload.Fuzz(bytes);
        }

        Assert.AreEqual("boom", new AgentConfigFuzzInvariantException("boom").Message);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static ManagerAgent? Start(
        string url,
        string stateDir,
        RecordingApplier applier,
        string mode,
        RecordingLogger? logger = null,
        string? localConfigHash = null)
    {
        var tokenFile = Path.Combine(Path.GetTempPath(), "twp-agent-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tokenFile, "test-token");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["manager.url"] = url,
            ["manager.tokenFile"] = tokenFile,
            ["manager.fleet"] = "f1",
            ["manager.mode"] = mode,
            ["manager.stateDir"] = stateDir,
            ["manager.nodeId"] = "failure-node",
            ["manager.applyDebounceMs"] = "0",
        };
        return ManagerAgent.TryStart(
            new PlusActivationContext
            {
                ProxyServer = new object(),
                Options = options,
                ConfigApplier = applier,
                StateDirectory = stateDir,
                Logger = logger,
                HostStatus = localConfigHash is null ? null : new FixedHostStatus(localConfigHash),
            },
            options);
    }

    private static string DeadUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static string ConfigBody(string hash, string yaml) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["configHash"] = hash,
            ["yaml"] = yaml,
            ["revisionId"] = "rev-" + hash,
        });

    private static void WriteLastKnownGood(string dir, string yaml, string hash)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManagerAgent.LastKnownGoodYamlFileName), yaml);
        File.WriteAllText(Path.Combine(dir, ManagerAgent.LastKnownGoodHashFileName), hash);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(40);
        }

        Assert.Fail("Condition not met within " + timeout);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twp-agent-fail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed class RecordingApplier : IConfigApplier
    {
        private readonly ConcurrentQueue<(string Yaml, string Hash)> _applied = new();
        private int _attempts;

        public string? ThrowMessage { get; init; }

        public int Attempts => Volatile.Read(ref _attempts);

        public IReadOnlyList<(string Yaml, string Hash)> Applied => _applied.ToArray();

        public Task<ConfigApplyResult> ApplyAsync(string yaml, string configHash, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            if (ThrowMessage is not null)
            {
                throw new InvalidOperationException(ThrowMessage);
            }

            _applied.Enqueue((yaml, configHash));
            return Task.FromResult(new ConfigApplyResult { Ok = true, AppliedHash = configHash });
        }
    }

    private sealed class FixedHostStatus(string configHash) : IPlusHostStatus
    {
        public void SetManagedState(bool managed, bool drifted)
        {
        }

        public PlusConfigStatus GetConfigStatus() => new() { ConfigHash = configHash, Managed = true, Drifted = false };

        public IReadOnlyList<PlusCertificateInfo> GetCertificates() => [];
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join("\n", _lines);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>One request handed to a <see cref="FakeManager"/> handler.</summary>
    private sealed class FakeCall(HttpListenerContext context)
    {
        public string Path => context.Request.Url?.AbsolutePath ?? "/";

        public async Task Respond(int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
            {
                await context.Response.OutputStream.WriteAsync(bytes);
            }

            context.Response.Close();
        }
    }

    /// <summary>Scriptable manager: every request goes to the handler; paths are counted.</summary>
    private sealed class FakeManager : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<FakeCall, Task> _handler;
        private readonly ConcurrentDictionary<string, int> _counts = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private FakeManager(int port, Func<FakeCall, Task> handler)
        {
            _handler = handler;
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public string Url { get; }

        public static FakeManager Start(Func<FakeCall, Task> handler)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try
                {
                    return new FakeManager(port, handler);
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException("could not bind a fake manager", last);
        }

        public int Count(string path) => _counts.GetValueOrDefault(path);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
                {
                    return;
                }

                var call = new FakeCall(context);
                _counts.AddOrUpdate(call.Path, 1, (_, n) => n + 1);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handler(call);
                    }
                    catch
                    {
                        try
                        {
                            context.Response.Abort();
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                _listener.Close();
            }
            catch
            {
                // ignore
            }

            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }

            _stop.Dispose();
        }
    }
}
