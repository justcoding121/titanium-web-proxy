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
                // Bytes, not LoadFromAssemblyPath: several CLI processes loading the same
                // dependency (Google.Api.CommonProtos) via a memory-mapped path race on macOS
                // and throw "An operation is not legal in the current state".
                return File.Exists(candidate) ? LoadFromBytes(alc, candidate) : null;
            };

            var asm = LoadFromBytes(alc, dllPath);
            foreach (var type in asm.GetExportedTypes())
            {
                if (!typeof(ITitaniumPlusModule).IsAssignableFrom(type) || type.IsAbstract)
                {
                    continue;
                }

                // Only modules with a public parameterless ctor (skip accidental assignable types).
                if (type.GetConstructor(Type.EmptyTypes) is null)
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

    private static Assembly LoadFromBytes(AssemblyLoadContext alc, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                return alc.LoadFromStream(new MemoryStream(bytes, writable: false));
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }
}
