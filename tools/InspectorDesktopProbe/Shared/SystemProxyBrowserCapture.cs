using System.Diagnostics;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Shared;

/// <summary>
/// Launch a browser that must honor the OS system proxy (no --proxy-server on Windows).
/// Shared by InspectorDesktopProbe and E2E-Slow.
/// </summary>
public static class SystemProxyBrowserCapture
{
    public const string DefaultProbeHost = "example.com";

    public static Process StartChromiumViaSystemProxy(
        string browserPath,
        string userDataDir,
        string url,
        bool disableQuic,
        bool headless = false)
    {
        var quic = disableQuic ? "--disable-quic " : string.Empty;
        var headlessArg = headless ? "--headless=new --disable-gpu " : string.Empty;
        return Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments =
                $"{headlessArg}{quic}--no-first-run --disable-extensions --disable-background-networking " +
                $"--user-data-dir=\"{userDataDir}\" " +
                $"\"{url}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {browserPath}");
    }

    public static Process StartFirefox(string browserPath, string url) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = $"-no-remote -new-instance \"{url}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {browserPath}");

    public static async Task<SessionSnapshot?> WaitForHostAsync(
        InterceptionService interception,
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        SessionSnapshot? captured = null;
        void Handler(object? _, SessionSnapshot s)
        {
            if (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase))
                captured = s;
        }

        interception.SessionCaptured += Handler;
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (captured is null && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            return captured;
        }
        finally
        {
            interception.SessionCaptured -= Handler;
        }
    }

    public static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    public static void TryDeleteDir(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
