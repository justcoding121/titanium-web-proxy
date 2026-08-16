// Measures a single cold page load in a real Chrome, driven over the DevTools Protocol.
//
// One invocation == one measurement: a fresh Chrome profile is created, one URL is navigated,
// the browser's own Navigation Timing and Resource Timing are read back, and Chrome is killed.
// A fresh profile per run means no HTTP cache, no warm sockets and no prior Alt-Svc knowledge,
// which is the cold-start case we care about.
//
// The point of reading Resource Timing (not just the load event) is that a proxy stall does not
// necessarily move the load event much: it shows up as a handful of subresources sitting for
// seconds while the rest of the page finishes normally. res_over1s / res_over3s count those.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Read back inside the page: Navigation Timing for the document, Resource Timing for the tail.
// All values are milliseconds relative to navigationStart.
const string TimingScript =
    """
    (() => {
      const nav = performance.getEntriesByType('navigation')[0];
      const resources = performance.getEntriesByType('resource');
      const durations = resources.map(r => r.duration).sort((a, b) => a - b);
      const quantile = q => durations.length === 0
        ? 0
        : durations[Math.min(durations.length - 1, Math.floor(q * durations.length))];
      return JSON.stringify({
        ttfb: nav ? nav.responseStart : 0,
        dcl: nav ? nav.domContentLoadedEventEnd : 0,
        load: nav ? nav.loadEventEnd : 0,
        resCount: resources.length,
        resP50: quantile(0.5),
        resP95: quantile(0.95),
        resMax: durations.length === 0 ? 0 : durations[durations.length - 1],
        resOver1s: durations.filter(d => d > 1000).length,
        resOver3s: durations.filter(d => d > 3000).length
      });
    })()
    """;

var options = ProbeOptions.Parse(args);
if (options is null)
{
    await ProbeOptions.PrintUsageAsync();
    return 2;
}

var result = await RunAsync(options);

await Console.Out.WriteLineAsync(
    $"{options.Arm,-12} {options.Site,-34} trial={options.Trial} ok={result.Ok,-5} " +
    $"wall_ms={result.WallMs,7:F0} ttfb_ms={result.TtfbMs,7:F0} load_ms={result.LoadMs,7:F0} " +
    $"res={result.ResourceCount,3} >1s={result.ResourcesOver1S,2} >3s={result.ResourcesOver3S,2} " +
    $"res_max_ms={result.ResourceMaxMs,7:F0}{(result.Error is null ? "" : "  err=" + result.Error)}");

if (options.CsvPath is not null)
    AppendCsv(options, result);

return result.Ok ? 0 : 1;

async Task<ProbeResult> RunAsync(ProbeOptions opt)
{
    var userDataDir = Path.Combine(
        Path.GetTempPath(), "chrome-load-probe", Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(userDataDir);

    Process? chrome = null;
    try
    {
        chrome = LaunchChrome(opt, userDataDir);

        var browserWsUrl = await WaitForDevToolsAsync(userDataDir, chrome, TimeSpan.FromSeconds(30));
        using var cdp = await CdpConnection.ConnectAsync(browserWsUrl);

        // Chrome is talking to a MITM proxy presenting generated leaf certificates. The command-line
        // flag alone is unreliable across Chrome versions, so the CDP override is applied as well.
        var targetId = (await cdp.SendAsync("Target.createTarget", new JsonObject
        {
            ["url"] = "about:blank"
        }))?["targetId"]?.GetValue<string>() ?? throw new InvalidOperationException("no targetId");

        var sessionId = (await cdp.SendAsync("Target.attachToTarget", new JsonObject
        {
            ["targetId"] = targetId,
            ["flatten"] = true
        }))?["sessionId"]?.GetValue<string>() ?? throw new InvalidOperationException("no sessionId");

        await cdp.SendAsync("Security.enable", new JsonObject(), sessionId);
        await cdp.SendAsync("Security.setIgnoreCertificateErrors",
            new JsonObject { ["ignore"] = true }, sessionId);
        await cdp.SendAsync("Page.enable", new JsonObject(), sessionId);
        await cdp.SendAsync("Network.enable", new JsonObject(), sessionId);

        // Diagnostic: isolate document TTFB from H3 multiplexing by blocking common subresources.
        if (string.Equals(Environment.GetEnvironmentVariable("TWP_BLOCK_SUBRESOURCES"), "1",
                StringComparison.Ordinal))
        {
            await cdp.SendAsync("Network.setBlockedURLs", new JsonObject
            {
                ["urls"] = new JsonArray(
                    "*.js", "*.css", "*.svg", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp",
                    "*.woff", "*.woff2", "*.ttf", "*google*", "*doubleclick*", "*facebook*",
                    "*googletagmanager*", "*cloudflareinsights*")
            }, sessionId);
        }

        var loadFired = cdp.WaitForEventAsync("Page.loadEventFired", sessionId);

        var sw = Stopwatch.StartNew();
        var navigate = await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = opt.Url }, sessionId);
        if (navigate?["errorText"] is { } navError)
            return ProbeResult.Failed($"navigate: {navError.GetValue<string>()}");

        var completed = await Task.WhenAny(loadFired, Task.Delay(opt.TimeoutMs));
        var timedOut = completed != loadFired;
        var wallMs = sw.Elapsed.TotalMilliseconds;

        // Resource entries for requests that finish just after the load event would otherwise be
        // missed; a short settle window catches them without materially changing the timings read
        // from Navigation Timing, which are anchored to navigationStart rather than to "now".
        await Task.Delay(opt.SettleMs);

        var evaluated = await cdp.SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = TimingScript,
            ["returnByValue"] = true,
            ["awaitPromise"] = false
        }, sessionId);

        var json = evaluated?["result"]?["value"]?.GetValue<string>();
        if (json is null)
            return ProbeResult.Failed(timedOut ? "load timeout, no timing" : "no timing");

        var timing = JsonNode.Parse(json)!;
        double Get(string name) => timing[name]?.GetValue<double>() ?? 0;

        return new ProbeResult(
            Ok: !timedOut,
            WallMs: wallMs,
            TtfbMs: Get("ttfb"),
            DclMs: Get("dcl"),
            LoadMs: Get("load"),
            ResourceCount: (int)Get("resCount"),
            ResourceP50Ms: Get("resP50"),
            ResourceP95Ms: Get("resP95"),
            ResourceMaxMs: Get("resMax"),
            ResourcesOver1S: (int)Get("resOver1s"),
            ResourcesOver3S: (int)Get("resOver3s"),
            Error: timedOut ? "load timeout" : null);
    }
    catch (Exception ex)
    {
        return ProbeResult.Failed(ex.Message.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' '));
    }
    finally
    {
        TryKill(chrome);
        TryDeleteDirectory(userDataDir);
    }
}

static Process LaunchChrome(ProbeOptions opt, string userDataDir)
{
    var args = new List<string>
    {
        // Port 0 makes Chrome pick a free port and write it to DevToolsActivePort, so concurrent or
        // back-to-back runs cannot collide on a hard-coded port.
        "--remote-debugging-port=0",
        $"--user-data-dir={userDataDir}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-client-side-phishing-detection",
        "--disable-sync",
        "--disable-extensions",
        "--no-service-autorun",
        "--metrics-recording-only",
        "--password-store=basic",
        "--window-size=1366,900"
    };

    if (opt.Headless)
        args.Add("--headless=new");

    if (opt.ProxyServer is { } proxy)
    {
        args.Add($"--proxy-server={proxy}");
        args.Add("--ignore-certificate-errors");
    }
    else
    {
        // Explicit, so the direct arm is unaffected by whatever the machine's WinINet/PAC settings
        // happen to be at the time -- including a system proxy left behind by an earlier proxy run.
        args.Add("--no-proxy-server");
    }

    var psi = new ProcessStartInfo(opt.ChromePath)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };

    foreach (var arg in args)
        psi.ArgumentList.Add(arg);

    psi.ArgumentList.Add("about:blank");

    var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start Chrome");
    _ = process.StandardError.ReadToEndAsync();
    _ = process.StandardOutput.ReadToEndAsync();
    return process;
}

static async Task<string> WaitForDevToolsAsync(string userDataDir, Process chrome, TimeSpan timeout)
{
    var portFile = Path.Combine(userDataDir, "DevToolsActivePort");
    var deadline = DateTime.UtcNow + timeout;

    while (DateTime.UtcNow < deadline)
    {
        if (chrome.HasExited)
            throw new InvalidOperationException($"Chrome exited early with code {chrome.ExitCode}");

        if (File.Exists(portFile))
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(portFile);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                    return $"ws://127.0.0.1:{port}{lines[1]}";
            }
            catch (IOException)
            {
                // Chrome is mid-write; retry.
            }
        }

        await Task.Delay(50);
    }

    throw new TimeoutException("Chrome did not expose a DevTools endpoint");
}

static void TryKill(Process? process)
{
    if (process is null)
        return;

    try
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
    catch
    {
        // Best effort: a probe run must not fail because teardown raced with Chrome exiting.
    }
    finally
    {
        process.Dispose();
    }
}

static void TryDeleteDirectory(string path)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return;
        }
        catch
        {
            Thread.Sleep(200);
        }
    }
}

static void AppendCsv(ProbeOptions opt, ProbeResult r)
{
    var directory = Path.GetDirectoryName(opt.CsvPath!);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    if (!File.Exists(opt.CsvPath!))
    {
        File.WriteAllText(opt.CsvPath!,
            "timestamp,arm,site,trial,ok,wall_ms,ttfb_ms,dcl_ms,load_ms," +
            "res_count,res_p50_ms,res_p95_ms,res_max_ms,res_over1s,res_over3s,error\n");
    }

    File.AppendAllText(opt.CsvPath!,
        $"{DateTime.UtcNow:o},{opt.Arm},{opt.Site},{opt.Trial},{(r.Ok ? 1 : 0)}," +
        $"{r.WallMs:F1},{r.TtfbMs:F1},{r.DclMs:F1},{r.LoadMs:F1}," +
        $"{r.ResourceCount},{r.ResourceP50Ms:F1},{r.ResourceP95Ms:F1},{r.ResourceMaxMs:F1}," +
        $"{r.ResourcesOver1S},{r.ResourcesOver3S},{r.Error}\n");
}

internal readonly record struct ProbeResult(
    bool Ok,
    double WallMs,
    double TtfbMs,
    double DclMs,
    double LoadMs,
    int ResourceCount,
    double ResourceP50Ms,
    double ResourceP95Ms,
    double ResourceMaxMs,
    int ResourcesOver1S,
    int ResourcesOver3S,
    string? Error)
{
    public static ProbeResult Failed(string error) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, error);
}

internal sealed record ProbeOptions(
    string Url,
    string ChromePath,
    string? ProxyServer,
    string Arm,
    string Site,
    int Trial,
    int TimeoutMs,
    int SettleMs,
    bool Headless,
    string? CsvPath)
{
    public static ProbeOptions? Parse(string[] args)
    {
        string? url = null, chrome = null, proxy = null, csv = null, arm = null;
        int trial = 0, timeout = 45000, settle = 750;
        var headless = true;

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--url": url = next; i++; break;
                case "--chrome": chrome = next; i++; break;
                case "--proxy": proxy = next; i++; break;
                case "--arm": arm = next; i++; break;
                case "--trial": trial = int.Parse(next!); i++; break;
                case "--timeout-ms": timeout = int.Parse(next!); i++; break;
                case "--settle-ms": settle = int.Parse(next!); i++; break;
                case "--csv": csv = next; i++; break;
                case "--headful": headless = false; break;
                default: return null;
            }
        }

        if (url is null)
            return null;

        chrome ??= @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (!File.Exists(chrome))
            throw new FileNotFoundException($"Chrome not found at {chrome}; pass --chrome");

        return new ProbeOptions(
            url, chrome, proxy, arm ?? (proxy is null ? "direct" : "proxy"),
            new Uri(url).Host, trial, timeout, settle, headless, csv);
    }

    public static Task PrintUsageAsync() =>
        Console.Error.WriteLineAsync(
            """
            Usage: ChromeLoadProbe --url <url> [options]

              --url <url>          Page to load (required).
              --chrome <path>      Chrome executable. Defaults to the standard install path.
              --proxy <host:port>  Route Chrome through this proxy. Omitted => direct (--no-proxy-server).
              --arm <name>         Label recorded in the CSV. Defaults to "direct" / "proxy".
              --trial <n>          Trial number recorded in the CSV.
              --timeout-ms <n>     Load-event timeout. Default 45000.
              --settle-ms <n>      Wait after load before reading Resource Timing. Default 750.
              --csv <path>         Append one row per run, creating the header if needed.
              --headful            Launch a visible window instead of headless.
            """);
}

/// <summary>
///     Minimal DevTools Protocol client: one WebSocket, one receive pump, responses matched by id and
///     events awaited by name. Enough to drive a navigation; not a general-purpose CDP library.
/// </summary>
internal sealed class CdpConnection : IDisposable
{
    private readonly ClientWebSocket socket;
    private readonly CancellationTokenSource cts = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pending = [];
    private readonly List<(string Method, string? SessionId, TaskCompletionSource<JsonNode?> Tcs)> waiters = [];
    private readonly Lock gate = new();
    private int nextId;

    private CdpConnection(ClientWebSocket socket)
    {
        this.socket = socket;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<CdpConnection> ConnectAsync(string url)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), CancellationToken.None);
        return new CdpConnection(socket);
    }

    public async Task<JsonNode?> SendAsync(string method, JsonObject parameters, string? sessionId = null)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (gate)
            pending[id] = tcs;

        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

        if (sessionId is not null)
            message["sessionId"] = sessionId;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public Task<JsonNode?> WaitForEventAsync(string method, string? sessionId = null)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            waiters.Add((method, sessionId, tcs));
        return tcs.Task;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var accumulated = new MemoryStream();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, cts.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;

                accumulated.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;

                var text = Encoding.UTF8.GetString(accumulated.ToArray());
                accumulated.SetLength(0);
                Dispatch(text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private void Dispatch(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is null)
            return;

        if (node["id"]?.GetValue<int>() is { } id)
        {
            TaskCompletionSource<JsonNode?>? tcs;
            lock (gate)
            {
                pending.Remove(id, out tcs);
            }

            if (node["error"] is { } error)
                tcs?.TrySetException(new InvalidOperationException(error.ToJsonString()));
            else
                tcs?.TrySetResult(node["result"]);
            return;
        }

        if (node["method"]?.GetValue<string>() is not { } method)
            return;

        var sessionId = node["sessionId"]?.GetValue<string>();
        List<TaskCompletionSource<JsonNode?>> matched = [];

        lock (gate)
        {
            for (var i = waiters.Count - 1; i >= 0; i--)
            {
                var waiter = waiters[i];
                if (waiter.Method != method)
                    continue;
                if (waiter.SessionId is not null && waiter.SessionId != sessionId)
                    continue;

                matched.Add(waiter.Tcs);
                waiters.RemoveAt(i);
            }
        }

        foreach (var tcs in matched)
            tcs.TrySetResult(node["params"]);
    }

    public void Dispose()
    {
        cts.Cancel();
        socket.Dispose();
        cts.Dispose();
    }
}
