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
            alc.Resolving += ResolveBesideExecutable;
            return FindModule(alc.LoadFromAssemblyPath(dllPath), ref warning);
        }
        catch (Exception ex)
        {
            warning = $"Failed to load Plus: {ex.Message}";
            return null;
        }
    }

    private static Assembly? ResolveBesideExecutable(AssemblyLoadContext context, AssemblyName name)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        return File.Exists(candidate) ? LoadDependency(context, candidate) : null;
    }

    /// <summary>
    ///     <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> is the supported call from
    ///     <see cref="AssemblyLoadContext.Resolving"/>. <c>LoadFromStream</c> there throws
    ///     "An operation is not legal in the current state". Retry that same message: on macOS
    ///     several CLI processes mapping one dependency can fail it transiently.
    /// </summary>
    private static Assembly LoadDependency(AssemblyLoadContext context, string candidate)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            catch (Exception ex) when (attempt < attempts && IsTransientAssemblyLoad(ex))
            {
                Thread.Sleep(50 * attempt);
            }
        }

        throw new InvalidOperationException("Plus dependency was not loaded: " + candidate);
    }

    private static ITitaniumPlusModule? FindModule(Assembly asm, ref string? warning)
    {
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

    private static bool IsTransientAssemblyLoad(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("not legal in the current state", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
