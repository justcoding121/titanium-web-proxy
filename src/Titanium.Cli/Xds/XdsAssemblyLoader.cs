using System.Reflection;
using System.Runtime.Loader;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Xds;

internal static class XdsAssemblyLoader
{
    private static readonly object Gate = new();
    private static Assembly? _xdsAssembly;

    public static object? TryCreateHost(XdsConfig config, TwpConfig bootstrap, string stateDirectory, Action<TwpConfig> onConfigReady)
    {
        var assembly = EnsureLoaded();
        var type = assembly.GetType("Titanium.Xds.XdsControlPlaneHost", throwOnError: true)!;
        return Activator.CreateInstance(type, config, bootstrap, stateDirectory, onConfigReady);
    }

    private static Assembly EnsureLoaded()
    {
        var existing = Volatile.Read(ref _xdsAssembly);
        if (existing is not null)
        {
            return existing;
        }

        lock (Gate)
        {
            existing = _xdsAssembly;
            if (existing is not null)
            {
                return existing;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "Titanium.Xds.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("xDS support assembly was not found beside the CLI.", path);
            }

            existing = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            Volatile.Write(ref _xdsAssembly, existing);
            return existing;
        }
    }
}
