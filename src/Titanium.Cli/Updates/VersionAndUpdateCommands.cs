using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Titanium.Cli.Config;

namespace Titanium.Cli.Updates;

internal static class VersionCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var check = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);
        var channel = ParseChannel(args);

        PrintLocalVersions(plus);

        if (!check)
        {
            return 0;
        }

        var client = new UpdateFeedClient(channel);
        var manifest = await client.TryGetManifestAsync();
        if (manifest is null)
        {
            await Console.Error.WriteLineAsync("Unable to query update feed.");
            return 1;
        }

        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remote = Version.TryParse(StripPrerelease(manifest.Version), out var v) ? v : new Version(0, 0);
        Console.WriteLine($"Remote Cli ({channel}): {manifest.Version}");

        var exit = 0;
        if (remote > local)
        {
            Console.WriteLine("A newer Cli build is available. Run: titanium update");
            exit = 2;
        }
        else
        {
            Console.WriteLine("Cli is up to date.");
        }

        if (plus)
        {
            var plusLocal = TryGetLocalPlusVersion();
            var plusRemote = manifest.Products?.Plus?.Version;
            if (!string.IsNullOrEmpty(plusRemote) &&
                Version.TryParse(StripPrerelease(plusRemote), out var pr) &&
                (plusLocal is null || pr > plusLocal))
            {
                Console.WriteLine($"A newer Plus build is available ({plusRemote}). Run: titanium update --plus");
                exit = 2;
            }
            else if (plusLocal is not null)
            {
                Console.WriteLine($"Plus is up to date ({plusLocal}).");
            }
        }

        return exit;
    }

    private static Version? TryGetLocalPlusVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return AssemblyName.GetAssemblyName(path).Version;
        }
        catch
        {
            return null;
        }
    }

    private static void PrintLocalVersions(bool includePlus)
    {
        void Print(string name, Assembly? asm)
        {
            var ver = asm?.GetName().Version?.ToString() ?? "(not loaded)";
            Console.WriteLine($"{name}: {ver}");
        }

        Print("Cli", typeof(Program).Assembly);
        Print("Core", typeof(Titanium.Web.Proxy.ProxyServer).Assembly);
        Print("Abstractions", typeof(Titanium.Web.Proxy.Abstractions.ReverseProxyOptions).Assembly);
        Print("Configuration", typeof(Titanium.Web.Proxy.Configuration.TwpConfigLoader).Assembly);

        if (includePlus || File.Exists(Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll")))
        {
            var module = PlusLoader.TryLoad(out var warning);
            if (warning is not null)
            {
                Console.WriteLine($"Plus: {warning}");
            }
            else if (module is not null)
            {
                Console.WriteLine($"Plus: {module.GetType().Assembly.GetName().Version} (RequiredAbstractions={module.RequiredAbstractionsVersion})");
            }
            else
            {
                Console.WriteLine("Plus: not present");
            }
        }
    }

    internal static string ParseChannel(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--channel" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return Environment.GetEnvironmentVariable("TITANIUM_UPDATE_CHANNEL") ?? "stable";
    }

    internal static string StripPrerelease(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "0.0.0";
        }

        var dash = version.IndexOf('-');
        return dash > 0 ? version[..dash] : version.TrimStart('v');
    }
}

internal static class UpdateCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);
        var channel = VersionCommand.ParseChannel(args);
        var client = new UpdateFeedClient(channel);
        var manifest = await client.TryGetManifestAsync();
        if (manifest is null)
        {
            await Console.Error.WriteLineAsync("Unable to query update feed.");
            return 1;
        }

        if (plus)
        {
            return await UpdatePlusAsync(manifest);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var winget = ResolveWingetPath();
                if (winget is null)
                {
                    throw new FileNotFoundException("winget.exe not found");
                }

                var psi = new ProcessStartInfo(winget, "upgrade --id justcoding121.TitaniumCli -e --accept-package-agreements --accept-source-agreements")
                {
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode == 0)
                    {
                        Console.WriteLine("Updated via winget.");
                        return 0;
                    }
                }
            }
            catch
            {
                // fall through to download
            }
        }

        var rid = ResolveRid();
        var asset = manifest.Products?.Cli?.Assets?.GetValueOrDefault(rid);
        if (asset?.Url is null)
        {
            Console.WriteLine($"Cli update ({channel}): re-download Titanium.Cli-{rid}.zip from GitHub Releases.");
            Console.WriteLine($"Remote version: {manifest.Version}");
            return 0;
        }

        var destZip = Path.Combine(Path.GetTempPath(), $"Titanium.Cli-{rid}-{manifest.Version}.zip");
        Console.WriteLine($"Downloading {asset.Url}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");
            var bytes = await http.GetByteArrayAsync(asset.Url);
            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await Console.Error.WriteLineAsync("SHA256 mismatch — aborting update.");
                    return 1;
                }
            }

            await File.WriteAllBytesAsync(destZip, bytes);
        }

        Console.WriteLine($"Downloaded to {destZip}. Extract over the install directory and restart.");
        Console.WriteLine("If this process is locked, stop titanium and unzip manually.");
        return 0;
    }

    private static async Task<int> UpdatePlusAsync(ReleaseManifest manifest)
    {
        var asset = manifest.Products?.Plus?.Asset;
        if (asset?.Url is null)
        {
            Console.WriteLine("Plus update: download Titanium.Plus.dll from GitHub Releases and place beside this executable, then restart.");
            return 0;
        }

        var dest = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        var backup = dest + ".bak";
        Console.WriteLine($"Downloading {asset.Url}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");
        var bytes = await http.GetByteArrayAsync(asset.Url);
        if (!string.IsNullOrEmpty(asset.Sha256))
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync("SHA256 mismatch — aborting Plus update.");
                return 1;
            }
        }

        if (File.Exists(dest))
        {
            File.Copy(dest, backup, overwrite: true);
        }

        await File.WriteAllBytesAsync(dest + ".new", bytes);
        Console.WriteLine($"Wrote {dest}.new — stop `titanium run` then rename to Titanium.Plus.dll (backup at .bak).");
        return 0;
    }

    private static string ResolveRid()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx-x64";
        }

        return "linux-x64";
    }

    private static string? ResolveWingetPath()
    {
        var localApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");
        if (File.Exists(localApps))
        {
            return localApps;
        }

        return TryFindWingetUnderProgramFiles();
    }

    private static string? TryFindWingetUnderProgramFiles()
    {
        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (!Directory.Exists(programFiles))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(programFiles, "winget.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            // WindowsApps may deny enumeration.
            return null;
        }
    }
}

internal sealed class UpdateFeedClient
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _channel;

    public UpdateFeedClient(string channel) => _channel = channel;

    public async Task<ReleaseManifest?> TryGetManifestAsync()
    {
        var feed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        if (feed == string.Empty)
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");

            if (!string.IsNullOrEmpty(feed))
            {
                var json = await http.GetStringAsync(feed);
                return JsonSerializer.Deserialize<ReleaseManifest>(json, ManifestJson);
            }

            // Prefer release-manifest.json asset on latest (or first prerelease for beta).
            var api = _channel.Equals("beta", StringComparison.OrdinalIgnoreCase)
                ? "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases"
                : "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest";

            var payload = await http.GetStringAsync(api);
            using var doc = JsonDocument.Parse(payload);
            if (!TrySelectRelease(doc.RootElement, out var release))
            {
                return null;
            }

            var version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0";
            var fromAsset = await TryLoadManifestAssetAsync(http, release, version);
            return fromAsset ?? new ReleaseManifest { Version = version, Channel = _channel };
        }
        catch
        {
            return null;
        }
    }

    private bool TrySelectRelease(JsonElement root, out JsonElement release)
    {
        release = default;
        if (_channel.Equals("beta", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                {
                    release = el;
                    return true;
                }
            }

            return false;
        }

        release = root;
        return true;
    }

    private async Task<ReleaseManifest?> TryLoadManifestAssetAsync(
        HttpClient http,
        JsonElement release,
        string version)
    {
        if (!release.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.GetProperty("browser_download_url").GetString();
            if (url is null)
            {
                continue;
            }

            var manifestJson = await http.GetStringAsync(url);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, ManifestJson);
            if (manifest is null)
            {
                continue;
            }

            manifest.Version ??= version;
            manifest.Channel ??= _channel;
            return manifest;
        }

        return null;
    }
}

internal sealed class ReleaseManifest
{
    public string? Version { get; set; }
    public string? Channel { get; set; }
    public ProductsBlock? Products { get; set; }
}

internal sealed class ProductsBlock
{
    public CliBlock? Cli { get; set; }
    public PlusProductBlock? Plus { get; set; }
}

internal sealed class CliBlock
{
    public Dictionary<string, AssetBlock>? Assets { get; set; }
}

internal sealed class PlusProductBlock
{
    public string? Version { get; set; }
    public AssetBlock? Asset { get; set; }
}

internal sealed class AssetBlock
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}
