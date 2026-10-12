namespace Titanium.Inspector.Services;

/// <summary>
/// Pure helpers for Inspector dated log files: default folder, names, prune, and path migration.
/// </summary>
public static class InspectorLogFiles
{
    public const string FilePrefix = "titanium-inspector-";
    public const string LegacyFileName = "titanium-inspector.log";

    public static string DefaultFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector",
            "logs");

    /// <summary>Resolves the configured folder, falling back to <see cref="DefaultFolder"/>.</summary>
    public static string ResolveFolder(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultFolder();
        }

        return Path.GetFullPath(configured.Trim());
    }

    public static string NewFileName(DateTime now) =>
        $"{FilePrefix}{now:yyyy-MM-dd_HH-mm-ss}.log";

    /// <summary>
    /// Returns a unique path in <paramref name="folder"/> for <paramref name="now"/>.
    /// If the base name already exists, appends <c>_2</c>, <c>_3</c>, …
    /// </summary>
    public static string UniquePath(string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var baseName = NewFileName(now);
        var candidate = Path.Combine(folder, baseName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(baseName);
        var i = 2;
        while (true)
        {
            candidate = Path.Combine(folder, $"{stem}_{i}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            i++;
        }
    }

    /// <summary>
    /// Deletes older Inspector log files until at most <paramref name="keep"/> remain.
    /// Never deletes <paramref name="currentFile"/> or unrelated files.
    /// Matches both dated <c>titanium-inspector-*.log</c> and legacy <c>titanium-inspector.log*</c>.
    /// </summary>
    public static void Prune(string folder, int keep, string? currentFile)
    {
        if (keep < 1 || !Directory.Exists(folder))
        {
            return;
        }

        var currentFull = TryGetFullPath(currentFile);
        var files = CollectInspectorLogFiles(folder);
        files.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        var kept = 0;
        foreach (var file in files)
        {
            if (!TryGetFileFullName(file, out var full))
            {
                continue;
            }

            if (IsProtectedOrWithinKeep(full, currentFull, keep, ref kept))
            {
                continue;
            }

            TryDelete(file);
        }
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static List<FileInfo> CollectInspectorLogFiles(string folder)
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!IsInspectorLogFileName(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                files.Add(new FileInfo(path));
            }
            catch (IOException)
            {
                // skip unreadable
            }
            catch (UnauthorizedAccessException)
            {
                // skip unreadable
            }
        }

        return files;
    }

    private static bool TryGetFileFullName(FileInfo file, out string full)
    {
        try
        {
            full = file.FullName;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            full = "";
            return false;
        }
    }

    private static bool IsProtectedOrWithinKeep(
        string full,
        string? currentFull,
        int keep,
        ref int kept)
    {
        if (currentFull is not null
            && string.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase))
        {
            kept++;
            return true;
        }

        if (kept < keep)
        {
            kept++;
            return true;
        }

        return false;
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    /// <summary>
    /// If <paramref name="path"/> ends with <c>.log</c>, returns its directory; otherwise returns
    /// the path as a folder (trimmed). Empty/null stays null/empty.
    /// </summary>
    public static string? MigrateFilePathToFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var trimmed = path.Trim();
        if (trimmed.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(trimmed);
            return string.IsNullOrEmpty(dir) ? trimmed : dir;
        }

        return trimmed;
    }

    public static bool IsInspectorLogFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        if (fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Legacy: titanium-inspector.log, titanium-inspector.log.1, …
        return fileName.Equals(LegacyFileName, StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith(LegacyFileName + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens the log folder in the OS file manager. Creates it if missing.</summary>
    public static bool TryOpenLogsFolder(string? configuredFolder, out string? error)
    {
        var folder = ResolveFolder(configuredFolder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }

        return DesktopShell.TryOpenDirectory(folder, out error);
    }
}
