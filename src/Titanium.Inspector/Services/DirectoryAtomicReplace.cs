namespace Titanium.Inspector.Services;

/// <summary>
///     Swap a fully prepared staging directory into <paramref name="dest"/>, restoring the
///     previous install if the swap fails.
/// </summary>
internal static class DirectoryAtomicReplace
{
    public static void Commit(string staging, string dest)
    {
        var parent = Path.GetDirectoryName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? throw new InvalidOperationException("Destination has no parent directory");
        Directory.CreateDirectory(parent);

        var backup = Path.Combine(parent, Path.GetFileName(dest) + ".old-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(dest))
                Directory.Move(dest, backup);
            Directory.Move(staging, dest);
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(dest) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, dest); } catch { /* best-effort restore */ }
            }

            throw;
        }
    }

    public static void TryDelete(string path)
    {
        if (!Directory.Exists(path))
            return;
        try { Directory.Delete(path, recursive: true); } catch { /* ignore */ }
    }
}
