using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.InteropServices;

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
        Console.WriteLine("""
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
        await Console.Error.WriteLineAsync($"Unknown http3-deps subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static int Status()
    {
        var supported = QuicListener.IsSupported;
        Console.WriteLine($"QuicListener.IsSupported: {supported}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Arch: {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"Suggested RID: {SuggestRid()}");

        if (supported)
        {
            Console.WriteLine("HTTP/3 natives appear available (bundled zip or system MsQuic).");
            return 0;
        }

        Console.WriteLine("HTTP/3 is not available on this machine.");
        Console.WriteLine("Fixes:");
        Console.WriteLine("  1) Download the matching RID zip for your OS/arch (see /docs/http3).");
        Console.WriteLine("     Alpine/K8s musl images need linux-musl-x64 or linux-musl-arm64, not linux-x64.");
        Console.WriteLine("     RID zips ship MsQuic+OpenSSL only; install host deps if the loader still fails:");
        Console.WriteLine("       Ubuntu/Debian: libnuma1");
        Console.WriteLine("       Alpine:        numactl lttng-ust");
        Console.WriteLine($"  2) Or run: titanium http3-deps {InstallSubcommand}");
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("  Windows requires Windows 11 or Windows Server 2022+ (OS MsQuic).");
        }

        return 0;
    }

    private static async Task<int> InstallAsync()
    {
        if (QuicListener.IsSupported)
        {
            Console.WriteLine("QuicListener.IsSupported is already true; nothing to install.");
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                "Windows HTTP/3 uses OS MsQuic. Upgrade to Windows 11 or Windows Server 2022+.");
            return 1;
        }

        if (OperatingSystem.IsMacOS())
        {
            return await RunPackageInstallAsync(
                "brew",
                [InstallSubcommand, LibMsQuicPackage],
                $"Homebrew is required: https://brew.sh — then: brew {InstallSubcommand} {LibMsQuicPackage}");
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

        await Console.Error.WriteLineAsync(
            $"No supported package manager detected. Install {LibMsQuicPackage} manually or use a bundled RID zip.");
        return 1;
    }

    private static async Task<int> InstallDebianAsync()
    {
        // Register Microsoft package repo when missing, then apt-get install libmsquic.
        var id = ReadOsRelease("ID") ?? "ubuntu";
        var versionId = ReadOsRelease("VERSION_ID") ?? "22.04";
        var prodDeb = $"https://packages.microsoft.com/config/{id}/{versionId}/packages-microsoft-prod.deb";

        Console.WriteLine($"Configuring Microsoft package repo ({id} {versionId})…");
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
            await Console.Error.WriteLineAsync(
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
        if (!HasCommand(fileName) && fileName != "brew")
        {
            await Console.Error.WriteLineAsync(hint);
            return 1;
        }

        if (fileName == "brew" && !HasCommand("brew"))
        {
            await Console.Error.WriteLineAsync(hint);
            return 1;
        }

        var prefix = fileName is "apk" or "dnf" or "zypper" ? "sudo" : null;
        if (prefix is not null)
        {
            return await RunAsync(prefix, [fileName, .. args]);
        }

        return await RunAsync(fileName, args);
    }

    private static async Task<int> RunAsync(string fileName, IReadOnlyList<string> args)
    {
        Console.WriteLine($"> {fileName} {string.Join(' ', args)}");
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
                await Console.Error.WriteLineAsync($"Failed to start {fileName}.");
                return 1;
            }

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var outText = await stdout;
            var errText = await stderr;
            if (!string.IsNullOrWhiteSpace(outText))
            {
                Console.Write(outText);
            }

            if (!string.IsNullOrWhiteSpace(errText))
            {
                await Console.Error.WriteAsync(errText);
            }

            if (p.ExitCode == 0)
            {
                Console.WriteLine($"QuicListener.IsSupported (this process): {QuicListener.IsSupported}");
                Console.WriteLine("If still false, restart the CLI so the loader picks up new libraries.");
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
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
