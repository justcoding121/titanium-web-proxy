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
        // Prefer an assembly already in the default context. Titanium.Xds is copied beside the CLI with
        // Google.Protobuf; loading that same path into the Plus collectible ALC fails on macOS with
        // "An operation is not legal in the current state" (0x80131509). Collectible contexts may
        // reference default-context assemblies.
        foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        if (!File.Exists(candidate))
        {
            return null;
        }

        // Shared host satellites (xDS) must stay in the default context so Plus and Xds see one copy.
        if (IsSharedHostDependency(name.Name))
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
        }

        return LoadDependency(context, candidate);
    }

    private static bool IsSharedHostDependency(string? simpleName) =>
        simpleName is "Google.Protobuf" or "Grpc.Net.Client" or "Grpc.Net.Common" or "Grpc.Core.Api"
            or "Grpc.Net.Client.Web";

    /// <summary>
    ///     <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> is the supported call from
    ///     <see cref="AssemblyLoadContext.Resolving"/>. <c>LoadFromStream</c> there throws
    ///     "An operation is not legal in the current state". Retry that same message: on macOS
    ///     several CLI processes mapping one dependency can fail it transiently. If the collectible
    ///     load keeps failing, fall back to the default context (same path the xDS satellite uses).
    /// </summary>
    private static Assembly LoadDependency(AssemblyLoadContext context, string candidate)
    {
        const int attempts = 5;
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            catch (Exception ex) when (IsTransientAssemblyLoad(ex))
            {
                last = ex;
                if (attempt < attempts)
                {
                    Thread.Sleep(50 * attempt);
                    continue;
                }

                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }
                catch (Exception fallback)
                {
                    throw new InvalidOperationException(
                        "Plus dependency was not loaded: " + candidate, fallback);
                }
            }
        }

        throw new InvalidOperationException("Plus dependency was not loaded: " + candidate, last);
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
