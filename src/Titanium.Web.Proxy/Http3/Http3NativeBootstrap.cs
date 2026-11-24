using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
/// Ensures app-local MsQuic natives are visible to <see cref="QuicListener"/> on macOS
/// framework-dependent hosts (typical Debug / <c>dotnet run</c>).
/// </summary>
/// <remarks>
/// On non-Windows, <see cref="QuicListener.IsSupported"/> loads MsQuic by leaf name only
/// (<c>libmsquic</c>), not from <see cref="AppContext.BaseDirectory"/>. Self-contained
/// publishes place <c>System.Net.Quic.dll</c> next to the bundled dylibs so AssemblyDirectory
/// search works. Framework-dependent builds keep Quic in the shared framework directory, so
/// copying dylibs beside the app is not enough unless <c>DYLD_FALLBACK_LIBRARY_PATH</c>
/// (or <c>DYLD_LIBRARY_PATH</c>) includes that folder — set before process start.
/// <para>
/// Re-launch uses a child process. The parent must forward POSIX termination/reload signals
/// to that child (and cancel the parent's default terminate) so <c>kill -HUP &lt;started-pid&gt;</c>,
/// SIGTERM from a service manager, and Ctrl+C reach the process that actually runs the proxy.
/// </para>
/// </remarks>
public static class Http3NativeBootstrap
{
    internal const string ReexecMarkerEnv = "TWP_HTTP3_REEXEC";
    internal const string SkipReexecEnv = "TWP_SKIP_HTTP3_REEXEC";

    /// <summary>
    /// When macOS app-local <c>libmsquic.dylib</c> is present but dyld cannot see it yet,
    /// re-launches the current process with <c>DYLD_FALLBACK_LIBRARY_PATH</c> pointing at
    /// <see cref="AppContext.BaseDirectory"/>. No-ops on Windows/Linux, self-contained layouts,
    /// when natives are missing, or when the library path already includes the app directory.
    /// </summary>
    /// <param name="args">Application arguments (as passed to <c>Main</c>), used when relaunching.</param>
    /// <returns>
    /// On Linux Titanium hosts, paths of leftover <c>libssl.so*</c> / <c>libcrypto.so*</c>
    /// that could not be deleted. Empty when cleanup succeeded or did not apply. Callers must
    /// stop before MsQuic loads when this list is not empty.
    /// </returns>
    public static IReadOnlyList<string> EnsureAppLocalMsQuicVisible(string[]? args = null)
    {
        // An 8.0.5 zip update copies the new build over the old directory and
        // leaves libssl/libcrypto behind. That updater runs inside 8.0.5, so
        // the delete has to happen when this process starts, before MsQuic loads.
        // Only Titanium's own hosts: this method is public, and a library consumer
        // may keep its own OpenSSL beside the app.
        IReadOnlyList<string> blocked = Array.Empty<string>();
        if (OperatingSystem.IsLinux()
            && IsTitaniumOpenSslCleanupHost(Assembly.GetEntryAssembly()?.GetName().Name))
        {
            blocked = RemoveStaleBundledOpenSsl(AppContext.BaseDirectory).BlockedPaths;
        }

        if (!OperatingSystem.IsMacOS())
        {
            return blocked;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(SkipReexecEnv), "1", StringComparison.Ordinal))
        {
            return blocked;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(ReexecMarkerEnv), "1", StringComparison.Ordinal))
        {
            return blocked;
        }

        var baseDir = NormalizeDir(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(baseDir))
        {
            return blocked;
        }

        var msquic = Path.Combine(baseDir, "libmsquic.dylib");
        if (!File.Exists(msquic))
        {
            return blocked;
        }

        // Self-contained / app-local framework: System.Net.Quic lives next to the dylibs.
        if (IsQuicAssemblyBesideApp(baseDir))
        {
            return blocked;
        }

        if (DyldSearchPathContains(baseDir))
        {
            return blocked;
        }

        RelaunchWithDyldFallback(baseDir, args ?? Array.Empty<string>());
        return blocked;
    }

    /// <summary>
    /// True for the Titanium CLI, Inspector, and load-probe hosts. Other entry
    /// assemblies keep whatever OpenSSL they shipped beside their binaries.
    /// </summary>
    internal static bool IsTitaniumOpenSslCleanupHost(string? entryAssemblyName) =>
        entryAssemblyName is "titanium" or "TitaniumInspector" or "RpsLoadProbe";

    /// <summary>
    /// Deletes bundled OpenSSL copies next to the app. Host OpenSSL 3.3+ aborts
    /// when MsQuic loads those files beside <c>libmsquic</c>. Writable files are
    /// removed quietly. A file that cannot be deleted is left in place and listed
    /// in <see cref="StaleOpenSslCleanupResult.BlockedPaths"/>. A directory with
    /// no <c>libmsquic.so*</c> is left untouched.
    /// </summary>
    internal static StaleOpenSslCleanupResult RemoveStaleBundledOpenSsl(string? directory) =>
        RemoveStaleBundledOpenSsl(directory, File.Delete);

    internal static StaleOpenSslCleanupResult RemoveStaleBundledOpenSsl(
        string? directory,
        Action<string> delete)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return StaleOpenSslCleanupResult.Empty;
        }

        if (!HasBundledMsQuic(directory))
        {
            return StaleOpenSslCleanupResult.Empty;
        }

        var removed = 0;
        List<string>? blocked = null;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("libssl.so", StringComparison.Ordinal)
                && !name.StartsWith("libcrypto.so", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Root-owned or locked leftovers. Stop the host before MsQuic loads.
                blocked ??= new List<string>();
                blocked.Add(path);
            }
        }

        return new StaleOpenSslCleanupResult(
            removed,
            blocked is null ? Array.Empty<string>() : blocked);
    }

    /// <summary>
    /// Message for the CLI stderr and the Inspector dialog when leftover OpenSSL
    /// files are still beside the install and could not be deleted.
    /// </summary>
    public static string FormatBlockedOpenSslMessage(IReadOnlyList<string> blockedPaths)
    {
        if (blockedPaths is null || blockedPaths.Count == 0)
        {
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(blockedPaths[0]) ?? ".";
        var quoted = string.Join(
            " ",
            blockedPaths.Select(static p => "'" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
        return
            "Leftover OpenSSL libraries next to this install could not be removed "
            + "(often because a previous install wrote them as root)."
            + Environment.NewLine
            + "MsQuic will abort with \"No usable version of libssl was found\" if they stay."
            + Environment.NewLine
            + "Delete them, leave libmsquic in place, then start again:"
            + Environment.NewLine
            + "  sudo rm -f " + quoted
            + Environment.NewLine
            + "Install directory: " + dir;
    }

    /// <summary>Result of deleting leftover bundled OpenSSL next to MsQuic.</summary>
    internal readonly struct StaleOpenSslCleanupResult
    {
        public static StaleOpenSslCleanupResult Empty { get; } =
            new(0, Array.Empty<string>());

        public StaleOpenSslCleanupResult(int removedCount, IReadOnlyList<string> blockedPaths)
        {
            RemovedCount = removedCount;
            BlockedPaths = blockedPaths ?? Array.Empty<string>();
        }

        public int RemovedCount { get; }

        public IReadOnlyList<string> BlockedPaths { get; }
    }

    private static bool HasBundledMsQuic(string directory) =>
        Directory.EnumerateFiles(directory)
            .Any(path => Path.GetFileName(path).StartsWith("libmsquic.so", StringComparison.Ordinal));

    private static bool IsQuicAssemblyBesideApp(string baseDir)
    {
        try
        {
            var location = typeof(QuicListener).Assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                // Single-file: Quic is embedded; BaseDirectory natives are the intended probe path.
                return true;
            }

            var quicDir = NormalizeDir(Path.GetDirectoryName(location));
            if (string.IsNullOrEmpty(quicDir))
            {
                return false;
            }

            return PathsEqual(quicDir, baseDir)
                   || quicDir.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool DyldSearchPathContains(string baseDir)
    {
        foreach (var key in new[] { "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_LIBRARY_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => PathsEqual(NormalizeDir(p), baseDir)))
            {
                return true;
            }
        }

        return false;
    }

    private static void RelaunchWithDyldFallback(string baseDir, string[] appArgs)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath))
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        AppendRelaunchArguments(psi, processPath, appArgs);

        var existingFallback = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH");
        psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = string.IsNullOrEmpty(existingFallback)
            ? baseDir
            : baseDir + ":" + existingFallback;
        psi.Environment[ReexecMarkerEnv] = "1";

        try
        {
            using var child = Process.Start(psi);
            if (child is null)
            {
                return;
            }

            // Parent keeps the original PID that launchd/shell/probes signal. Forward those
            // signals to the child that actually hosts Quic + the proxy, and cancel the parent's
            // default terminate so SIGHUP (reload) does not exit 129 before the child sees it.
            using var signalForwarders = ForwardUnixSignalsToChild(child);
            child.WaitForExit();
            Environment.Exit(child.ExitCode);
        }
        catch
        {
            // Leave the original process running; Quic may stay unsupported.
        }
    }

    /// <summary>
    /// Registers parent-side POSIX handlers that forward SIGHUP/SIGINT/SIGTERM to
    /// <paramref name="child"/> and cancel the parent's default terminate action.
    /// </summary>
    internal static IDisposable ForwardUnixSignalsToChild(Process child)
    {
        if (OperatingSystem.IsWindows())
        {
            return EmptyDisposable.Instance;
        }

        var registrations = new List<PosixSignalRegistration>(3);
        void Forward(PosixSignal signal, int signo)
        {
            registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
            {
                ctx.Cancel = true;
                try
                {
                    if (!child.HasExited)
                    {
                        _ = NativeKill(child.Id, signo);
                    }
                }
                catch
                {
                    // Child may have exited between HasExited and kill.
                }
            }));
        }

        // SIGHUP=1, SIGINT=2, SIGTERM=15 on Linux and macOS.
        Forward(PosixSignal.SIGHUP, 1);
        Forward(PosixSignal.SIGINT, 2);
        Forward(PosixSignal.SIGTERM, 15);
        return new SignalForwarderLease(registrations);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);

    private static void AppendRelaunchArguments(ProcessStartInfo psi, string processPath, string[] appArgs)
    {
        var hostName = Path.GetFileNameWithoutExtension(processPath);
        var isDotnetHost = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost)
        {
            // `dotnet path/to/app.dll …args` — keep the entry assembly path then app args.
            var entry = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entry) && File.Exists(entry))
            {
                psi.ArgumentList.Add(entry);
                foreach (var a in appArgs)
                {
                    psi.ArgumentList.Add(a);
                }

                return;
            }
        }

        // AppHost: argv is just the application arguments.
        foreach (var a in appArgs)
        {
            psi.ArgumentList.Add(a);
        }
    }

    private static string NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private sealed class SignalForwarderLease : IDisposable
    {
        private readonly List<PosixSignalRegistration> _registrations;

        public SignalForwarderLease(List<PosixSignalRegistration> registrations) =>
            _registrations = registrations;

        public void Dispose()
        {
            foreach (var reg in _registrations)
            {
                reg.Dispose();
            }

            _registrations.Clear();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
