using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Non-blocking status output for the harness. Protocol lines for process-split IPC still go to
/// stdout (parent parses them) but are written asynchronously and flushed; human status uses a
/// background channel so hot paths never block on the console lock.
/// </summary>
internal static class ProbeLog
{
    private static readonly Channel<string> StatusChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private static readonly ILogger Logger = LoggerFactory
        .Create(b => b.SetMinimumLevel(LogLevel.Information))
        .CreateLogger("RpsLoadProbe");

    private static int started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await foreach (var line in StatusChannel.Reader.ReadAllAsync())
            {
                try
                {
                    await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch
                {
                    // status sink must not tear down the probe
                }
            }
        });
    }

    /// <summary>Human-readable status (queued; never blocks callers on console I/O).</summary>
    public static void Info(string message)
    {
        EnsureStarted();
        StatusChannel.Writer.TryWrite(message);
        Logger.LogInformation("{Message}", message);
    }

    public static void Error(string message)
    {
        EnsureStarted();
        StatusChannel.Writer.TryWrite(message);
        Logger.LogError("{Message}", message);
    }

    /// <summary>
    /// Process-split IPC line that the parent must observe on stdout. Uses async write + flush
    /// rather than synchronous Console.WriteLine.
    /// </summary>
    public static async Task WriteProtocolLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await Console.Out.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void WriteProtocolLine(string line) =>
        WriteProtocolLineAsync(line).GetAwaiter().GetResult();
}
