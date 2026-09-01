using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Configuration.Parsers;

namespace Titanium.Cli.Parsers;

internal sealed class LoadedConfig
{
    public required TwpConfig Config { get; init; }
    public required string Path { get; init; }
    public required string Dialect { get; init; }
}

internal static class ConfigLoader
{
    public static LoadedConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Config file not found.", path);
        }

        var ext = Path.GetExtension(path);
        var name = Path.GetFileName(path);

        if (name.EndsWith(".twp", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("site-file", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadedConfig
            {
                Path = path,
                Dialect = "site-file",
                Config = SiteFileReader.ParseFile(path),
            };
        }

        if (ext.Equals(".conf", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadedConfig
            {
                Path = path,
                Dialect = "http-server",
                Config = HttpServerConfigReader.ParseFile(path),
            };
        }

        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("twp", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer reverse-proxy document dialect for generic *.json; native twp.json uses TwpConfigLoader.
            try
            {
                return new LoadedConfig
                {
                    Path = path,
                    Dialect = "json-reverse-proxy",
                    Config = JsonReverseProxyDocument.ParseFile(path),
                };
            }
            catch
            {
                // fall through to native
            }
        }

        return new LoadedConfig
        {
            Path = path,
            Dialect = "twp-native",
            Config = TwpConfigLoader.LoadFile(path),
        };
    }
}
