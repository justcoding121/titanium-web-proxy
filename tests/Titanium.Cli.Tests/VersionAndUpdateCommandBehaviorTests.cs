using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Http3;
using Titanium.Cli.Updates;

namespace Titanium.Cli.Tests;

/// <summary>
///     In-process `titanium version` / `titanium update` behavior against a loopback update feed: the operator must
///     get the right exit code and message for every feed outcome, and a bad download must never touch the install.
/// </summary>
[TestClass]
[DoNotParallelize]
public class VersionAndUpdateCommandBehaviorTests
{
    private const string FeedEnv = "TITANIUM_UPDATE_FEED";
    private const string ChannelEnv = "TITANIUM_UPDATE_CHANNEL";

    private string? _previousFeed;
    private string? _previousChannel;

    [TestInitialize]
    public void Init()
    {
        _previousFeed = Environment.GetEnvironmentVariable(FeedEnv);
        _previousChannel = Environment.GetEnvironmentVariable(ChannelEnv);
        Environment.SetEnvironmentVariable(ChannelEnv, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(FeedEnv, _previousFeed);
        Environment.SetEnvironmentVariable(ChannelEnv, _previousChannel);
    }

    // Bug class: `version` without --check hits the network or exits non-zero; component versions go missing.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Plain_PrintsComponentVersions_WithoutQueryingTheFeed()
    {
        Environment.SetEnvironmentVariable(FeedEnv, "http://127.0.0.1:1/never-called");

        var (code, stdout, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version"]));

        Assert.AreEqual(0, code, stderr);
        StringAssert.Contains(stdout, "Cli: ");
        StringAssert.Contains(stdout, "Core: ");
        StringAssert.Contains(stdout, "Abstractions: ");
        StringAssert.Contains(stdout, "Configuration: ");
        Assert.AreEqual(string.Empty, stderr);
    }

    // Bug class: `version --help` falls through and runs the command (or exits 1).
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Help_PrintsUsageAndExitsZero()
    {
        var (code, stdout, _) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--help"]));

        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout, "titanium version [--check]");
        StringAssert.Contains(stdout, "Exit codes with --check");
    }

    // Bug class: a typo in --channel silently falls back to stable and offers the wrong release.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_UnknownChannel_FailsWithTheOffendingValue()
    {
        var (code, _, stderr) = await RunAsync(
            () => VersionCommand.ExecuteAsync(["version", "--channel", "nightly"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Unknown channel 'nightly'");
    }

    // Bug class: an empty feed variable (air-gapped opt-out) still goes to the internet.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_WithFeedDisabled_ReportsItAndExitsOne()
    {
        Environment.SetEnvironmentVariable(FeedEnv, string.Empty);

        var (code, _, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--check"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Update feed disabled");
    }

    // Bug class: an available update is reported with exit 0, so scripts never notice it (contract: exit 2).
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_WhenFeedIsAhead_ExitsTwoAndNamesTheUpgradeCommand()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"99.0.0","channel":"stable"}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--check"]));

        Assert.AreEqual(2, code, stderr);
        StringAssert.Contains(stdout, "Remote Cli (stable): 99.0.0");
        StringAssert.Contains(stdout, "A newer Cli build is available");
        StringAssert.Contains(stdout, "titanium update --channel stable");
    }

    // Bug class: a local build newer than the feed is told to "update" (a downgrade).
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_WhenLocalIsNewerThanFeed_ExitsZeroWithoutOfferingADowngrade()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"0.0.1"}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--check"]));

        Assert.AreEqual(0, code, stderr);
        StringAssert.Contains(stdout, "is newer than stable 0.0.1");
        Assert.IsFalse(stdout.Contains("A newer Cli build", StringComparison.Ordinal), stdout);
    }

    // Bug class: --channel beta ignored by the feed query / printed as stable.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_BetaChannel_IsEchoedInTheReport()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"99.0.0-beta.1","channel":"beta"}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(
            () => VersionCommand.ExecuteAsync(["version", "--check", "--channel", "beta"]));

        Assert.AreEqual(2, code, stderr);
        StringAssert.Contains(stdout, "(beta)");
        StringAssert.Contains(stdout, "titanium update --channel beta");
    }

    // Bug class: the env-var channel is read after the flag check and ignored, or an invalid env value is accepted.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_ChannelFromEnvironment_IsHonoredAndValidated()
    {
        Environment.SetEnvironmentVariable(ChannelEnv, "gamma");

        var (code, _, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Unknown channel 'gamma'");
    }

    // Bug class: --plus --check on a machine without Plus reports "up to date" instead of exit 2.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_WithPlus_ReportsPlusState()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"0.0.1","products":{"plus":{"version":"99.0.0"}}}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(
            () => VersionCommand.ExecuteAsync(["version", "--check", "--plus"]));

        if (VersionCommand.TryGetLocalPlusVersion() is null)
        {
            Assert.AreEqual(2, code, stderr);
            StringAssert.Contains(stdout, "Plus is not installed");
            StringAssert.Contains(stdout, "titanium update --plus");
        }
        else
        {
            // A Plus DLL happens to sit beside the test host; the remote 99.0.0 must be offered.
            Assert.AreEqual(2, code, stderr);
            StringAssert.Contains(stdout, "A newer Plus build is available");
        }
    }

    // Bug class: a server error is swallowed and `--check` reports success.
    [TestMethod]
    [Timeout(30000)]
    public async Task Version_Check_WhenFeedReturnsServerError_ExitsOneWithNetworkError()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Respond("/feed.json", 500, "text/plain", "boom");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--check"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Update feed network error");
    }

    // Bug class: a malformed or null manifest crashes with a stack trace instead of a one-line error.
    [TestMethod]
    [DataRow("{not json", "Update feed JSON error")]
    [DataRow("null", "Update feed returned invalid JSON")]
    [Timeout(30000)]
    public async Task Version_Check_WhenFeedBodyIsInvalid_ExitsOneWithAClearError(string body, string expected)
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", body);
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => VersionCommand.ExecuteAsync(["version", "--check"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, expected);
    }

    // Bug class: manifest JSON keys are matched case-sensitively, so a hand-written feed loses its assets.
    [TestMethod]
    [Timeout(30000)]
    public async Task FeedClient_ParsesManifestCaseInsensitively()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json(
            "/feed.json",
            """
            {"VERSION":"8.1.0","Products":{"CLI":{"assets":{"win-x64":{"url":"http://x/cli.zip","sha256":"ab"}}},
             "plus":{"version":"8.1.1","bundle":{"url":"http://x/b.zip","size":12}}}}
            """);
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var manifest = await new UpdateFeedClient("stable").TryGetManifestAsync();

        Assert.IsNotNull(manifest);
        Assert.AreEqual("8.1.0", manifest.Version);
        Assert.AreEqual("http://x/cli.zip", manifest.Products!.Cli!.Assets!["win-x64"].Url);
        Assert.AreEqual("8.1.1", manifest.Products.Plus!.Version);
        Assert.AreEqual(12, manifest.Products.Plus.Bundle!.Size);
    }

    // Bug class: `update -h` starts an update instead of printing usage.
    [TestMethod]
    [Timeout(30000)]
    public async Task Update_Help_PrintsUsageAndExitsZero()
    {
        var (code, stdout, _) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "-h"]));

        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout, "titanium update [--plus]");
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Update_UnknownChannel_FailsBeforeQueryingTheFeed()
    {
        Environment.SetEnvironmentVariable(FeedEnv, "http://127.0.0.1:1/never-called");

        var (code, _, stderr) = await RunAsync(
            () => UpdateCommand.ExecuteAsync(["update", "--channel", "canary"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Unknown channel 'canary'");
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Update_WithFeedDisabled_ExitsOne()
    {
        Environment.SetEnvironmentVariable(FeedEnv, string.Empty);

        var (code, stdout, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Update feed disabled");
        StringAssert.Contains(stdout, "Checking for updates (stable)");
    }

    // Bug class: an up-to-date or newer local CLI is "updated" anyway (downgrade / needless restart).
    [TestMethod]
    [Timeout(30000)]
    public async Task Update_WhenLocalIsNewerThanFeed_ChangesNothingAndExitsZero()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"0.0.1"}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update"]));

        Assert.AreEqual(0, code, stderr);
        StringAssert.Contains(stdout, "is newer than stable 0.0.1. No changes.");
        Assert.IsFalse(stdout.Contains("Downloading", StringComparison.Ordinal), stdout);
    }

    // Bug class: a feed that is ahead but has no package for this platform proceeds to download "null".
    [TestMethod]
    [Timeout(30000)]
    public async Task Update_WhenFeedHasNoPackageForThisRid_ExitsOneNamingTheRid()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", """{"version":"99.0.0","products":{"cli":{"assets":{}}}}""");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "No CLI package for RID " + Http3DepsCommand.SuggestRid());
        StringAssert.Contains(stderr, "remote 99.0.0");
    }

    // Bug class: a corrupted or tampered CLI zip is accepted because the checksum is not enforced.
    [TestMethod]
    [Timeout(30000)]
    public async Task Update_WhenCliDownloadChecksumDiffers_AbortsWithoutInstalling()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Bytes("/cli.zip", "application/zip", Encoding.UTF8.GetBytes("not the advertised zip"));
        feed.Json("/feed.json", ManifestWithCli(feed.Url("/cli.zip"), new string('0', 64)));
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "SHA256 mismatch — aborting update.");
        StringAssert.Contains(stdout, "Verifying SHA256");
        Assert.IsFalse(stdout.Contains("Restarting updater", StringComparison.Ordinal), stdout);
    }

    // Bug class: a 404 on the asset URL surfaces as an unhandled exception.
    [TestMethod]
    [Timeout(30000)]
    public async Task Update_WhenCliAssetIsMissing_ReportsDownloadFailure()
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", ManifestWithCli(feed.Url("/missing.zip"), null));
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Download failed");
    }

    // Bug class: `update --plus` against a feed without Plus tries to install nothing and exits 0.
    [TestMethod]
    [DataRow("""{"version":"99.0.0"}""")]
    [DataRow("""{"version":"99.0.0","products":{"plus":{"version":"99.0.0"}}}""")]
    [Timeout(30000)]
    public async Task UpdatePlus_WhenFeedHasNoPlusPackage_ExitsOne(string manifest)
    {
        await using var feed = await FeedServer.StartAsync();
        feed.Json("/feed.json", manifest);
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "--plus"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "No Plus package on channel stable");
        StringAssert.Contains(stdout, "Checking Plus updates (stable)");
    }

    // Bug class: a Plus bundle with a wrong checksum is extracted next to the CLI.
    [TestMethod]
    [Timeout(30000)]
    public async Task UpdatePlus_WhenBundleChecksumDiffers_AbortsAndLeavesNoPlusFiles()
    {
        if (VersionCommand.TryGetLocalPlusVersion() is not null)
        {
            Assert.Inconclusive("A Plus DLL sits beside the test host; the install path is not exercised here.");
        }

        var bytes = Encoding.UTF8.GetBytes("bundle bytes");
        await using var feed = await FeedServer.StartAsync();
        feed.Bytes("/plus.zip", "application/zip", bytes);
        feed.Json("/feed.json", ManifestWithPlusBundle(feed.Url("/plus.zip"), new string('f', 64), bytes.Length));
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, stdout, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "--plus"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "SHA256 mismatch — aborting Plus update.");
        StringAssert.Contains(stdout, "Installing Plus 99.0.0 (stable)");
        AssertNoPlusLeftovers();
    }

    // Bug class: a size mismatch (truncated transfer with matching sha not required) installs a partial bundle.
    [TestMethod]
    [Timeout(30000)]
    public async Task UpdatePlus_WhenBundleSizeDiffers_AbortsAndLeavesNoPlusFiles()
    {
        if (VersionCommand.TryGetLocalPlusVersion() is not null)
        {
            Assert.Inconclusive("A Plus DLL sits beside the test host; the install path is not exercised here.");
        }

        var bytes = Encoding.UTF8.GetBytes("bundle bytes");
        await using var feed = await FeedServer.StartAsync();
        feed.Bytes("/plus.zip", "application/zip", bytes);
        feed.Json("/feed.json", ManifestWithPlusBundle(feed.Url("/plus.zip"), Sha(bytes), bytes.Length + 7));
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "--plus"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Plus download size mismatch");
        AssertNoPlusLeftovers();
    }

    // Bug class: a bundle that passes checksum/size but is not a zip leaves the install half written.
    [TestMethod]
    [Timeout(30000)]
    public async Task UpdatePlus_WhenBundleIsNotAZip_FailsAndLeavesNoPlusFiles()
    {
        if (VersionCommand.TryGetLocalPlusVersion() is not null)
        {
            Assert.Inconclusive("A Plus DLL sits beside the test host; the install path is not exercised here.");
        }

        var bytes = Encoding.UTF8.GetBytes("definitely not a zip archive");
        await using var feed = await FeedServer.StartAsync();
        feed.Bytes("/plus.zip", "application/zip", bytes);
        feed.Json("/feed.json", ManifestWithPlusBundle(feed.Url("/plus.zip"), Sha(bytes), bytes.Length));
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "--plus"]));

        Assert.AreNotEqual(0, code, stderr);
        Assert.IsFalse(string.IsNullOrWhiteSpace(stderr), "the failure must be explained to the operator");
        AssertNoPlusLeftovers();
    }

    // Bug class: the legacy single-DLL asset skips the checksum that the bundle path enforces.
    [TestMethod]
    [Timeout(30000)]
    public async Task UpdatePlus_LegacyAssetChecksumMismatch_AbortsAndLeavesNoPlusFiles()
    {
        if (VersionCommand.TryGetLocalPlusVersion() is not null)
        {
            Assert.Inconclusive("A Plus DLL sits beside the test host; the install path is not exercised here.");
        }

        await using var feed = await FeedServer.StartAsync();
        feed.Bytes("/Titanium.Plus.dll", "application/octet-stream", [1, 2, 3, 4]);
        feed.Json(
            "/feed.json",
            "{\"version\":\"99.0.0\",\"products\":{\"plus\":{\"version\":\"99.0.0\",\"asset\":{\"url\":\""
            + feed.Url("/Titanium.Plus.dll") + "\",\"sha256\":\"" + new string('1', 64) + "\"}}}}");
        Environment.SetEnvironmentVariable(FeedEnv, feed.Url("/feed.json"));

        var (code, _, stderr) = await RunAsync(() => UpdateCommand.ExecuteAsync(["update", "--plus"]));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "SHA256 mismatch — aborting Plus update.");
        AssertNoPlusLeftovers();
    }

    // Bug class: informational versions keep the +commit metadata, so labels never match release tags.
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("   ", null)]
    [DataRow("+abc123", null)]
    [DataRow("8.1.0+abc123", "8.1.0")]
    [DataRow(" 8.1.0-beta.2 ", "8.1.0-beta.2")]
    public void FormatInformationalVersion_StripsBuildMetadata(string? input, string? expected) =>
        Assert.AreEqual(expected, VersionCommand.FormatInformationalVersion(input));

    private static void AssertNoPlusLeftovers()
    {
        Assert.IsFalse(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll")),
            "a refused update must not install Plus");
        Assert.AreEqual(
            0,
            Directory.GetDirectories(AppContext.BaseDirectory, ".plus-download-*").Length,
            "the download scratch folder must be removed");
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ManifestWithCli(string url, string? sha)
    {
        var shaPart = sha is null ? string.Empty : ",\"sha256\":\"" + sha + "\"";
        return "{\"version\":\"99.0.0\",\"products\":{\"cli\":{\"assets\":{\""
               + Http3DepsCommand.SuggestRid() + "\":{\"url\":\"" + url + "\"" + shaPart + "}}}}}";
    }

    private static string ManifestWithPlusBundle(string url, string sha, long size) =>
        "{\"version\":\"99.0.0\",\"products\":{\"plus\":{\"version\":\"99.0.0\",\"bundle\":{\"url\":\""
        + url + "\",\"sha256\":\"" + sha + "\",\"size\":" + size + "}}}}";

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(Func<Task<int>> action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Synchronized(stdout));
            Console.SetError(TextWriter.Synchronized(stderr));
            var code = await action();
            await AsyncConsole.FlushAsync();
            lock (stdout)
            {
                lock (stderr)
                {
                    return (code, stdout.ToString(), stderr.ToString());
                }
            }
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    /// <summary>Tiny loopback HTTP/1.1 server: one canned response per path, 404 otherwise.</summary>
    private sealed class FeedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, (int Status, string Type, byte[] Body)> _routes = new();
        private Task _loop = Task.CompletedTask;

        private FeedServer(TcpListener listener) => _listener = listener;

        public static Task<FeedServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new FeedServer(listener);
            server._loop = Task.Run(server.AcceptLoopAsync);
            return Task.FromResult(server);
        }

        public string Url(string path) =>
            "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + path;

        public void Json(string path, string body) => Respond(path, 200, "application/json", body);

        public void Respond(string path, int status, string type, string body) =>
            Bytes(path, type, Encoding.UTF8.GetBytes(body), status);

        public void Bytes(string path, string type, byte[] body, int status = 200)
        {
            lock (_routes)
            {
                _routes[path] = (status, type, body);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch
            {
                // Listener torn down while accepting.
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new StringBuilder();
                    var buffer = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(buffer, _cts.Token) == 0)
                        {
                            return;
                        }

                        header.Append((char)buffer[0]);
                    }

                    var path = header.ToString().Split(' ')[1];
                    (int Status, string Type, byte[] Body) route;
                    lock (_routes)
                    {
                        route = _routes.TryGetValue(path, out var found) ? found : (404, "text/plain", []);
                    }

                    var head = $"HTTP/1.1 {route.Status} X\r\nContent-Type: {route.Type}\r\n"
                               + $"Content-Length: {route.Body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
                    await stream.WriteAsync(route.Body, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch
                {
                    // Client went away; nothing to report in a test fixture.
                }
            }
        }
    }
}
