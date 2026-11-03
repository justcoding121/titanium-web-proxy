using System.IO.Compression;

namespace Titanium.Cli.Updates;

internal static partial class UpdateCommand
{
    internal const string PlusFileListName = "Titanium.Plus.files";

    /// <summary>Upper bound on files in a Plus bundle zip (payload plus the file list).</summary>
    internal const int MaxPlusBundleEntries = 512;

    /// <summary>Upper bound on the uncompressed size of a Plus bundle zip.</summary>
    internal const long MaxPlusBundleUncompressedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Deletes the Plus file set beside the CLI. When <c>Titanium.Plus.files</c> is present, deletes
    /// exactly those names (and their <c>.bak</c> / <c>.new</c> copies). Otherwise deletes
    /// <c>Titanium.Plus*.dll</c> only. Idempotent when Plus is already absent.
    /// <paramref name="installDir"/> is for tests.
    /// </summary>
    internal static int RemovePlus(string? installDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(installDir)
            ? AppContext.BaseDirectory
            : installDir;
        dir = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!Directory.Exists(dir))
        {
            AsyncConsole.WriteError("Plus remove failed: install directory was not found.");
            return 1;
        }

        try
        {
            var listPath = Path.Combine(dir, PlusFileListName);
            var usedList = File.Exists(listPath);
            int removed;
            if (usedList)
            {
                removed = RemoveListedPlusFiles(dir, listPath);
                if (removed < 0)
                {
                    return 1;
                }
            }
            else
            {
                removed = RemoveLegacyPlusDlls(dir);
            }

            if (removed > 0)
            {
                AsyncConsole.WriteLine(usedList
                    ? "Removed Plus files from the CLI install directory."
                    : "Removed Titanium.Plus.dll from the CLI install directory.");
            }
            else
            {
                AsyncConsole.WriteLine("Plus is not installed beside the CLI (nothing to remove).");
            }

            AsyncConsole.WriteLine(
                "If a titanium run / OS service is still up with Plus loaded, stop and restart it so the " +
                "control plane and dashboard unload (remove-plus does not stop them). " +
                "If your config still has plus.enabled: true, set it to false (or remove the plus: block). " +
                "Plus is PolyForm Noncommercial — not for commercial use.");
            return 0;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError($"Plus remove failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Extracts a Plus bundle zip into a temp directory beside <paramref name="installDir"/>, checks the
    /// entry names and <c>Titanium.Plus.files</c>, then swaps each file in with a <c>.new</c> / <c>.bak</c> pair.
    /// Rolls every swapped file back when a later file fails.
    /// </summary>
    internal static int ApplyPlusBundleZip(string zipPath, string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            AsyncConsole.WriteError("Plus update failed: install directory was not found.");
            return 1;
        }

        installDir = Path.GetFullPath(installDir);
        string? stage = null;
        try
        {
            stage = CreatePlusStageDirectory(installDir);
            PopulatePlusStage(zipPath, stage);
            return SwapPlusStageIntoInstall(stage, installDir);
        }
        catch (InvalidDataException ex)
        {
            AsyncConsole.WriteError("Plus bundle rejected: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError("Plus update failed: " + ex.Message);
            return 1;
        }
        finally
        {
            if (stage is not null && Directory.Exists(stage))
            {
                try
                {
                    Directory.Delete(stage, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup of the temp directory beside the install.
                }
            }
        }
    }

    /// <summary>Replaces <c>Titanium.Plus.dll</c> only (legacy <c>products.plus.asset</c>).</summary>
    internal static int ApplyPlusLegacyDll(string sourceDll, string installDir)
    {
        var dest = Path.Combine(installDir, "Titanium.Plus.dll");
        var backup = dest + ".bak";
        var staging = dest + ".new";
        try
        {
            if (File.Exists(dest))
            {
                File.Copy(dest, backup, overwrite: true);
            }

            File.Copy(sourceDll, staging, overwrite: true);
            File.Move(staging, dest, overwrite: true);
            return 0;
        }
        catch (Exception ex)
        {
            TryDeleteFile(staging);
            TryRestorePlusBackup(dest, backup);
            AsyncConsole.WriteError($"Plus update failed: {ex.Message}");
            return 1;
        }
    }

    internal static bool IsPlainPlusFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 180)
        {
            return false;
        }

        if (name is "." or "..")
        {
            return false;
        }

        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    internal static void EnsurePlusBundleInventory(IReadOnlyList<(string Name, long Length)> entries)
    {
        if (entries.Count == 0)
        {
            throw new InvalidDataException("Plus bundle is empty.");
        }

        if (entries.Count > MaxPlusBundleEntries)
        {
            throw new InvalidDataException(
                $"Plus bundle has too many files ({entries.Count}; limit {MaxPlusBundleEntries}).");
        }

        long total = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, length) in entries)
        {
            if (!IsPlainPlusFileName(name))
            {
                throw new InvalidDataException($"Plus bundle entry '{name}' is not a plain file name.");
            }

            if (length < 0)
            {
                throw new InvalidDataException($"Plus bundle entry '{name}' has an invalid size.");
            }

            if (!names.Add(name))
            {
                throw new InvalidDataException($"Plus bundle entry '{name}' is duplicated.");
            }

            total += length;
            if (total > MaxPlusBundleUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Plus bundle exceeds {MaxPlusBundleUncompressedBytes} bytes uncompressed.");
            }
        }
    }

    private static int RemoveListedPlusFiles(string dir, string listPath)
    {
        var names = new List<string>();
        foreach (var raw in File.ReadAllLines(listPath))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!IsPlainPlusFileName(name))
            {
                AsyncConsole.WriteError($"Plus remove refused: '{name}' is not a plain file name.");
                return -1;
            }

            names.Add(name);
        }

        var removed = 0;
        foreach (var name in names)
        {
            removed += DeletePlusTrio(Path.Combine(dir, name));
        }

        if (!names.Contains(PlusFileListName, StringComparer.Ordinal))
        {
            removed += DeletePlusTrio(listPath);
        }

        return removed;
    }

    private static int RemoveLegacyPlusDlls(string dir)
    {
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "Titanium.Plus*.dll").ToArray())
        {
            removed += DeletePlusTrio(path);
        }

        // .bak / .new can remain after the dll itself is already gone.
        removed += DeletePlusTrio(Path.Combine(dir, "Titanium.Plus.dll"));
        return removed;
    }

    private static int DeletePlusTrio(string dest)
    {
        var removed = 0;
        foreach (var path in new[] { dest, dest + ".bak", dest + ".new" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            removed++;
        }

        return removed;
    }

    private static string CreatePlusStageDirectory(string installDir)
    {
        var parent = Path.GetDirectoryName(installDir);
        if (string.IsNullOrEmpty(parent))
        {
            parent = Path.GetTempPath();
        }

        var stage = Path.Combine(parent, ".plus-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        return stage;
    }

    private static void PopulatePlusStage(string zipPath, string stage)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = new List<(string Name, long Length)>();
        foreach (var entry in archive.Entries)
        {
            if (IsZipDirectory(entry))
            {
                continue;
            }

            entries.Add((entry.FullName, entry.Length));
        }

        EnsurePlusBundleInventory(entries);

        foreach (var entry in archive.Entries)
        {
            if (IsZipDirectory(entry))
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            EnsurePathInside(stage, dest);
            using var input = entry.Open();
            using var output = File.Create(dest);
            input.CopyTo(output);
        }

        var listPath = Path.Combine(stage, PlusFileListName);
        if (!File.Exists(listPath))
        {
            throw new InvalidDataException("Plus bundle is missing Titanium.Plus.files.");
        }

        var listed = ReadPlusFileList(listPath);
        var extracted = Directory.EnumerateFiles(stage)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (listed.Count != extracted.Count || !extracted.SetEquals(listed))
        {
            throw new InvalidDataException("Plus bundle file list does not match the archive entries.");
        }
    }

    private static HashSet<string> ReadPlusFileList(string listPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(listPath))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!IsPlainPlusFileName(name))
            {
                throw new InvalidDataException($"Plus bundle file list entry '{name}' is not a plain file name.");
            }

            if (!names.Add(name))
            {
                throw new InvalidDataException($"Plus bundle file list entry '{name}' is duplicated.");
            }
        }

        if (!names.Contains(PlusFileListName))
        {
            throw new InvalidDataException("Plus bundle file list must include Titanium.Plus.files.");
        }

        return names;
    }

    private static int SwapPlusStageIntoInstall(string stage, string installDir)
    {
        var names = Directory.EnumerateFiles(stage)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var swapped = new List<PlusFileSwap>();
        string? pendingStaging = null;
        try
        {
            foreach (var name in names)
            {
                if (!IsPlainPlusFileName(name))
                {
                    throw new InvalidDataException($"Plus bundle entry '{name}' is not a plain file name.");
                }

                var dest = Path.GetFullPath(Path.Combine(installDir, name));
                EnsurePathInside(installDir, dest);
                var backup = dest + ".bak";
                var staging = dest + ".new";
                pendingStaging = staging;
                File.Copy(Path.Combine(stage, name), staging, overwrite: true);
                var hadOriginal = File.Exists(dest);
                if (hadOriginal)
                {
                    File.Copy(dest, backup, overwrite: true);
                }

                File.Move(staging, dest, overwrite: true);
                pendingStaging = null;
                swapped.Add(new PlusFileSwap(dest, backup, hadOriginal));
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (pendingStaging is not null)
            {
                TryDeleteFile(pendingStaging);
            }

            RollbackPlusSwaps(swapped);
            if (ex is InvalidDataException)
            {
                throw;
            }

            AsyncConsole.WriteError("Plus update failed: " + ex.Message);
            return 1;
        }
    }

    private static void RollbackPlusSwaps(List<PlusFileSwap> swapped)
    {
        for (var i = swapped.Count - 1; i >= 0; i--)
        {
            var swap = swapped[i];
            try
            {
                if (swap.HadOriginal && File.Exists(swap.Backup))
                {
                    File.Copy(swap.Backup, swap.Dest, overwrite: true);
                }
                else if (!swap.HadOriginal && File.Exists(swap.Dest))
                {
                    File.Delete(swap.Dest);
                }
            }
            catch
            {
                // Best-effort. Continue so earlier files still restore.
            }
        }
    }

    private static void EnsurePathInside(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootFull, comparison))
        {
            throw new InvalidDataException("Plus bundle entry escapes the install directory.");
        }
    }

    private static bool IsZipDirectory(ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(entry.Name)
        || entry.FullName.EndsWith('/')
        || entry.FullName.EndsWith('\\');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private readonly record struct PlusFileSwap(string Dest, string Backup, bool HadOriginal);
}
