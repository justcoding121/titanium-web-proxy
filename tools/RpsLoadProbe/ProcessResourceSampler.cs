using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Polls RSS and CPU% for a child PID during a measure window.
/// Columns stay named <c>proxy_*</c> even when the sampled PID is the origin (origin-direct arms).
/// Includes the full descendant process tree (serve-proxy → nginx master → workers).
/// </summary>
internal sealed record ProcessResourceSample(long PeakRssBytes, double AvgCpuPercent);

internal static class ProcessResourceSampler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Sample <paramref name="pid"/> (plus direct children) for approximately <paramref name="duration"/>.
    /// Returns null if the process cannot be opened or no samples succeed.
    /// </summary>
    public static async Task<ProcessResourceSample?> SampleDuringAsync(int pid, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (pid <= 0)
            return null;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return await SampleWindowsAsync(pid, duration, cancellationToken).ConfigureAwait(false);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return await SampleLinuxAsync(pid, duration, cancellationToken).ConfigureAwait(false);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return await SampleMacAsync(pid, duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Sampling is best-effort; CSV columns stay empty.
        }

        return null;
    }

    /// <summary>
    /// Darwin: <see cref="Process.WorkingSet64"/> + <see cref="Process.TotalProcessorTime"/> over the
    /// descendant tree (serve-proxy → nginx master → workers). Same shape as the Windows sampler.
    /// </summary>
    private static async Task<ProcessResourceSample?> SampleMacAsync(int rootPid, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var handles = new Dictionary<int, Process>();
        try
        {
            AttachMacPids(handles, rootPid);
            if (handles.Count == 0)
                return null;

            long peakRss = 0;
            var cpuSamples = new List<double>(capacity: 64);
            var prevCpuByPid = new Dictionary<int, TimeSpan>();
            long? prevWall = null;
            var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            var processors = Math.Max(1, Environment.ProcessorCount);

            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AttachMacPids(handles, rootPid);

                long rssSum = 0;
                double cpuDeltaSec = 0;
                var wallNow = Stopwatch.GetTimestamp();
                var dead = new List<int>();

                foreach (var (pid, process) in handles)
                {
                    try
                    {
                        process.Refresh();
                        if (process.HasExited)
                        {
                            dead.Add(pid);
                            continue;
                        }

                        rssSum += process.WorkingSet64;
                        var cpu = process.TotalProcessorTime;
                        if (prevWall is not null && prevCpuByPid.TryGetValue(pid, out var prev))
                            cpuDeltaSec += (cpu - prev).TotalSeconds;
                        prevCpuByPid[pid] = cpu;
                    }
                    catch (InvalidOperationException)
                    {
                        dead.Add(pid);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        dead.Add(pid);
                    }
                }

                foreach (var pid in dead)
                {
                    if (handles.Remove(pid, out var gone))
                        gone.Dispose();
                    prevCpuByPid.Remove(pid);
                }

                if (handles.Count == 0)
                    break;

                if (rssSum > peakRss)
                    peakRss = rssSum;

                if (prevWall is { } wall)
                {
                    var wallSec = (wallNow - wall) / (double)Stopwatch.Frequency;
                    if (wallSec > 0)
                    {
                        var pct = cpuDeltaSec / wallSec / processors * 100.0;
                        if (pct >= 0 && !double.IsNaN(pct) && !double.IsInfinity(pct))
                            cpuSamples.Add(pct);
                    }
                }

                prevWall = wallNow;

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    break;
                var delay = TimeSpan.FromSeconds(Math.Min(PollInterval.TotalSeconds,
                    remainingTicks / (double)Stopwatch.Frequency));
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (peakRss <= 0 && cpuSamples.Count == 0)
                return null;

            var avgCpu = cpuSamples.Count > 0 ? cpuSamples.Average() : 0;
            return new ProcessResourceSample(peakRss, avgCpu);
        }
        finally
        {
            foreach (var process in handles.Values)
                process.Dispose();
        }
    }

    private static void AttachMacPids(Dictionary<int, Process> handles, int rootPid)
    {
        foreach (var pid in GetMacTreePids(rootPid))
        {
            if (handles.ContainsKey(pid))
                continue;
            try
            {
                handles[pid] = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // exited
            }
        }
    }

    /// <summary>
    /// Root PID plus descendants via repeated <c>pgrep -P</c> (nginx workers under serve-proxy).
    /// </summary>
    private static List<int> GetMacTreePids(int rootPid)
    {
        var result = new List<int>();
        var seen = new HashSet<int> { rootPid };
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        try
        {
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);
                foreach (var kid in TryPgrepChildren(current))
                {
                    if (seen.Add(kid))
                        queue.Enqueue(kid);
                }
            }
        }
        catch
        {
            return [rootPid];
        }

        return result.Count > 0 ? result : [rootPid];
    }

    private static List<int> TryPgrepChildren(int parentPid)
    {
        var kids = new List<int>();
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pgrep",
                    ArgumentList = { "-P", parentPid.ToString(CultureInfo.InvariantCulture) },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kid) && kid > 0)
                    kids.Add(kid);
            }
        }
        catch
        {
            // best-effort
        }

        return kids;
    }

    private static async Task<ProcessResourceSample?> SampleWindowsAsync(int rootPid, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        // Hold Process handles for the window so TotalProcessorTime deltas stay consistent.
        var handles = new Dictionary<int, Process>();
        try
        {
            AttachWindowsPids(handles, rootPid);
            if (handles.Count == 0)
                return null;

            long peakRss = 0;
            var cpuSamples = new List<double>(capacity: 64);
            var prevCpuByPid = new Dictionary<int, TimeSpan>();
            long? prevWall = null;
            var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            var processors = Math.Max(1, Environment.ProcessorCount);

            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AttachWindowsPids(handles, rootPid);

                long rssSum = 0;
                double cpuDeltaSec = 0;
                var wallNow = Stopwatch.GetTimestamp();
                var dead = new List<int>();

                foreach (var (pid, process) in handles)
                {
                    try
                    {
                        process.Refresh();
                        if (process.HasExited)
                        {
                            dead.Add(pid);
                            continue;
                        }

                        rssSum += process.WorkingSet64;
                        var cpu = process.TotalProcessorTime;
                        if (prevWall is not null && prevCpuByPid.TryGetValue(pid, out var prev))
                            cpuDeltaSec += (cpu - prev).TotalSeconds;
                        prevCpuByPid[pid] = cpu;
                    }
                    catch (InvalidOperationException)
                    {
                        dead.Add(pid);
                    }
                }

                foreach (var pid in dead)
                {
                    if (handles.Remove(pid, out var gone))
                        gone.Dispose();
                    prevCpuByPid.Remove(pid);
                }

                if (handles.Count == 0)
                    break;

                if (rssSum > peakRss)
                    peakRss = rssSum;

                if (prevWall is { } wall)
                {
                    var wallSec = (wallNow - wall) / (double)Stopwatch.Frequency;
                    if (wallSec > 0)
                    {
                        var pct = cpuDeltaSec / wallSec / processors * 100.0;
                        if (pct >= 0 && !double.IsNaN(pct) && !double.IsInfinity(pct))
                            cpuSamples.Add(pct);
                    }
                }

                prevWall = wallNow;

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    break;
                var delay = TimeSpan.FromSeconds(Math.Min(PollInterval.TotalSeconds,
                    remainingTicks / (double)Stopwatch.Frequency));
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (peakRss <= 0 && cpuSamples.Count == 0)
                return null;

            var avgCpu = cpuSamples.Count > 0 ? cpuSamples.Average() : 0;
            return new ProcessResourceSample(peakRss, avgCpu);
        }
        finally
        {
            foreach (var process in handles.Values)
                process.Dispose();
        }
    }

    private static void AttachWindowsPids(Dictionary<int, Process> handles, int rootPid)
    {
        foreach (var pid in GetWindowsTreePids(rootPid))
        {
            if (handles.ContainsKey(pid))
                continue;
            try
            {
                handles[pid] = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // exited
            }
        }
    }

    private static async Task<ProcessResourceSample?> SampleLinuxAsync(int rootPid, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var hz = GetLinuxClockTicksPerSecond();
        long peakRss = 0;
        var cpuSamples = new List<double>(capacity: 64);
        var prevTicksByPid = new Dictionary<int, long>();
        var prevWall = Stopwatch.GetTimestamp();
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var processors = Math.Max(1, Environment.ProcessorCount);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pids = GetLinuxTreePids(rootPid);
            if (pids.Count == 0)
                break;

            long rssSum = 0;
            long ticksSum = 0;
            var alive = new List<int>();
            foreach (var pid in pids)
            {
                var rss = TryReadLinuxRssBytes($"/proc/{pid}/status");
                var ticks = TryReadLinuxCpuTicks($"/proc/{pid}/stat");
                if (rss is null && ticks is null)
                    continue;
                alive.Add(pid);
                if (rss is > 0)
                    rssSum += rss.Value;
                if (ticks is { } t)
                    ticksSum += t;
            }

            if (alive.Count == 0)
                break;

            if (rssSum > peakRss)
                peakRss = rssSum;

            var wallNow = Stopwatch.GetTimestamp();
            if (prevTicksByPid.Count > 0)
            {
                var wallSec = (wallNow - prevWall) / (double)Stopwatch.Frequency;
                if (wallSec > 0 && hz > 0)
                {
                    long tickDelta = 0;
                    foreach (var pid in alive)
                    {
                        var ticks = TryReadLinuxCpuTicks($"/proc/{pid}/stat");
                        if (ticks is { } now && prevTicksByPid.TryGetValue(pid, out var prev))
                            tickDelta += now - prev;
                    }

                    var cpuSec = tickDelta / (double)hz;
                    var pct = cpuSec / wallSec / processors * 100.0;
                    if (pct >= 0 && !double.IsNaN(pct) && !double.IsInfinity(pct))
                        cpuSamples.Add(pct);
                }
            }

            prevTicksByPid.Clear();
            foreach (var pid in alive)
            {
                var ticks = TryReadLinuxCpuTicks($"/proc/{pid}/stat");
                if (ticks is { } t)
                    prevTicksByPid[pid] = t;
            }

            prevWall = wallNow;

            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                break;
            var delay = TimeSpan.FromSeconds(Math.Min(PollInterval.TotalSeconds,
                remainingTicks / (double)Stopwatch.Frequency));
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (peakRss <= 0 && cpuSamples.Count == 0)
            return null;

        var avgCpu = cpuSamples.Count > 0 ? cpuSamples.Average() : 0;
        return new ProcessResourceSample(peakRss, avgCpu);
    }

    /// <summary>
    /// Root PID plus all descendants (serve-proxy → nginx master → workers).
    /// Direct-children-only misses nginx workers under the .NET proxy child.
    /// </summary>
    private static List<int> GetWindowsTreePids(int rootPid)
    {
        var result = new List<int>();
        try
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
                return [rootPid];

            try
            {
                var childrenByParent = new Dictionary<int, List<int>>();
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snapshot, ref entry))
                    return [rootPid];

                do
                {
                    var pid = (int)entry.th32ProcessID;
                    var ppid = (int)entry.th32ParentProcessID;
                    if (pid <= 0 || pid == ppid)
                        continue;
                    if (!childrenByParent.TryGetValue(ppid, out var list))
                    {
                        list = [];
                        childrenByParent[ppid] = list;
                    }

                    list.Add(pid);
                } while (Process32Next(snapshot, ref entry));

                var queue = new Queue<int>();
                var seen = new HashSet<int> { rootPid };
                queue.Enqueue(rootPid);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    result.Add(current);
                    if (!childrenByParent.TryGetValue(current, out var kids))
                        continue;
                    foreach (var kid in kids)
                    {
                        if (seen.Add(kid))
                            queue.Enqueue(kid);
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        catch
        {
            return [rootPid];
        }

        return result.Count > 0 ? result : [rootPid];
    }

    private static List<int> GetLinuxTreePids(int rootPid)
    {
        var result = new List<int>();
        var seen = new HashSet<int> { rootPid };
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        try
        {
            // Build ppid → children once when walking deep trees (nginx workers are grandchildren).
            var childrenByParent = new Dictionary<int, List<int>>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    continue;
                var ppid = TryReadLinuxPpid($"/proc/{pid}/stat");
                if (ppid is null or <= 0 || ppid == pid)
                    continue;
                if (!childrenByParent.TryGetValue(ppid.Value, out var list))
                {
                    list = [];
                    childrenByParent[ppid.Value] = list;
                }

                list.Add(pid);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);
                if (!childrenByParent.TryGetValue(current, out var kids))
                    continue;
                foreach (var kid in kids)
                {
                    if (seen.Add(kid))
                        queue.Enqueue(kid);
                }
            }
        }
        catch
        {
            return [rootPid];
        }

        return result.Count > 0 ? result : [rootPid];
    }

    private static int? TryReadLinuxPpid(string statPath)
    {
        try
        {
            var line = File.ReadAllText(statPath);
            var closeParen = line.LastIndexOf(')');
            if (closeParen < 0 || closeParen + 2 >= line.Length)
                return null;
            var afterComm = line[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // After ") ": field 3=state (index 0), field 4=ppid (index 1)
            if (afterComm.Length < 2)
                return null;
            if (int.TryParse(afterComm[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid))
                return ppid;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static long? TryReadLinuxRssBytes(string statusPath)
    {
        try
        {
            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var kb))
                    return kb * 1024;
            }
        }
        catch
        {
            // process may have exited
        }

        return null;
    }

    /// <summary>
    /// Reads utime+stime (fields 14+15, 1-based after comm) from <c>/proc/&lt;pid&gt;/stat</c>.
    /// </summary>
    private static long? TryReadLinuxCpuTicks(string statPath)
    {
        try
        {
            var line = File.ReadAllText(statPath);
            var closeParen = line.LastIndexOf(')');
            if (closeParen < 0 || closeParen + 2 >= line.Length)
                return null;

            var afterComm = line[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // After ") ": field 3 is state; fields 14/15 are indices 11/12 in this slice (1-based 14 = index 11).
            if (afterComm.Length < 13)
                return null;
            if (!long.TryParse(afterComm[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utime))
                return null;
            if (!long.TryParse(afterComm[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stime))
                return null;
            return utime + stime;
        }
        catch
        {
            return null;
        }
    }

    private static long GetLinuxClockTicksPerSecond()
    {
        try
        {
            var hz = sysconf(_SC_CLK_TCK);
            if (hz > 0)
                return hz;
        }
        catch
        {
            // fall through
        }

        return 100;
    }

    // ReSharper disable once InconsistentNaming
    private const int _SC_CLK_TCK = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern long sysconf(int name);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
