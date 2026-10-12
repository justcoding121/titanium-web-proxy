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
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem}_{i}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
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

        string? currentFull = null;
        if (!string.IsNullOrWhiteSpace(currentFile))
        {
            try
            {
                currentFull = Path.GetFullPath(currentFile);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                currentFull = null;
            }
        }

        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (!IsInspectorLogFileName(name))
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

        files.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        var kept = 0;
        foreach (var file in files)
        {
            string full;
            try
            {
                full = file.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (currentFull is not null
                && string.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase))
            {
                kept++;
                continue;
            }

            if (kept < keep)
            {
                kept++;
                continue;
            }

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
