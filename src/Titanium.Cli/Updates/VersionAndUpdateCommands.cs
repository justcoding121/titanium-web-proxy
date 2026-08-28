using System.Reflection;
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
            Console.Error.WriteLine("Unable to query update feed.");
            return 1;
        }

        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remote = Version.TryParse(manifest.Version, out var v) ? v : new Version(0, 0);
        Console.WriteLine($"Remote Cli ({channel}): {manifest.Version}");
        if (remote > local)
        {
            Console.WriteLine("A newer Cli build is available. Run: titanium update");
            return 2;
        }

        Console.WriteLine("Cli is up to date.");
        return 0;
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
            Console.Error.WriteLine("Unable to query update feed.");
            return 1;
        }

        if (plus)
        {
            Console.WriteLine($"Plus update ({channel}): download asset from GitHub Releases and place Titanium.Plus.dll beside this executable, then restart.");
            if (manifest.Products?.Plus?.Asset?.Url is string url)
            {
                Console.WriteLine(url);
            }

            return 0;
        }

        Console.WriteLine($"Cli update ({channel}): prefer winget upgrade or re-download from GitHub Releases.");
        Console.WriteLine($"Remote version: {manifest.Version}");
        return 0;
    }
}

internal sealed class UpdateFeedClient
{
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
            var url = string.IsNullOrEmpty(feed)
                ? "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest"
                : feed;

            if (_channel.Equals("beta", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(feed))
            {
                url = "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases";
            }

            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");
            var json = await http.GetStringAsync(url);
            if (_channel.Equals("beta", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(feed))
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                    {
                        return new ReleaseManifest
                        {
                            Version = el.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0",
                            Channel = "beta",
                        };
                    }
                }

                return null;
            }

            using var latest = JsonDocument.Parse(json);
            if (latest.RootElement.TryGetProperty("tag_name", out var tag))
            {
                return new ReleaseManifest
                {
                    Version = tag.GetString()?.TrimStart('v') ?? "0.0.0",
                    Channel = _channel,
                };
            }

            return JsonSerializer.Deserialize<ReleaseManifest>(json);
        }
        catch
        {
            return null;
        }
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
    public PlusBlock? Plus { get; set; }
}

internal sealed class PlusBlock
{
    public AssetBlock? Asset { get; set; }
}

internal sealed class AssetBlock
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}
