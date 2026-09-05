using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.InteropServices;
using Titanium.Cli;

namespace Titanium.Cli.Http3;

/// <summary>
/// Reports Quic / MsQuic availability and optionally installs system packages for long-tail hosts
/// where the RID zip does not already bundle natives (or bundling failed to load).
/// </summary>
internal static class Http3DepsCommand
{
    private const string InstallSubcommand = "install";
    private const string LibMsQuicPackage = "libmsquic";
    private const string LddAbsolutePath = "/usr/bin/ldd";

    public static async Task<int> ExecuteAsync(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        return sub switch
        {
            "status" => Status(),
            InstallSubcommand => await InstallAsync(),
            "help" or "-h" or "--help" => PrintHelp(),
            _ => await UnknownAsync(sub),
        };
    }

    private static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium http3-deps status|install

              status   Print QuicListener.IsSupported and packaging hints.
              install  Install system MsQuic (+ host deps) via apt / dnf / zypper / apk / brew.

            Prefer the matching CLI RID zip (linux-x64, linux-musl-x64, osx-arm64, …) which already
            bundles MsQuic + OpenSSL (MIT/Apache). Zips do NOT ship libnuma / lttng-ust (LGPL/GPL);
            those stay host packages. Use install for empty/distroless images or when Quic is false.
            """);
        return 0;
    }

    private static async Task<int> UnknownAsync(string sub)
    {
        AsyncConsole.WriteError($"Unknown http3-deps subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static int Status()
    {
        var supported = QuicListener.IsSupported;
        AsyncConsole.WriteLine($"QuicListener.IsSupported: {supported}");
        AsyncConsole.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        AsyncConsole.WriteLine($"Arch: {RuntimeInformation.OSArchitecture}");
        AsyncConsole.WriteLine($"Suggested RID: {SuggestRid()}");

        if (supported)
        {
            AsyncConsole.WriteLine("HTTP/3 natives appear available (bundled zip or system MsQuic).");
            return 0;
        }

        AsyncConsole.WriteLine("HTTP/3 is not available on this machine.");
        AsyncConsole.WriteLine("Fixes:");
        AsyncConsole.WriteLine("  1) Download the matching RID zip for your OS/arch (see /docs/http3).");
        AsyncConsole.WriteLine("     Alpine/K8s musl images need linux-musl-x64 or linux-musl-arm64, not linux-x64.");
        AsyncConsole.WriteLine("     RID zips ship MsQuic+OpenSSL only; install host deps if the loader still fails:");
        AsyncConsole.WriteLine("       Ubuntu/Debian: libnuma1");
        AsyncConsole.WriteLine("       Alpine:        numactl lttng-ust");
        AsyncConsole.WriteLine($"  2) Or run: titanium http3-deps {InstallSubcommand}");
        if (OperatingSystem.IsMacOS())
        {
            AsyncConsole.WriteLine("     macOS: brew install libmsquic openssl@3, then rebuild (copies natives beside the binary).");
            AsyncConsole.WriteLine("     Framework-dependent Debug also needs those libs on DYLD_FALLBACK_LIBRARY_PATH;");
            AsyncConsole.WriteLine("     Inspector/CLI re-launch with that automatically (or use `dotnet run` launchSettings).");
            AsyncConsole.WriteLine("     Manual: export DYLD_FALLBACK_LIBRARY_PATH=\"$(brew --prefix)/opt/libmsquic/lib:$(brew --prefix)/opt/openssl@3/lib\"");
        }
        if (OperatingSystem.IsWindows())
        {
            AsyncConsole.WriteLine("  Windows requires Windows 11 or Windows Server 2022+ (OS MsQuic).");
        }

        return 0;
    }

    private static async Task<int> InstallAsync()
    {
        if (QuicListener.IsSupported)
        {
            AsyncConsole.WriteLine("QuicListener.IsSupported is already true; nothing to install.");
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            AsyncConsole.WriteError(
                "Windows HTTP/3 uses OS MsQuic. Upgrade to Windows 11 or Windows Server 2022+.");
            return 1;
        }

        if (OperatingSystem.IsMacOS())
        {
            return await RunPackageInstallAsync(
                ResolveBrewCommand(),
                [InstallSubcommand, LibMsQuicPackage],
                $"Homebrew is required: https://brew.sh — then: brew {InstallSubcommand} {LibMsQuicPackage} openssl@3. " +
                "For local Debug builds, rebuild Inspector/CLI so natives are copied beside the binary.");
        }

        if (File.Exists("/etc/alpine-release") || LooksLikeMusl())
        {
            // libmsquic needs numactl + lttng-ust at runtime; RID zips no longer bundle them.
            return await RunPackageInstallAsync(
                "apk",
                ["add", "--no-cache", LibMsQuicPackage, "numactl", "lttng-ust"],
                $"Enable the Alpine community repo, then: apk add {LibMsQuicPackage} numactl lttng-ust");
        }

        if (File.Exists("/etc/debian_version") || HasCommand("apt-get"))
        {
            return await InstallDebianAsync();
        }

        if (HasCommand("dnf"))
        {
            return await RunPackageInstallAsync(
                "dnf",
                [InstallSubcommand, "-y", LibMsQuicPackage],
                $"Configure packages.microsoft.com for your distro, then: dnf {InstallSubcommand} {LibMsQuicPackage}");
        }

        if (HasCommand("zypper"))
        {
            return await RunPackageInstallAsync(
                "zypper",
                [InstallSubcommand, "-y", LibMsQuicPackage],
                $"Configure packages.microsoft.com for your distro, then: zypper {InstallSubcommand} {LibMsQuicPackage}");
        }

        AsyncConsole.WriteError(
            $"No supported package manager detected. Install {LibMsQuicPackage} manually or use a bundled RID zip.");
        return 1;
    }

    private static async Task<int> InstallDebianAsync()
    {
        // Register Microsoft package repo when missing, then apt-get install libmsquic.
        var id = ReadOsRelease("ID") ?? "ubuntu";
        var versionId = ReadOsRelease("VERSION_ID") ?? "22.04";
        var prodDeb = $"https://packages.microsoft.com/config/{id}/{versionId}/packages-microsoft-prod.deb";

        AsyncConsole.WriteLine($"Configuring Microsoft package repo ({id} {versionId})…");
        var tmp = Path.Combine(Path.GetTempPath(), "packages-microsoft-prod.deb");
        try
        {
            using (var http = new HttpClient())
            {
                var bytes = await http.GetByteArrayAsync(prodDeb);
                await File.WriteAllBytesAsync(tmp, bytes);
            }
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(
                $"Failed to download {prodDeb}: {ex.Message}. Install {LibMsQuicPackage} manually from packages.microsoft.com.");
            return 1;
        }

        var dpkg = await RunAsync("sudo", ["dpkg", "-i", tmp]);
        _ = dpkg;
        try { File.Delete(tmp); } catch { /* ignore */ }

        var update = await RunAsync("sudo", ["apt-get", "update"]);
        if (update != 0)
        {
            return update;
        }

        return await RunAsync("sudo", ["apt-get", InstallSubcommand, "-y", LibMsQuicPackage, "libnuma1"]);
    }

    private static async Task<int> RunPackageInstallAsync(string fileName, string[] args, string hint)
    {
        var isBrew = fileName == "brew" ||
                     fileName.EndsWith("/brew", StringComparison.Ordinal) ||
                     fileName.EndsWith("\\brew", StringComparison.Ordinal);
        if (isBrew)
        {
            if (!HasCommand("brew") && !File.Exists(fileName))
            {
                AsyncConsole.WriteError(hint);
                return 1;
            }
        }
        else if (!HasCommand(fileName))
        {
            AsyncConsole.WriteError(hint);
            return 1;
        }

        var prefix = fileName is "apk" or "dnf" or "zypper" ? "sudo" : null;
        if (prefix is not null)
        {
            return await RunAsync(prefix, [fileName, .. args]);
        }

        return await RunAsync(fileName, args);
    }

    /// <summary>Prefer PATH brew, then common prefixes (including user installs under ~/.homebrew).</summary>
    private static string ResolveBrewCommand()
    {
        if (HasCommand("brew"))
        {
            return "brew";
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".homebrew", "bin", "brew"),
                     "/opt/homebrew/bin/brew",
                     "/usr/local/bin/brew",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "brew";
    }

    private static async Task<int> RunAsync(string fileName, IReadOnlyList<string> args)
    {
        AsyncConsole.WriteLine($"> {fileName} {string.Join(' ', args)}");
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                AsyncConsole.WriteError($"Failed to start {fileName}.");
                return 1;
            }

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var outText = await stdout;
            var errText = await stderr;
            if (!string.IsNullOrWhiteSpace(outText))
            {
                AsyncConsole.Write(outText);
            }

            if (!string.IsNullOrWhiteSpace(errText))
            {
                AsyncConsole.WriteErrorRaw(errText);
            }

            if (p.ExitCode == 0)
            {
                AsyncConsole.WriteLine($"QuicListener.IsSupported (this process): {QuicListener.IsSupported}");
                AsyncConsole.WriteLine("If still false, restart the CLI so the loader picks up new libraries.");
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return 1;
        }
    }

    private static bool HasCommand(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeMusl()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = LddAbsolutePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
            return text.Contains("musl", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadOsRelease(string key)
    {
        const string path = "/etc/os-release";
        if (!File.Exists(path))
        {
            return null;
        }

        var prefix = key + "=";
        var line = File.ReadLines(path).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : line[prefix.Length..].Trim().Trim('"');
    }

    internal static string SuggestRid()
    {
        var arm = RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm;
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "osx-arm64" : "osx-x64";
        }

        if (File.Exists("/etc/alpine-release") || LooksLikeMusl())
        {
            return arm ? "linux-musl-arm64" : "linux-musl-x64";
        }

        return arm ? "linux-arm64" : "linux-x64";
    }
}
