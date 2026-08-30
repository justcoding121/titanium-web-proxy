using System.Threading.Channels;

namespace Titanium.Cli;

/// <summary>
/// Non-blocking console UX for the CLI. Call sites only enqueue; a single background task owns
/// <see cref="Console"/> I/O so callers never block on the console lock. Await
/// <see cref="FlushAsync"/> before process exit so queued lines appear.
/// </summary>
internal static class AsyncConsole
{
    private sealed class Entry
    {
        public bool IsError;
        public bool NewLine = true;
        public string Text = "";
        public TaskCompletionSource? Flush;
    }

    private static readonly Channel<Entry> Channel = System.Threading.Channels.Channel.CreateUnbounded<Entry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private static int started;

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await foreach (var entry in Channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (entry.Flush is { } flush)
                    {
                        flush.TrySetResult();
                        continue;
                    }

                    var writer = entry.IsError ? Console.Error : Console.Out;
                    if (entry.NewLine)
                        await writer.WriteLineAsync(entry.Text).ConfigureAwait(false);
                    else
                        await writer.WriteAsync(entry.Text).ConfigureAwait(false);
                }
                catch
                {
                    entry.Flush?.TrySetResult();
                }
            }
        });
    }

    public static void WriteLine(string message)
    {
        EnsureStarted();
        Channel.Writer.TryWrite(new Entry { Text = message });
    }

    public static void Write(string message)
    {
        EnsureStarted();
        Channel.Writer.TryWrite(new Entry { Text = message, NewLine = false });
    }

    public static void WriteError(string message)
    {
        EnsureStarted();
        Channel.Writer.TryWrite(new Entry { IsError = true, Text = message });
    }

    public static void WriteErrorRaw(string message)
    {
        EnsureStarted();
        Channel.Writer.TryWrite(new Entry { IsError = true, NewLine = false, Text = message });
    }

    /// <summary>Wait until every entry enqueued so far has been written.</summary>
    public static Task FlushAsync()
    {
        if (Volatile.Read(ref started) == 0) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel.Writer.TryWrite(new Entry { Flush = tcs });
        return tcs.Task;
    }
}
