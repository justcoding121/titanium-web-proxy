using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Built-in size-based rolling-file sink. Rolls the active file to <c>&lt;path&gt;.1</c> once it
///     reaches <see cref="ProxyLoggingOptions.MaxFileSizeBytes" />, shifting older rolled files up to
///     <c>&lt;path&gt;.N</c> and deleting anything beyond <see cref="ProxyLoggingOptions.MaxRolledFiles" />.
///     All file I/O happens on the single background writer thread owned by
///     <see cref="ChannelLoggerProviderBase" />; a locked/unwritable path backs off briefly and retries
///     rather than disabling the sink for the rest of the process (so a force-killed previous instance
///     still holding the handle, or a diagnostic reader briefly locking a rolled file, cannot silently
///     truncate the visible active-file history for the remainder of a run).
/// </summary>
internal sealed class RollingFileLoggerProvider : ChannelLoggerProviderBase
{
    private readonly string filePath;
    private readonly long maxFileSizeBytes;
    private readonly int maxRolledFiles;

    private StreamWriter? writer;
    private long currentSize;
    /// <summary>UTC ticks after which a transient open/write failure should be retried; 0 = healthy.</summary>
    private long retryAfterUtcTicks;
    private bool permanentlyUnavailable;

    public RollingFileLoggerProvider(ProxyLoggingOptions options) : base(options.QueueCapacity)
    {
        filePath = Path.GetFullPath(options.FilePath);
        maxFileSizeBytes = Math.Max(1024, options.MaxFileSizeBytes);
        maxRolledFiles = Math.Max(0, options.MaxRolledFiles);
    }

    protected override async Task WriteEntryAsync(LogEntry entry)
    {
        if (permanentlyUnavailable) return;
        if (retryAfterUtcTicks != 0 && DateTime.UtcNow.Ticks < retryAfterUtcTicks) return;

        try
        {
            var currentWriter = EnsureWriter();

            var line = ProxyLog.FormatLine(entry);
            await currentWriter.WriteLineAsync(line).ConfigureAwait(false);
            currentSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            retryAfterUtcTicks = 0;

            if (currentSize >= maxFileSizeBytes) Roll();
        }
        catch (IOException)
        {
            // Transient: previous process still releasing the handle after a force-kill, AV scan,
            // or another reader briefly locking a file during roll. Drop this entry, close the
            // handle, and retry on a later write — do not permanently disable the sink (that made
            // active-file HE/cancel counts look like they "went backwards" after restarts).
            CloseWriterSilent();
            retryAfterUtcTicks = DateTime.UtcNow.AddSeconds(1).Ticks;
        }
        catch (UnauthorizedAccessException)
        {
            permanentlyUnavailable = true;
            CloseWriterSilent();
        }
    }

    private StreamWriter EnsureWriter()
    {
        if (writer != null) return writer;

        OpenWriter();

        // A killed previous process (or a failed mid-roll) can leave an oversized active file.
        // Roll before accepting more writes so counts don't appear to "reset" while history is only
        // in sibling rolled files that metrics forgot to include.
        if (currentSize >= maxFileSizeBytes)
        {
            Roll();
            if (writer == null) OpenWriter();
        }

        return writer!;
    }

    private void OpenWriter()
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        currentSize = stream.Length;
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Roll()
    {
        CloseWriterSilent();

        try
        {
            if (maxRolledFiles <= 0)
            {
                // Rolling is disabled: start the active file fresh rather than letting it grow forever.
                if (File.Exists(filePath)) File.Delete(filePath);
                currentSize = 0;
                return;
            }

            for (var i = maxRolledFiles; i >= 1; i--)
            {
                var source = i == 1 ? filePath : filePath + "." + (i - 1);
                var destination = filePath + "." + i;

                if (!File.Exists(source)) continue;

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(source, destination);
            }

            // Only clear size after the active file was successfully moved/deleted above.
            currentSize = 0;
        }
        catch (IOException)
        {
            // Best effort: if rolling itself fails (e.g. a rolled file is open elsewhere), leave
            // currentSize alone so the next EnsureWriter() re-reads Length and retries the roll.
            // Do not pretend the file is empty — that skipped rolls and made active-file greps
            // under-count history that still lived only in *.N siblings.
        }
    }

    private void CloseWriterSilent()
    {
        try
        {
            writer?.Flush();
            writer?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Best-effort close during roll/teardown; stream may already be disposed or in use.
        }
        catch (IOException)
        {
            // Best-effort close during roll/teardown; ignore sink I/O failures.
        }
        finally
        {
            writer = null;
        }
    }

    protected override void DisposeSink()
    {
        try
        {
            // ChannelLoggerProviderBase.Dispose() only *bounded*-waits for the background writer task to
            // drain before calling here; on a slow/loaded runner (e.g. under code-coverage instrumentation)
            // that task can still be mid-write when the timeout elapses. Racing a synchronous Flush/Dispose
            // against that in-flight async write throws (typically InvalidOperationException: "stream is
            // currently in use"). Same best-effort philosophy as WriteEntryAsync/Roll: never let sink
            // teardown throw into the caller.
            writer?.Flush();
            writer?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Covers ObjectDisposedException too (it derives from InvalidOperationException).
        }
        catch (IOException)
        {
            // Logging teardown is best-effort; ignore sink I/O failures.
        }
        finally
        {
            writer = null;
        }
    }
}
