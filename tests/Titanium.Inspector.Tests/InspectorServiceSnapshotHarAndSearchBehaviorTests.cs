using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector edges: SessionSnapshot change notifications, HAR postData copy, file-log sink,
///     AppContainer listing, ReplayService body file, and process/pid search tokens.
/// </summary>
[TestClass]
public class InspectorServiceSnapshotHarAndSearchBehaviorTests
{
    // Bug class: PropertyChanged never fires for Host/ProcessId (or fires twice), so the grid Process column stays blank.
    [TestMethod]
    public void SessionSnapshot_PropertyChanged_RaisesDisplayAliases()
    {
        var changed = new List<string?>();
        var snap = new SessionSnapshot();
        snap.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        snap.ProcessId = 42;
        snap.ProcessName = "chrome";
        snap.Host = "api.example";
        snap.ContentType = "application/json; charset=utf-8";
        snap.Url = "https://api.example/x";

        CollectionAssert.Contains(changed, nameof(SessionSnapshot.ProcessId));
        CollectionAssert.Contains(changed, nameof(SessionSnapshot.ProcessDisplay));
        CollectionAssert.Contains(changed, nameof(SessionSnapshot.Host));
        CollectionAssert.Contains(changed, nameof(SessionSnapshot.ContentTypeDisplay));
        Assert.AreEqual("chrome:42", snap.ProcessDisplay);
        Assert.AreEqual("application/json", snap.ContentTypeDisplay);

        // Same value must not re-raise.
        var before = changed.Count;
        snap.ProcessId = 42;
        Assert.AreEqual(before, changed.Count);
    }

    // Bug class: request postData text is never extracted from a spilled HAR, so Copy request body is empty.
    [TestMethod]
    public void HarBodyCopy_CopyProperty_ExtractsPostDataText()
    {
        var json = """
            {"log":{"entries":[{"request":{"postData":{"mimeType":"text/plain","text":"hello-post"}},"response":{"content":{"text":"ignored"}}}}]}
            """;
        using var har = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = new MemoryStream();
        var truncated = HarBodyCopy.CopyProperty(har, "postData", "text", output, maxBytes: 1024, encoding: null);
        Assert.IsFalse(truncated);
        Assert.AreEqual("hello-post", Encoding.UTF8.GetString(output.ToArray()));
    }

    // Bug class: InspectorFileLogProvider drops Warning/Error levels or never creates a dated file for RunLogEntry.
    [TestMethod]
    public async Task InspectorFileLogProvider_WritesWarningAndErrorLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-filelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            InspectorFileLogProvider? provider = new(dir, maxBytes: 10_000_000, keep: 3, queueCapacity: 32);
            try
            {
                var logger = provider.CreateLogger("Tests");
                logger.LogWarning("warn-line");
                logger.LogError("error-line");
                logger.LogInformation("info-line");

                string? path = null;
                for (var i = 0; i < 40 && path is null; i++)
                {
                    path = provider.CurrentFilePath;
                    if (path is null)
                    {
                        await Task.Delay(50);
                    }
                }

                Assert.IsNotNull(path);
                provider.Dispose();
                provider = null;
                await Task.Delay(50);
                await using var fs = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var text = await reader.ReadToEndAsync();
                StringAssert.Contains(text, "warn-line");
                StringAssert.Contains(text, "error-line");
                StringAssert.Contains(text, "info-line");
            }
            finally
            {
                provider?.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // File handle may still be releasing.
            }
        }
    }

    // Bug class: ListContainers throws on Windows (or returns null), taking down the loopback settings page.
    [TestMethod]
    public void AppContainerLoopback_ListContainers_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("AppContainer APIs are Windows-only.");
        }

        var list = AppContainerLoopback.ListContainers();
        Assert.IsNotNull(list);
    }

    // Bug class: BodyFilePath is ignored so large replay bodies never leave the editor, or the file path throws.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ReplayService_BodyFilePath_IsSentAsRequestBody()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-replay-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bodyFile = Path.Combine(dir, "body.bin");
        await File.WriteAllBytesAsync(bodyFile, Encoding.UTF8.GetBytes("from-file"));

        using var backend = new System.Net.HttpListener();
        var port = FreePort();
        backend.Prefixes.Add($"http://127.0.0.1:{port}/");
        backend.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await backend.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        try
        {
            var result = await ReplayService.ReplayAsync(
                new SessionSnapshot { Method = "POST", Url = $"http://127.0.0.1:{port}/" },
                new ReplayRequestOptions(BodyFilePath: bodyFile));
            await serve.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual("from-file", result.ResponseBody);
        }
        finally
        {
            try
            {
                backend.Stop();
            }
            catch
            {
                // Already stopped.
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    // Bug class: process:/pid: tokens never match, so filtering by Chrome PID shows every session.
    [TestMethod]
    public void SessionSearch_ProcessAndPidTokens()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, ProcessName = "chrome", ProcessId = 100, Url = "https://a/" },
            new SessionSnapshot { Id = 2, ProcessName = "firefox", ProcessId = 200, Url = "https://b/" },
            new SessionSnapshot { Id = 3, ProcessName = "Chrome", ProcessId = 100, Url = "https://c/" },
        };
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "process:chrome").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "pid:200").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "pid:100").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "-process:chrome").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "process:firefox pid:200").Count());
    }

    // Bug class: ProgressiveDelivery.WritePacedAsync with sliceBytes<=0 throws instead of using the default slice.
    [TestMethod]
    public async Task ProgressiveDelivery_WritePaced_NonPositiveSliceUsesDefault()
    {
        var wire = Encoding.ASCII.GetBytes(new string('x', 40));
        var dest = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(
            dest, wire, chargeWireBytes: wire.Length, () => null, CancellationToken.None, sliceBytes: 0);
        CollectionAssert.AreEqual(wire, dest.ToArray());
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
