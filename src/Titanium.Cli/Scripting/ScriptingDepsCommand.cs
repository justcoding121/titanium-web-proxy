using System.Runtime.InteropServices;

namespace Titanium.Cli.Scripting;

/// <summary>
/// Reports whether the scripting natives (Titanium Lua host shim + Wasmtime C API) load on this host and
/// prints per-OS fixes. Modeled on <c>Http3DepsCommand</c>; the RID zips bundle both natives
/// (see tools/packaging/scripting-native.lock.json), so <c>install</c> only documents the remaining routes.
/// </summary>
internal static class ScriptingDepsCommand
{
    private const string WasmtimeLabel = "Wasmtime";
    private const string InstallSubcommand = "install";

    private sealed record NativeProbe(string Label, string[] FileNames);

    public static Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length >= 2 && CliHelp.IsHelpToken(args[1]))
        {
            return Task.FromResult(PrintHelp());
        }

        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        if (args.Length > 2 && CliHelp.RequestsHelp(args.AsSpan(2)))
        {
            return Task.FromResult(PrintHelp());
        }

        return Task.FromResult(sub switch
        {
            "status" => Status(),
            InstallSubcommand => Install(),
            "help" or "-h" or "--help" => PrintHelp(),
            _ => Unknown(sub),
        });
    }

    private static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium scripting-deps status|install

              status   Report whether the Lua (Titanium shim) and Wasmtime natives load, with per-OS fixes.
              install  Print install hints (brew / apt / RID zip / bundle script); exits 0 when nothing is missing.

            Prefer the matching CLI RID zip (win-x64, linux-x64, linux-musl-x64, osx-arm64, …), which bundles
            lua54 / liblua54 and wasmtime / libwasmtime next to the binary. Building from source:
            tools/packaging/bundle-scripting-native.ps1 -Rid <rid> -PublishDir <dir> (see tools/scripting/README.md).
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static int Unknown(string sub)
    {
        AsyncConsole.WriteError($"Unknown scripting-deps subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static int Status()
    {
        AsyncConsole.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        AsyncConsole.WriteLine($"Arch: {RuntimeInformation.OSArchitecture}");
        AsyncConsole.WriteLine($"Suggested RID: {SuggestRid()}");

        var missing = new List<NativeProbe>();
        foreach (var probe in Probes())
        {
            var path = TryLoad(probe.FileNames);
            if (path is not null)
            {
                AsyncConsole.WriteLine($"{probe.Label}: loaded ({path})");
            }
            else
            {
                AsyncConsole.WriteLine($"{probe.Label}: not found (looked for {string.Join(", ", probe.FileNames)})");
                missing.Add(probe);
            }
        }

        if (missing.Count == 0)
        {
            AsyncConsole.WriteLine("Scripting natives appear available (bundled zip or system install).");
            return 0;
        }

        AsyncConsole.WriteLine("Scripting is not fully available on this machine.");
        AsyncConsole.WriteLine("Fixes:");
        AsyncConsole.WriteLine("  1) Download the matching RID zip for your OS/arch; it bundles both natives beside the binary.");
        AsyncConsole.WriteLine("     Alpine/K8s musl images need linux-musl-x64 or linux-musl-arm64, not linux-x64.");
        WriteOsFixes(missing);
        AsyncConsole.WriteLine($"  2) Or see: titanium scripting-deps {InstallSubcommand}");
        return 0;
    }

    private static int Install()
    {
        var missing = Probes().Where(p => TryLoad(p.FileNames) is null).ToList();
        if (missing.Count == 0)
        {
            AsyncConsole.WriteLine("Lua and Wasmtime natives already load; nothing to install.");
            return 0;
        }

        AsyncConsole.WriteLine($"Missing: {string.Join(", ", missing.Select(m => m.Label))}");
        AsyncConsole.WriteLine("Titanium does not install these through a system package manager: the Lua native must include the");
        AsyncConsole.WriteLine("Titanium host shim (ctx + libraries), which distro/brew `lua` packages do not provide.");
        AsyncConsole.WriteLine("Supported routes:");
        AsyncConsole.WriteLine($"  - Download the {SuggestRid()} CLI zip (bundles both natives).");
        AsyncConsole.WriteLine("  - Fetch/build from the pinned lock file:");
        AsyncConsole.WriteLine($"      pwsh tools/packaging/bundle-scripting-native.ps1 -Rid {SuggestRid()} -PublishDir <dir containing titanium>");
        AsyncConsole.WriteLine("    (details: tools/scripting/README.md)");
        WriteOsFixes(missing);
        AsyncConsole.WriteError("Nothing was installed. Re-run `titanium scripting-deps status` after copying the natives.");
        return 1;
    }

    private static void WriteOsFixes(List<NativeProbe> missing)
    {
        var wantsWasmtime = missing.Any(m => m.Label == WasmtimeLabel);
        var wantsLua = missing.Any(m => m.Label == "Lua");

        if (OperatingSystem.IsWindows())
        {
            AsyncConsole.WriteLine("  Windows: keep lua54.dll and wasmtime.dll next to titanium.exe (win-x64 zip), or on PATH.");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            AsyncConsole.WriteLine("  macOS:");
            if (wantsWasmtime)
            {
                AsyncConsole.WriteLine("    Wasmtime: brew install wasmtime may provide libwasmtime.dylib; otherwise use the zip / bundle script.");
            }

            if (wantsLua)
            {
                AsyncConsole.WriteLine("    Lua: build liblua54.dylib with the Titanium shim (bundle script on macOS needs Xcode CLT: xcode-select --install).");
            }

            return;
        }

        if (File.Exists("/etc/alpine-release"))
        {
            AsyncConsole.WriteLine("  Alpine: apk add build-base (to build the Lua shim); Wasmtime: use the linux-musl zip or bundle script.");
            return;
        }

        AsyncConsole.WriteLine("  Linux:");
        if (wantsLua)
        {
            AsyncConsole.WriteLine("    Lua shim build deps (Debian/Ubuntu): sudo apt-get install -y build-essential");
        }

        if (wantsWasmtime)
        {
            AsyncConsole.WriteLine("    Wasmtime: no distro package is assumed; use the zip or tools/packaging/bundle-scripting-native.ps1.");
        }

        AsyncConsole.WriteLine("    Keep the .so files next to the titanium binary (RPATH is $ORIGIN) or on LD_LIBRARY_PATH.");
    }

    private static IEnumerable<NativeProbe> Probes()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new NativeProbe("Lua", ["lua54.dll"]);
            yield return new NativeProbe(WasmtimeLabel, ["wasmtime.dll"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return new NativeProbe("Lua", ["liblua54.dylib"]);
            yield return new NativeProbe(WasmtimeLabel, ["libwasmtime.dylib"]);
        }
        else
        {
            yield return new NativeProbe("Lua", ["liblua54.so"]);
            yield return new NativeProbe(WasmtimeLabel, ["libwasmtime.so"]);
        }
    }
    /// <summary>Loads (and immediately frees) the first candidate that resolves; returns a display path or null.</summary>
    private static string? TryLoad(IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            var local = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(local) && NativeLibrary.TryLoad(local, out var h1))
            {
                NativeLibrary.Free(h1);
                return local;
            }

            if (NativeLibrary.TryLoad(name, out var h2))
            {
                NativeLibrary.Free(h2);
                return name + " (system search path)";
            }
        }

        return null;
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

        if (File.Exists("/etc/alpine-release"))
        {
            return arm ? "linux-musl-arm64" : "linux-musl-x64";
        }

        return arm ? "linux-arm64" : "linux-x64";
    }
}
