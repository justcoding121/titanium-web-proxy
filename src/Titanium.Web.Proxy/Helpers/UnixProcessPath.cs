using System;
using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>Resolves well-known Unix binaries to absolute paths for Process.Start (S4036).</summary>
internal static class UnixProcessPath
{
    /// <summary>Unit tests substitute a local executable so launch branches run on Windows.</summary>
    internal static Func<string[], string?>? ResolveOverrideForTests { get; set; }

    internal static string? Resolve(params string[] candidates)
    {
        if (ResolveOverrideForTests is not null)
            return ResolveOverrideForTests(candidates);

        return candidates.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
    }
}
