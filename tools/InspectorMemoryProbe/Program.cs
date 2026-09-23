using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

/// <summary>
/// Live memory soak: start Inspector capture proxy, drive HTTP(+optional Edge) traffic,
/// print InMemoryBodyBytes + working-set samples so spill/deselect can be judged.
/// </summary>
var seconds = 90;
var launchEdge = true;
foreach (var a in args)
{
    if (int.TryParse(a, out var s) && s > 0)
    {
        seconds = s;
    }
    else if (a is "--no-edge")
    {
        launchEdge = false;
    }
}

var cacheDir = Path.Combine(Path.GetTempPath(), "twp-mem-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cacheDir);

using var store = new SessionStore(
    new SessionStoreOptions
    {
        MaxSessionsInMemory = 10_000,
        SpillBodiesToDisk = true,
        DiskCacheMaxBytes = 2L * 1024 * 1024 * 1024,
    },
    cacheDir);

using var interception = new InterceptionService(new RecordingSystemProxyController());
interception.Capturing = true;
interception.DecryptHttps = true;
interception.SessionCaptured += (_, snap) => store.Add(snap);
interception.SessionUpdated += (_, snap) => store.NotifyUpdated(snap);

await interception.StartAsync(IPAddress.Loopback, 0);
var port = interception.BoundPort;
Console.WriteLine($"proxy=127.0.0.1:{port} cache={cacheDir} seconds={seconds}");

GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();
var baselineWs = Environment.WorkingSet;
Console.WriteLine($"t=0 sessions={store.Count} bodyBytes={store.InMemoryBodyBytes} wsMB={Mb(baselineWs)}");

Process? edge = null;
var edgeProfile = Path.Combine(Path.GetTempPath(), "twp-mem-edge-" + Guid.NewGuid().ToString("N"));
if (launchEdge && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Directory.CreateDirectory(edgeProfile);
    var edgePath = ResolveEdgePath();
    if (edgePath is not null)
    {
        edge = Process.Start(new ProcessStartInfo
        {
            FileName = edgePath,
            Arguments =
                $"--user-data-dir=\"{edgeProfile}\" --no-first-run --disable-popup-blocking " +
                $"--proxy-server=127.0.0.1:{port} --ignore-certificate-errors " +
                "https://news.ycombinator.com https://www.wikipedia.org/wiki/Main_Page https://example.com",
            UseShellExecute = false,
        });
        Console.WriteLine($"edge_pid={edge?.Id}");
    }
}

using var handler = new HttpClientHandler
{
    Proxy = new WebProxy($"http://127.0.0.1:{port}"),
    UseProxy = true,
    ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };

var urls = new[]
{
    "https://news.ycombinator.com/",
    "https://www.wikipedia.org/wiki/Main_Page",
    "https://example.com/",
    "https://httpbin.org/html",
    "https://httpbin.org/bytes/200000",
    "https://www.bbc.com/news",
    "https://github.com/",
};

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var loadTask = Task.Run(async () =>
{
    var i = 0;
    while (!cts.IsCancellationRequested)
    {
        var url = urls[i++ % urls.Length];
        try
        {
            using var resp = await http.GetAsync(url, cts.Token);
            _ = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch
        {
            // best-effort browse load
        }

        await Task.Delay(400, CancellationToken.None);
    }
}, CancellationToken.None);

var sampleTask = Task.Run(async () =>
{
    var t0 = DateTime.UtcNow;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(5000, CancellationToken.None);
        var elapsed = (int)(DateTime.UtcNow - t0).TotalSeconds;
        // Simulate user flipping selection across recent rows.
        foreach (var snap in store.Sessions.TakeLast(8).ToList())
        {
            store.PinnedSessionId = snap.Id;
            try
            {
                await store.EnsureBodiesLoadedAsync(snap, CancellationToken.None);
            }
            catch
            {
                // ignore
            }

            store.PinnedSessionId = null;
        }

        await store.FlushSpillAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine(
            $"t={elapsed} sessions={store.Count} spilled={store.SpilledCount} " +
            $"bodyBytes={store.InMemoryBodyBytes} bodyMB={store.InMemoryBodyBytes / (1024.0 * 1024):F2} " +
            $"wsMB={Mb(Environment.WorkingSet)} deltaWsMB={Mb(Environment.WorkingSet - baselineWs)}");
    }
}, CancellationToken.None);

try
{
    await Task.Delay(TimeSpan.FromSeconds(seconds));
}
finally
{
    cts.Cancel();
    try { await loadTask; } catch { /* ignore */ }
    try { await sampleTask; } catch { /* ignore */ }
}

await store.FlushSpillAsync(TimeSpan.FromSeconds(10));
store.PinnedSessionId = null;
GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true);

Console.WriteLine(
    $"DONE sessions={store.Count} spilled={store.SpilledCount} " +
    $"bodyBytes={store.InMemoryBodyBytes} bodyMB={store.InMemoryBodyBytes / (1024.0 * 1024):F2} " +
    $"wsMB={Mb(Environment.WorkingSet)} deltaWsMB={Mb(Environment.WorkingSet - baselineWs)}");

try { edge?.Kill(entireProcessTree: true); } catch { /* ignore */ }
interception.Stop();
try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore */ }
try { Directory.Delete(edgeProfile, recursive: true); } catch { /* ignore */ }

// Soft gate: payload RAM must be small; WS growth is noisy but should not look like all bodies retained.
var bodyOk = store.InMemoryBodyBytes < 4L * 1024 * 1024;
var wsOk = Environment.WorkingSet - baselineWs < 350L * 1024 * 1024;
Console.WriteLine($"PASS_BODY={bodyOk} PASS_WS_GROWTH={wsOk}");
return bodyOk ? 0 : 2;

static double Mb(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 1);

static string? ResolveEdgePath()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft", "Edge", "Application", "msedge.exe"),
    };
    return candidates.FirstOrDefault(File.Exists);
}
