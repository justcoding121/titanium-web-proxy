using System;
using System.Diagnostics;
using System.IO;
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
    public static void EnsureAppLocalMsQuicVisible(string[]? args = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(SkipReexecEnv), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(ReexecMarkerEnv), "1", StringComparison.Ordinal))
        {
            return;
        }

        var baseDir = NormalizeDir(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(baseDir))
        {
            return;
        }

        var msquic = Path.Combine(baseDir, "libmsquic.dylib");
        if (!File.Exists(msquic))
        {
            return;
        }

        // Self-contained / app-local framework: System.Net.Quic lives next to the dylibs.
        if (IsQuicAssemblyBesideApp(baseDir))
        {
            return;
        }

        if (DyldSearchPathContains(baseDir))
        {
            return;
        }

        RelaunchWithDyldFallback(baseDir, args ?? Array.Empty<string>());
    }

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

            foreach (var part in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (PathsEqual(NormalizeDir(part), baseDir))
                {
                    return true;
                }
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

            child.WaitForExit();
            Environment.Exit(child.ExitCode);
        }
        catch
        {
            // Leave the original process running; Quic may stay unsupported.
        }
    }

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
}
