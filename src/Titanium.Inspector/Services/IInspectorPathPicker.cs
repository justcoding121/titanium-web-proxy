using Avalonia.Platform.Storage;

namespace Titanium.Inspector.Services;

/// <summary>File open/save prompts; injectable so headless / E2E tests avoid StorageProvider.</summary>
public interface IInspectorPathPicker
{
    Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern);

    Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns);
}

/// <summary>Production picker: Avalonia StorageProvider when available, else Desktop fallback path.</summary>
public sealed class AvaloniaInspectorPathPicker : IInspectorPathPicker
{
    public async Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern)
    {
        var fromUi = await InspectorPathPickerHelpers.TrySaveViaStorageAsync(title, suggestedFileName, filterName, pattern);
        if (fromUi is not null)
        {
            return fromUi;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, suggestedFileName.Contains('{', StringComparison.Ordinal)
            ? suggestedFileName
            : Path.GetFileNameWithoutExtension(suggestedFileName)
              + $"-{DateTime.Now:yyyyMMddHHmmss}"
              + Path.GetExtension(suggestedFileName));
    }

    public async Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns)
    {
        var fromUi = await InspectorPathPickerHelpers.TryOpenViaStorageAsync(title, filterName, patterns);
        if (fromUi is not null)
        {
            return fromUi;
        }

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
}

/// <summary>Scripted paths for unit / headless / E2E-UI tests.</summary>
public sealed class ScriptedInspectorPathPicker : IInspectorPathPicker
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public int SaveCalls { get; private set; }
    public int OpenCalls { get; private set; }

    public Task<string?> PickSavePathAsync(string title, string suggestedFileName, string filterName, string pattern)
    {
        SaveCalls++;
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickOpenPathAsync(string title, string filterName, params string[] patterns)
    {
        OpenCalls++;
        return Task.FromResult(OpenPath);
    }
}

internal static class InspectorPathPickerHelpers
{
    public static async Task<string?> TrySaveViaStorageAsync(
        string title,
        string suggestedFileName,
        string filterName,
        string pattern)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanSave: true } sp)
        {
            return null;
        }

        var file = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = [pattern] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> TryOpenViaStorageAsync(string title, string filterName, string[] patterns)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanOpen: true } sp)
        {
            return null;
        }

        var files = await sp.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = patterns.ToList() },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
