using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Shared plumbing for the built-in asynchronous sinks (<see cref="ConsoleLoggerProvider" />,
///     <see cref="RollingFileLoggerProvider" />). Every log call goes through a bounded
///     <see cref="Channel{T}" /> and a single dedicated writer task/thread per sink, so a call site
///     (including deep inside a hot proxy code path) never blocks on console/disk I/O: it only does a
///     non-blocking <see cref="ChannelWriter{T}.TryWrite" />.
///     Delivery is best-effort: when the main queue saturates, low-severity entries are dropped (and
///     counted) while <see cref="LogLevel.Error" />/<see cref="LogLevel.Critical" /> entries overflow
///     into a second, small bounded channel that the same writer drains preferentially. The sink handle
///     itself is only ever touched by that one writer - never by the calling (hot-path) thread and never
///     under a lock shared with it, since a shared lock would block a proxy hot path on disk I/O, which
///     is exactly what this whole channel design exists to prevent.
/// </summary>
internal abstract class ChannelLoggerProviderBase : ILoggerProvider
{
    private readonly Channel<LogEntry> channel;
    private readonly Channel<LogEntry> priorityChannel;
    private readonly Task writerTask;
    private readonly CancellationTokenSource stopTokenSource = new();
    private long droppedEntries;
    private long priorityDroppedEntries;
    private bool disposed;

    protected ChannelLoggerProviderBase(int queueCapacity)
    {
        // FullMode = Wait (rather than one of the Drop* modes) is deliberate even though nothing
        // here ever awaits a write: TryWrite is the only writer API used, it never blocks
        // regardless of FullMode, and Wait is the only mode under which TryWrite's return value
        // means what Enqueue below needs it to mean. Under DropWrite/DropNewest/DropOldest,
        // TryWrite always returns true - even when the item was just silently discarded - because
        // those modes are defined as "the write always succeeds; something else gets dropped
        // instead". That would make the saturation check below (and the Error/Critical overflow
        // path it guards) permanently unreachable.
        channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Overflow path for Error/Critical entries that lose the race for a main-channel slot.
        // Deliberately small and bounded: this exists so the writer - not the calling thread - is
        // still the only thing that ever touches sink state, not as a general second queue. It
        // must never itself block a hot path, so like the main channel it drops (and counts)
        // rather than blocks when full - see the FullMode note above for why Wait is correct here.
        priorityChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        writerTask = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ChannelSinkLogger(this, categoryName);
    }

    internal void Enqueue(in LogEntry entry)
    {
        if (disposed) return;

        if (channel.Writer.TryWrite(entry)) return;

        Interlocked.Increment(ref droppedEntries);
        ProxyMetrics.LoggerEntryDropped("main");

        // The main queue is saturated. Errors/critical failures are important enough to justify
        // routing around the drop, but the single owning writer task must remain the only thread
        // that ever writes to the sink: a synchronous write here on the calling thread would race
        // the writer task against the same file handle/console with no lock protecting either
        // side (and adding one would block this hot-path thread on disk I/O). So high-severity
        // overflow goes to the small dedicated priority channel instead, via the same non-blocking
        // TryWrite discipline as the main path.
        if (entry.Level >= LogLevel.Error && !priorityChannel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref priorityDroppedEntries);
            ProxyMetrics.LoggerEntryDropped("priority");
        }
    }

    /// <summary>
    ///     Writes one formatted entry to the underlying sink (console/file). Called only from the single
    ///     background writer task, so implementations do not need to be thread-safe against each other.
    /// </summary>
    protected abstract Task WriteEntryAsync(LogEntry entry);

    private async Task ProcessQueueAsync()
    {
        var stopToken = stopTokenSource.Token;
        try
        {
            while (true)
            {
                // Drain any pending high-severity overflow first and exhaustively, so a burst of
                // Error/Critical entries cannot be starved behind a busy main channel.
                await DrainPriorityChannelAsync().ConfigureAwait(false);

                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!more)
                {
                    // Main channel completed (Dispose called): one last priority drain for anything
                    // that arrived concurrently with completion, then stop.
                    await DrainPriorityChannelAsync().ConfigureAwait(false);
                    break;
                }

                while (channel.Reader.TryRead(out var entry))
                {
                    await WriteEntrySafeAsync(entry).ConfigureAwait(false);

                    // Interleave so a long run on the main channel cannot indefinitely delay a
                    // priority entry queued in the meantime.
                    await DrainPriorityChannelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task DrainPriorityChannelAsync()
    {
        while (priorityChannel.Reader.TryRead(out var entry))
            await WriteEntrySafeAsync(entry).ConfigureAwait(false);
    }

    private async Task WriteEntrySafeAsync(LogEntry entry)
    {
        try
        {
            await WriteEntryAsync(entry).ConfigureAwait(false);
        }
        catch
        {
            // A misbehaving sink (disk full, permission error, etc.) must never crash the shared
            // writer loop or escape into proxy code; best-effort delivery only.
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        channel.Writer.TryComplete();
        priorityChannel.Writer.TryComplete();

        var writerCompletedInTime = false;
        try
        {
            // Bounded drain: give the writer a short window to flush what is already queued, but
            // never block shutdown indefinitely on a stuck sink.
            writerCompletedInTime = writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // writerTask faulted rather than timed out; either way it is no longer running, which
            // is what writerCompletedInTime distinguishes below.
            writerCompletedInTime = writerTask.IsCompleted;
        }

        if (!writerCompletedInTime)
        {
            // The writer task may still be mid-write inside WriteEntryAsync. Calling DisposeSink()
            // now would race that in-progress write against handle teardown. Ask the loop to stop
            // at its next cancellation check and deliberately leak the sink handle rather than risk
            // disposing underneath an active write; the OS reclaims it at process exit.
            stopTokenSource.Cancel();
            OnSinkDisposalLeaked();
            return;
        }

        stopTokenSource.Dispose();
        DisposeSink();
    }

    /// <summary>
    ///     Called when the writer task did not finish draining within the shutdown grace period, so
    ///     <see cref="DisposeSink" /> was deliberately skipped to avoid racing an in-progress write. The
    ///     default implementation reports to <see cref="Console.Error" /> as a last resort: the sink this
    ///     provider owns cannot be trusted to still be usable, and nothing else is guaranteed to be
    ///     listening for it. Exposed as protected so tests (and derived sinks) can observe the condition
    ///     directly instead of scraping stderr.
    /// </summary>
    protected virtual void OnSinkDisposalLeaked()
    {
        try
        {
            Console.Error.WriteLine(
                $"{GetType().Name}: sink writer did not complete within the shutdown grace period; " +
                "the underlying handle was intentionally left open (leaked) rather than disposed " +
                "underneath an in-progress write.");
        }
        catch
        {
            // Best-effort only; stderr itself may be unavailable during shutdown.
        }
    }

    /// <summary>
    ///     Releases sink-specific resources (open file handles, etc.). Called once, only after the
    ///     writer task has fully completed - never after a timed-out drain, since the writer could still
    ///     be mid-write against the same handle in that case.
    /// </summary>
    protected virtual void DisposeSink()
    {
    }

    private sealed class ChannelSinkLogger : ILogger
    {
        private readonly ChannelLoggerProviderBase provider;
        private readonly string category;

        public ChannelSinkLogger(ChannelLoggerProviderBase provider, string category)
        {
            this.provider = provider;
            this.category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            provider.Enqueue(new LogEntry(DateTime.Now, logLevel, category, eventId, message, exception));
        }
    }
}
