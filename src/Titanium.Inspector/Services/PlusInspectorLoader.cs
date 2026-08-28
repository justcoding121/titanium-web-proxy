using System.Reflection;
using System.Runtime.Loader;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Inspector.Services;

/// <summary>
/// Loads Plus for <see cref="IPlusInspectorViewProvider"/> only — never calls Apply.
/// </summary>
public static class PlusInspectorLoader
{
    public static IReadOnlyList<object> TryLoadPanels(out string? warning)
    {
        warning = null;
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        if (!File.Exists(dllPath))
        {
            return Array.Empty<object>();
        }

        try
        {
            var alc = new AssemblyLoadContext("Titanium.Plus.Inspector", isCollectible: true);
            var asm = alc.LoadFromAssemblyPath(dllPath);
            var panels = new List<object>();
            var abstractionsVersion = typeof(IPlusInspectorViewProvider).Assembly.GetName().Version ?? new Version(0, 0);

            foreach (var type in asm.GetExportedTypes())
            {
                if (!typeof(IPlusInspectorViewProvider).IsAssignableFrom(type) || type.IsAbstract)
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not IPlusInspectorViewProvider provider)
                {
                    continue;
                }

                if (abstractionsVersion < provider.RequiredAbstractionsVersion)
                {
                    warning =
                        $"Plus Inspector views require Abstractions {provider.RequiredAbstractionsVersion} but host has {abstractionsVersion}; skipping.";
                    return Array.Empty<object>();
                }

                panels.AddRange(provider.CreatePanels(new InspectorPanelContext
                {
                    HostWindow = new object(),
                }));
            }

            return panels;
        }
        catch (Exception ex)
        {
            warning = $"Failed to load Plus Inspector views: {ex.Message}";
            return Array.Empty<object>();
        }
    }
}
