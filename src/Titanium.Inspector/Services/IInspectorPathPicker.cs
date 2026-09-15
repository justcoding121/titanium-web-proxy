using Avalonia.Platform.Storage;

namespace Titanium.Inspector.Services;

/// <summary>One save-dialog type filter (display name + wildcard pattern).</summary>
public readonly record struct PathPickerFileType(string Name, string Pattern);

/// <summary>File open/save prompts; injectable so headless / E2E tests avoid StorageProvider.</summary>
public interface IInspectorPathPicker
{
    Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern);

    Task<string?> PickSavePathAsync(string title, string suggestedFileName, IReadOnlyList<PathPickerFileType> fileTypes);

    Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns);
}

/// <summary>
/// Production picker: Avalonia StorageProvider when a window can show a dialog.
/// Cancel must return null (never fall back to Desktop / the folder the dialog had open).
/// Headless / no-window uses a Desktop path only when no save/open UI was shown.
/// </summary>
public sealed class AvaloniaInspectorPathPicker : IInspectorPathPicker
{
    public Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern) =>
        PickSavePathAsync(title, suggestedFileName, [new PathPickerFileType(filterName, pattern)]);

    public async Task<string?> PickSavePathAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<PathPickerFileType> fileTypes)
    {
        var attempt = await InspectorPathPickerHelpers.TrySaveViaStorageAsync(title, suggestedFileName, fileTypes);
        if (attempt.DialogShown)
        {
            return attempt.Path;
        }

        return InspectorPathPickerHelpers.FallbackDesktopSavePath(suggestedFileName);
    }

    public async Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns)
    {
        var attempt = await InspectorPathPickerHelpers.TryOpenViaStorageAsync(title, filterName, patterns);
        if (attempt.DialogShown)
        {
            return attempt.Path;
        }

        return InspectorPathPickerHelpers.FallbackDesktopOpenPath(patterns);
    }
}

/// <summary>Scripted paths for unit / headless / E2E-UI tests.</summary>
public sealed class ScriptedInspectorPathPicker : IInspectorPathPicker
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public int SaveCalls { get; private set; }
    public int OpenCalls { get; private set; }
    public IReadOnlyList<PathPickerFileType>? LastSaveFileTypes { get; private set; }

    public Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern) =>
        PickSavePathAsync(title, suggestedFileName, [new PathPickerFileType(filterName, pattern)]);

    public Task<string?> PickSavePathAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<PathPickerFileType> fileTypes)
    {
        SaveCalls++;
        LastSaveFileTypes = fileTypes;
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns)
    {
        OpenCalls++;
        return Task.FromResult(OpenPath);
    }
}

/// <summary>
/// Result of a StorageProvider pick. <see cref="DialogShown"/> is true when a native
/// dialog ran (including Cancel). Callers must not fall back to Desktop in that case.
/// </summary>
internal readonly record struct StoragePickAttempt(bool DialogShown, string? Path);

internal static class InspectorPathPickerHelpers
{
    public static async Task<StoragePickAttempt> TrySaveViaStorageAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<PathPickerFileType> fileTypes)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanSave: true } sp)
        {
            return new StoragePickAttempt(false, null);
        }

        var choices = fileTypes.Count == 0
            ? [new FilePickerFileType("All") { Patterns = ["*.*"] }]
            : fileTypes
                .Select(t => new FilePickerFileType(t.Name) { Patterns = [t.Pattern] })
                .ToList();

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = choices,
        });
        return new StoragePickAttempt(true, NormalizePickedPath(file?.TryGetLocalPath()));
    }

    public static async Task<StoragePickAttempt> TryOpenViaStorageAsync(string title, string filterName, string[] patterns)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanOpen: true } sp)
        {
            return new StoragePickAttempt(false, null);
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(filterName) { Patterns = patterns.ToList() },
            ],
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        return new StoragePickAttempt(true, NormalizePickedPath(path));
    }

    /// <summary>
    /// Reject cancel / empty / folder-only results. Some native pickers report the
    /// directory that was open when the user cancelled instead of a file path.
    /// </summary>
    public static string? NormalizePickedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return path;
    }

    public static string FallbackDesktopSavePath(string suggestedFileName)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, suggestedFileName.Contains('{', StringComparison.Ordinal)
            ? suggestedFileName
            : Path.GetFileNameWithoutExtension(suggestedFileName)
              + $"-{DateTime.Now:yyyyMMddHHmmss}"
              + Path.GetExtension(suggestedFileName));
    }

    public static string? FallbackDesktopOpenPath(string[] patterns)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var pattern in patterns)
        {
            var hit = Directory.EnumerateFiles(desktop, pattern)
                .OrderByDescending(f => f)
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static Avalonia.Controls.Window? TryGetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}
