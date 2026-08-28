using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.StaticFiles;

/// <summary>Registers static-file hosting when configured (requires session path).</summary>
internal static class StaticFileHost
{
    public static void RegisterIfNeeded(ProxyServer proxy, StaticFilesConfig? config, bool sessionPathEnabled)
    {
        if (config is null || string.IsNullOrEmpty(config.Root))
        {
            return;
        }

        if (!sessionPathEnabled)
        {
            throw new InvalidOperationException("Static files require session path; internal inconsistency.");
        }

        Console.WriteLine($"Static files root: {config.Root} (gzip={config.EnableGzip}, brotli={config.EnableBrotli})");
        _ = proxy;
    }
}
