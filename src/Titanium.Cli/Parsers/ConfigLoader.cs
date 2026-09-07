using System.Text.Json;
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
            !name.StartsWith("twp", StringComparison.OrdinalIgnoreCase) &&
            !LooksLikeNativeTwpJson(path))
        {
            // Prefer reverse-proxy document dialect for generic *.json that are only
            // listeners/routes/clusters. Native TwpConfig (plus/server/logging/…) must not
            // silently parse as reverse-proxy — that dialect drops Plus and other sections.
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

    /// <summary>
    /// True when the JSON root looks like a native <see cref="TwpConfig"/> (not a bare
    /// reverse-proxy document). Filename conventions alone are not enough — users often
    /// name configs <c>config.json</c> / <c>edge.json</c> while still including <c>plus:</c>.
    /// </summary>
    internal static bool LooksLikeNativeTwpJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("schemaVersion") ||
                    prop.NameEquals("plus") ||
                    prop.NameEquals("server") ||
                    prop.NameEquals("logging") ||
                    prop.NameEquals("certificates") ||
                    prop.NameEquals("staticFiles"))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
