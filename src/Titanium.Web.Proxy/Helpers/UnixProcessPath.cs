using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>Resolves well-known Unix binaries to absolute paths for Process.Start (S4036).</summary>
internal static class UnixProcessPath
{
    internal static string? Resolve(params string[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
}
