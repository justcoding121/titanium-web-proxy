using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>
///     Always-on, low-volume timing trace for Inspector UX / OS-trust hangs.
///     Writes to <c>%AppData%/TitaniumInspector/logs/ux-trace.log</c> (separate from
///     <c>titanium-inspector.log</c>) so Debug file logging does not need to be enabled.
/// </summary>
internal static class InspectorUxTrace
{
    private static readonly object Gate = new();
    private static readonly string Path = ResolvePath();
    private const long RotateBytes = 4 * 1024 * 1024;
    private static long _seq;

    public static string LogFilePath => Path;

    public static IDisposable Scope(string name, string? detail = null)
    {
        var id = Interlocked.Increment(ref _seq);
        var sw = Stopwatch.StartNew();
        Write("BEGIN", name, detail, elapsedMs: null, id);
        return new ScopeEnd(name, detail, sw, id);
    }

    public static void Event(string name, string? detail = null) =>
        Write("EVENT", name, detail, elapsedMs: null, id: Interlocked.Increment(ref _seq));

    private static void Write(string kind, string name, string? detail, long? elapsedMs, long id)
    {
        try
        {
            var sb = new StringBuilder(160);
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            sb.Append(" t=").Append(Environment.CurrentManagedThreadId);
            sb.Append(' ').Append(kind);
            sb.Append(" #").Append(id);
            sb.Append(' ').Append(name);
            if (elapsedMs is not null)
                sb.Append(" ms=").Append(elapsedMs.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(detail))
                sb.Append(' ').Append(detail);
            sb.AppendLine();

            lock (Gate)
            {
                RotateIfNeeded_NoLock();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, sb.ToString());
            }
        }
        catch
        {
            // never break UX because of tracing
        }
    }

    private static void RotateIfNeeded_NoLock()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < RotateBytes)
                return;

            var bak = Path + ".1";
            if (File.Exists(bak))
                File.Delete(bak);
            File.Move(Path, bak);
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolvePath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector", "logs", "ux-trace.log");

    private sealed class ScopeEnd(string name, string? detail, Stopwatch sw, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var ms = sw.ElapsedMilliseconds;
            Write("END", name, detail, ms, id);
            // Easy grep marker for hangs / multi-second stalls.
            if (ms >= 750)
                Write("SLOW", name, detail, ms, id);
        }
    }
}
