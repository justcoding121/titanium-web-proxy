using System.Reflection;
using System.Runtime.Loader;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Cli.Geo;

/// <summary>Loads <c>Titanium.Cli.GeoIp.dll</c> on first use so MaxMind.Db stays off the default CLI path.</summary>
internal static class GeoIpAssemblyLoader
{
    private static readonly object Gate = new();
    private static Assembly? _geoIpAssembly;

    public static IGeoIpResolver CreateMmdbResolver(string path)
    {
        var assembly = EnsureLoaded();
        var type = assembly.GetType("Titanium.Cli.GeoIp.MmdbGeoIpResolver", throwOnError: true)!;
        return (IGeoIpResolver)Activator.CreateInstance(type, path)!;
    }

    private static Assembly EnsureLoaded()
    {
        var existing = Volatile.Read(ref _geoIpAssembly);
        if (existing is not null)
        {
            return existing;
        }

        lock (Gate)
        {
            existing = _geoIpAssembly;
            if (existing is not null)
            {
                return existing;
            }

            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "Titanium.Cli.GeoIp.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("GeoIP support assembly was not found beside the CLI.", path);
            }

            existing = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            Volatile.Write(ref _geoIpAssembly, existing);
            return existing;
        }
    }
}
