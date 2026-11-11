using System.Reflection;
using System.Runtime.Loader;
using Titanium.Cli.Updates;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Cli.Config;

/// <summary>
/// Loads <c>Titanium.Plus.dll</c> into the default assembly context when it sits beside the executable.
    /// Plus stays in that context for the process (the same way the xDS and GeoIP satellites load) so cache
    /// middleware does not cross into a collectible context on every request. Dependencies are taken from
    /// <c>Titanium.Plus.files</c> or from a dll already beside the executable. Third-party script plugins keep
    /// their own collectible load. <c>titanium update --plus</c> runs in a separate process and does not load
    /// Plus before it swaps files.
/// </summary>
internal static class PlusLoader
{
    private static readonly System.Threading.Lock Gate = new();
    private static Assembly? _plusAssembly;
    private static string? _loadDirectory;
    private static HashSet<string>? _bundleNames;
    private static int _resolverRegistered;

    public static ITitaniumPlusModule? TryLoad(out string? warning) =>
        TryLoadFrom(AppContext.BaseDirectory, out warning);

    /// <summary>Load Plus from <paramref name="directory"/>. Tests stage a directory that is not the test host base.</summary>
    internal static ITitaniumPlusModule? TryLoadFrom(string directory, out string? warning)
    {
        warning = null;
        directory = Path.GetFullPath(directory);
        var dllPath = Path.Combine(directory, "Titanium.Plus.dll");
        if (!File.Exists(dllPath))
        {
            warning = "Plus features enabled but Titanium.Plus.dll was not found beside the executable. Install it with: titanium update --plus.";
            return null;
        }

        try
        {
            lock (Gate)
            {
                if (_plusAssembly is not null)
                {
                    return FindModule(_plusAssembly, ref warning);
                }

                var already = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(loaded =>
                    string.Equals(loaded.GetName().Name, "Titanium.Plus", StringComparison.OrdinalIgnoreCase));
                PublishBundleList(directory);
                EnsureResolver();
                if (already is not null)
                {
                    _plusAssembly = already;
                    return FindModule(already, ref warning);
                }

                PreloadSharedHostDependencies(directory);
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
                _plusAssembly = asm;
                return FindModule(asm, ref warning);
            }
        }
        catch (Exception ex)
        {
            warning = $"Failed to load Plus: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Resolves a Plus bundle dependency, or null when the simple name is not listed in
    /// <c>Titanium.Plus.files</c>. Used by the default-context resolver and by tests.
    /// </summary>
    internal static Assembly? TryResolveListedDependency(AssemblyName name) =>
        ResolveBundleDependency(AssemblyLoadContext.Default, name);

    private static void PublishBundleList(string directory)
    {
        _loadDirectory = directory;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listPath = Path.Combine(directory, UpdateCommand.PlusFileListName);
        if (File.Exists(listPath))
        {
            foreach (var raw in File.ReadAllLines(listPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.Contains("..", StringComparison.Ordinal) ||
                    line.IndexOfAny(['/', '\\']) >= 0)
                {
                    continue;
                }

                if (!line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(line[..^4]);
            }
        }

        _bundleNames = names;
    }

    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0)
        {
            AssemblyLoadContext.Default.Resolving += ResolveBundleDependency;
        }
    }

    private static Assembly? ResolveBundleDependency(AssemblyLoadContext context, AssemblyName name)
    {
        _ = context;
        var simple = name.Name;
        var directory = _loadDirectory;
        if (simple is null || directory is null)
        {
            return null;
        }

        var candidate = Path.Combine(directory, simple + ".dll");
        var listed = _bundleNames?.Contains(simple) == true;
        // The file list is the bundle contract. A dll that is already beside the CLI (a previous
        // install, or a harness that left a dependency in place without re-listing it) still has to load.
        // Names with no file here stay unresolved so the rest of the process is unchanged.
        if (!listed && !File.Exists(candidate))
        {
            return null;
        }

        if (!File.Exists(candidate))
        {
            return null;
        }

        var alreadyLoaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(loaded =>
            string.Equals(loaded.GetName().Name, simple, StringComparison.OrdinalIgnoreCase));
        if (alreadyLoaded is not null)
        {
            return alreadyLoaded;
        }

        return LoadFromPath(candidate);
    }

    private static void PreloadSharedHostDependencies(string directory)
    {
        foreach (var name in new[]
                 {
                     "Google.Protobuf", "Google.Api.CommonProtos", "Grpc.Core.Api", "Grpc.Net.Common",
                     "Grpc.Net.Client", "DnsClient",
                 })
        {
            if (AssemblyLoadContext.Default.Assemblies.Any(a =>
                    string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidate = Path.Combine(directory, name + ".dll");
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                LoadFromPath(candidate);
            }
            catch (Exception ex) when (IsTransientAssemblyLoad(ex) || ex is BadImageFormatException or FileLoadException)
            {
                // Best-effort; Resolving still loads the dependency when Plus asks for it.
            }
        }
    }

    private static Assembly LoadFromPath(string candidate)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            catch (Exception ex) when (IsTransientAssemblyLoad(ex) && attempt < attempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
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
