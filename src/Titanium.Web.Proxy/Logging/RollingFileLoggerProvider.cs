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
///     <see cref="ChannelLoggerProviderBase" />; a locked/unwritable path disables the sink for the rest
///     of the run instead of retrying (and logging about) every entry.
/// </summary>
internal sealed class RollingFileLoggerProvider : ChannelLoggerProviderBase
{
    private readonly string filePath;
    private readonly long maxFileSizeBytes;
    private readonly int maxRolledFiles;

    private StreamWriter? writer;
    private long currentSize;
    private bool unavailable;

    public RollingFileLoggerProvider(ProxyLoggingOptions options) : base(options.QueueCapacity)
    {
        filePath = Path.GetFullPath(options.FilePath);
        maxFileSizeBytes = Math.Max(1024, options.MaxFileSizeBytes);
        maxRolledFiles = Math.Max(0, options.MaxRolledFiles);
    }

    protected override async Task WriteEntryAsync(LogEntry entry)
    {
        if (unavailable) return;

        try
        {
            EnsureWriter();

            var line = ProxyLog.FormatLine(entry);
            await writer!.WriteLineAsync(line).ConfigureAwait(false);
            currentSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (currentSize >= maxFileSizeBytes) Roll();
        }
        catch (IOException)
        {
            // Locked/missing directory/unwritable path: stop trying for the rest of the run rather than
            // retrying (and potentially recursively logging about) every subsequent entry.
            unavailable = true;
        }
        catch (UnauthorizedAccessException)
        {
            unavailable = true;
        }
    }

    private void EnsureWriter()
    {
        if (writer != null) return;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        currentSize = stream.Length;
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Roll()
    {
        writer?.Dispose();
        writer = null;

        try
        {
            if (maxRolledFiles <= 0)
            {
                // Rolling is disabled: start the active file fresh rather than letting it grow forever.
                if (File.Exists(filePath)) File.Delete(filePath);
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
        }
        catch (IOException)
        {
            // Best effort: if rolling itself fails (e.g. a rolled file is open elsewhere), keep appending
            // to the current file rather than losing entries.
        }

        currentSize = 0;
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
