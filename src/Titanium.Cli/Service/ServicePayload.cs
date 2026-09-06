namespace Titanium.Cli.Service;

/// <summary>
/// macOS LaunchDaemons cannot read TCC-protected locations (Documents, Desktop, Downloads),
/// even as root. Machine-service install copies the CLI payload to a system path.
/// </summary>
internal static class ServicePayload
{
    public static string MacOsDaemonPayloadDirectory(string serviceName) =>
        "/Library/Application Support/Titanium/services/" + serviceName;

    public static string? DiscoverAppDirectory(IReadOnlyList<string> programPrefix)
    {
        foreach (var part in programPrefix)
        {
            if (part.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetFullPath(part));
        }

        if (programPrefix.Count > 0)
            return Path.GetDirectoryName(Path.GetFullPath(programPrefix[0]));

        return null;
    }

    public static string[] RemapPrefix(IReadOnlyList<string> programPrefix, string sourceDir, string destDir)
    {
        var src = TrimSep(Path.GetFullPath(sourceDir));
        var dst = TrimSep(Path.GetFullPath(destDir));
        var result = new string[programPrefix.Count];
        for (var i = 0; i < programPrefix.Count; i++)
        {
            var full = Path.GetFullPath(programPrefix[i]);
            if (full.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(src, StringComparison.OrdinalIgnoreCase))
            {
                result[i] = dst + full[src.Length..];
            }
            else
            {
                result[i] = full;
            }
        }

        return result;
    }

    public static void CopyDirectory(string sourceDir, string destDir)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        destDir = Path.GetFullPath(destDir);
        if (sourceDir.Equals(destDir, StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort uninstall.
        }
    }

    private static string TrimSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
