using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Spawns origin and proxy as separate OS processes so the load generator (parent)
/// does not steal CPU from the proxy under test.
/// </summary>
internal sealed class ChildProcessStack : IAsyncDisposable
{
    private readonly Process? originProcess;
    private readonly Process proxyProcess;
    private readonly StreamReader originStdout;
    private readonly StreamReader proxyStdout;

    public Uri TargetUri { get; }
    public string TargetUrl => TargetUri.ToString();
    public string? ExplicitProxyUrl { get; }
    public string? NginxVersion { get; }

    private ChildProcessStack(Process? originProcess, StreamReader originStdout, Process proxyProcess,
        StreamReader proxyStdout, Uri targetUri, string? explicitProxyUrl, string? nginxVersion)
    {
        this.originProcess = originProcess;
        this.originStdout = originStdout;
        this.proxyProcess = proxyProcess;
        this.proxyStdout = proxyStdout;
        TargetUri = targetUri;
        ExplicitProxyUrl = explicitProxyUrl;
        NginxVersion = nginxVersion;
    }

    public static async Task<ChildProcessStack> StartAsync(string arm, string? nginxPath,
        CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot locate current process path for child spawn.");

        // HTTPS MITM needs a shared in-process CA between origin and proxy, so that arm uses a
        // single --serve child. Reverse-HTTP/1 arms keep a 3-process split (origin / proxy / load).
        if (arm == "twp-https-mitm")
            return await StartCombinedServeAsync(exe, "https-mitm", nginxPath, cancellationToken);

        var originArgs = "--serve-origin";
        var origin = StartChild(exe, originArgs);
        var originLines = await ReadUntilReadyAsync(origin, cancellationToken);
        var originHttp = Require(originLines, "origin_http");
        var originHttpPort = new Uri(originHttp).Port;

        var mode = arm switch
        {
            "twp-reverse-http1" => "reverse-http1",
            "nginx-reverse-http1" => "nginx-reverse-http1",
            _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, null)
        };

        var proxyArgs = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"--serve-proxy --mode {mode} --origin-http-port {originHttpPort}");
        if (!string.IsNullOrWhiteSpace(nginxPath))
            proxyArgs.Append(CultureInfo.InvariantCulture, $" --nginx-path \"{nginxPath}\"");

        var proxy = StartChild(exe, proxyArgs.ToString());
        Dictionary<string, string> proxyLines;
        try
        {
            proxyLines = await ReadUntilReadyAsync(proxy, cancellationToken);
        }
        catch
        {
            TryKill(proxy);
            TryKill(origin);
            throw;
        }

        var target = Require(proxyLines, "target_for_client");
        proxyLines.TryGetValue("explicit_proxy", out var explicitProxy);
        proxyLines.TryGetValue("nginx", out var nginxVersion);

        return new ChildProcessStack(origin, origin.StandardOutput, proxy, proxy.StandardOutput,
            new Uri(target), string.IsNullOrWhiteSpace(explicitProxy) ? null : explicitProxy, nginxVersion);
    }

    private static async Task<ChildProcessStack> StartCombinedServeAsync(string exe, string mode, string? nginxPath,
        CancellationToken cancellationToken)
    {
        var args = new StringBuilder().Append(CultureInfo.InvariantCulture, $"--serve --mode {mode}");
        if (!string.IsNullOrWhiteSpace(nginxPath))
            args.Append(CultureInfo.InvariantCulture, $" --nginx-path \"{nginxPath}\"");

        var serve = StartChild(exe, args.ToString());
        Dictionary<string, string> lines;
        try
        {
            lines = await ReadUntilReadyAsync(serve, cancellationToken);
        }
        catch
        {
            TryKill(serve);
            throw;
        }

        var target = Require(lines, "target_for_client");
        lines.TryGetValue("explicit_proxy", out var explicitProxy);
        return new ChildProcessStack(null, serve.StandardOutput, serve, serve.StandardOutput,
            new Uri(target), string.IsNullOrWhiteSpace(explicitProxy) ? null : explicitProxy, null);
    }

    public async ValueTask DisposeAsync()
    {
        TryKill(proxyProcess);
        if (!ReferenceEquals(originProcess, proxyProcess))
            TryKill(originProcess);
        await Task.CompletedTask;
        if (!ReferenceEquals(originStdout, proxyStdout))
            originStdout.Dispose();
        proxyStdout.Dispose();
        proxyProcess.Dispose();
        if (originProcess != null && !ReferenceEquals(originProcess, proxyProcess))
            originProcess.Dispose();
    }

    private static Process StartChild(string exe, string args)
    {
        // Use cmd-style single Arguments string carefully; prefer ArgumentList when we can.
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var part in SplitArgs(args))
            psi.ArgumentList.Add(part);
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start child: {exe} {args}");
        return process;
    }

    private static IEnumerable<string> SplitArgs(string args)
    {
        // Minimal splitter: respects double-quoted segments.
        var list = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    list.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            list.Add(current.ToString());
        return list;
    }

    private static async Task<Dictionary<string, string>> ReadUntilReadyAsync(Process process,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Child exited early (code {process.ExitCode}). stderr: {err}");
            }

            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            Console.WriteLine($"  [child] {line}");
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                map[key] = value;
            }

            if (line.Equals("READY", StringComparison.Ordinal))
                return map;
        }

        TryKill(process);
        throw new TimeoutException("Timed out waiting for child READY marker.");
    }

    private static string Require(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Child did not report {key}=...");
        return value;
    }

    private static void TryKill(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best effort
        }
    }
}
