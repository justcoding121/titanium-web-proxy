using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class LoggingSettingsWindow : Window
{
    private static readonly string[] Levels = ["Error", "Warning", "Information", "Debug", "Trace"];

    private readonly SettingsService _settings;
    private readonly Action<InspectorSettings>? _applyLogging;
    private readonly Func<string?>? _currentFilePath;
    private bool _saved;
    private int _lastFileSize = 50;
    private int _lastRolled = 5;
    private int _lastMemory = 2000;

    public LoggingSettingsWindow() : this(SettingsService.Load(), null, null)
    {
    }

    public LoggingSettingsWindow(
        SettingsService settings,
        Action<InspectorSettings>? applyLogging,
        Func<string?>? currentFilePath = null)
    {
        _settings = settings;
        _applyLogging = applyLogging;
        _currentFilePath = currentFilePath;
        InitializeComponent();
        LanguageService.AttachWindow(this);
        DialogWorkArea.Attach(this);
        LevelCombo.ItemsSource = Levels;
        LevelCombo.SelectionChanged += (_, _) => UpdateLevelCost();
        WriteFileCheck.IsCheckedChanged += (_, _) => UpdateEnabledState();
        FileSizeBox.TextChanged += (_, _) => UpdateDiskUseHelp();
        RolledFilesBox.TextChanged += (_, _) => UpdateDiskUseHelp();
        LoadFromSettings();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowse;
        OpenFolderButton.Click += OnOpenFolder;
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(
        Window owner,
        SettingsService settings,
        Action<InspectorSettings>? applyLogging,
        Func<string?>? currentFilePath = null)
    {
        var w = new LoggingSettingsWindow(settings, applyLogging, currentFilePath);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    /// <summary>Legacy alias; prefer <see cref="InspectorLogFiles.DefaultFolder"/>.</summary>
    public static string DefaultLogPath() => InspectorLogFiles.DefaultFolder();

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        WriteFileCheck.IsChecked = s.LoggingEnableFile;
        var level = Levels.FirstOrDefault(l =>
            string.Equals(l, s.LoggingMinimumLevel, StringComparison.OrdinalIgnoreCase)) ?? "Error";
        LevelCombo.SelectedItem = level;
        PathBox.Text = InspectorLogFiles.ResolveFolder(s.LoggingFilePath);
        _lastFileSize = Math.Clamp(s.LoggingMaxFileSizeMb, 1, 4096);
        _lastRolled = Math.Clamp(Math.Max(1, s.LoggingMaxRolledFiles), 1, 100);
        _lastMemory = Math.Clamp(s.LoggingMemoryLines, 100, 50_000);
        FileSizeBox.Text = _lastFileSize.ToString();
        RolledFilesBox.Text = _lastRolled.ToString();
        MemoryLinesBox.Text = _lastMemory.ToString();
        UpdateLevelCost();
        UpdateEnabledState();
        UpdateDiskUseHelp();
        UpdateCurrentFileLine();
    }

    private void UpdateLevelCost()
    {
        var level = LevelCombo.SelectedItem as string;
        LevelCost.IsVisible = level is "Debug" or "Trace";
    }

    private void UpdateEnabledState()
    {
        var filesOn = WriteFileCheck.IsChecked == true;
        // Hide file controls when off — greying NumericUpDown/TextBox looked broken in Fluent dark.
        FileRows.IsVisible = filesOn;
        FileSizeRow.IsVisible = filesOn;
        KeepFilesRow.IsVisible = filesOn;
    }

    private void UpdateDiskUseHelp()
    {
        var size = ParseOr(_lastFileSize, FileSizeBox.Text, 1, 4096);
        var keep = ParseOr(_lastRolled, RolledFilesBox.Text, 1, 100);
        var totalMb = (long)size * keep;
        var amount = totalMb >= 1024
            ? $"{totalMb / 1024.0:0.##} GB"
            : $"{totalMb} MB";
        var keepHelp = LanguageService.Get("logging.keepFilesHelp");
        var diskUse = LanguageService.Format("logging.diskUse", amount);
        KeepFilesHelp.Text = string.IsNullOrEmpty(keepHelp)
            ? diskUse
            : keepHelp.TrimEnd() + " " + diskUse;
    }

    private void UpdateCurrentFileLine()
    {
        var label = LanguageService.Get("logging.thisRun");
        var current = _currentFilePath?.Invoke();
        if (string.IsNullOrEmpty(current))
        {
            ThisRunFileText.Text = label + " " + LanguageService.Get("logging.thisRunPending");
            return;
        }

        ThisRunFileText.Text = label + " " + Path.GetFileName(current);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Get("logging.path"),
            AllowMultiple = false,
        });
        var folder = folders.Count > 0 ? folders[0] : null;
        if (InspectorPathPickerHelpers.NormalizePickedPath(folder?.TryGetLocalPath()) is { } path)
        {
            PathBox.Text = path;
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        var path = string.IsNullOrWhiteSpace(PathBox.Text)
            ? InspectorLogFiles.DefaultFolder()
            : PathBox.Text.Trim();
        if (!InspectorLogFiles.TryOpenLogsFolder(path, out var error))
        {
            StatusText.Text = "Could not open log folder: " + (error ?? "unknown error");
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (!TryParseBox(FileSizeBox.Text, 1, 4096, out var fileMb)
            || !TryParseBox(RolledFilesBox.Text, 1, 100, out var rolled)
            || !TryParseBox(MemoryLinesBox.Text, 100, 50_000, out var memory))
        {
            StatusText.Text = LanguageService.Get("logging.numbersInvalid");
            return;
        }

        var s = _settings.Current;
        // Inspector always keeps the logger factory on so Recent logs keeps receiving lines.
        s.LoggingEnabled = true;
        s.LoggingEnableFile = WriteFileCheck.IsChecked == true;
        s.LoggingMinimumLevel = LevelCombo.SelectedItem as string ?? "Error";
        s.LoggingMaxFileSizeMb = fileMb;
        s.LoggingMaxRolledFiles = rolled;
        s.LoggingMemoryLines = memory;
        s.LoggingModelVersion = 1;
        var path = PathBox.Text?.Trim();
        s.LoggingFilePath = string.IsNullOrWhiteSpace(path)
            ? InspectorLogFiles.DefaultFolder()
            : path;
        _settings.Save();
        _applyLogging?.Invoke(s);
        _saved = true;
        Close();
    }

    private static int ParseOr(int fallback, string? text, int min, int max)
    {
        if (TryParseBox(text, min, max, out var value))
        {
            return value;
        }

        return fallback;
    }

    private static bool TryParseBox(string? text, int min, int max, out int value)
    {
        if (int.TryParse(text?.Trim(), out value) && value >= min && value <= max)
        {
            return true;
        }

        value = 0;
        return false;
    }
}
