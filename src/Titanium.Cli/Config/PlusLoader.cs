using System.Reflection;
using System.Runtime.Loader;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Cli.Config;

/// <summary>
/// Loads Titanium.Plus.dll via a collectible ALC when present beside the exe and features are enabled.
/// </summary>
internal static class PlusLoader
{
    public static ITitaniumPlusModule? TryLoad(out string? warning)
    {
        warning = null;
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        if (!File.Exists(dllPath))
        {
            warning = "Plus features enabled but Titanium.Plus.dll was not found beside the executable.";
            return null;
        }

        try
        {
            var alc = new AssemblyLoadContext("Titanium.Plus", isCollectible: true);
            alc.Resolving += (_, name) =>
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? alc.LoadFromAssemblyPath(candidate) : null;
            };

            var asm = alc.LoadFromAssemblyPath(dllPath);
            foreach (var type in asm.GetExportedTypes())
            {
                if (!typeof(ITitaniumPlusModule).IsAssignableFrom(type) || type.IsAbstract)
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not ITitaniumPlusModule module)
                {
                    continue;
                }

                var abstractionsVersion = typeof(ITitaniumPlusModule).Assembly.GetName().Version ?? new Version(0, 0);
                if (abstractionsVersion < module.RequiredAbstractionsVersion)
                {
                    warning =
                        $"Plus requires Abstractions {module.RequiredAbstractionsVersion} but host has {abstractionsVersion}; skipping Plus.";
                    return null;
                }

                return module;
            }

            warning = "Titanium.Plus.dll did not export ITitaniumPlusModule.";
            return null;
        }
        catch (Exception ex)
        {
            warning = $"Failed to load Plus: {ex.Message}";
            return null;
        }
    }
}
