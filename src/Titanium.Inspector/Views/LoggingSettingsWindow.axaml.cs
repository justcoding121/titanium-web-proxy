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
    private decimal _lastFileSize = 50;
    private decimal _lastRolled = 5;
    private decimal _lastMemory = 2000;

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
        LevelCombo.ItemsSource = Levels;
        LevelCombo.SelectionChanged += (_, _) => UpdateLevelCost();
        EnableLoggingCheck.IsCheckedChanged += (_, _) => UpdateEnabledState();
        WriteFileCheck.IsCheckedChanged += (_, _) => UpdateEnabledState();
        FileSizeBox.ValueChanged += (_, _) => UpdateDiskUseHelp();
        RolledFilesBox.ValueChanged += (_, _) => UpdateDiskUseHelp();
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
        EnableLoggingCheck.IsChecked = s.LoggingEnabled;
        WriteFileCheck.IsChecked = s.LoggingEnableFile;
        var level = Levels.FirstOrDefault(l =>
            string.Equals(l, s.LoggingMinimumLevel, StringComparison.OrdinalIgnoreCase)) ?? "Error";
        LevelCombo.SelectedItem = level;
        PathBox.Text = InspectorLogFiles.ResolveFolder(s.LoggingFilePath);
        _lastFileSize = Math.Clamp(s.LoggingMaxFileSizeMb, 1, 4096);
        _lastRolled = Math.Clamp(Math.Max(1, s.LoggingMaxRolledFiles), 1, 100);
        _lastMemory = Math.Clamp(s.LoggingMemoryLines, 100, 50_000);
        FileSizeBox.Value = _lastFileSize;
        RolledFilesBox.Value = _lastRolled;
        MemoryLinesBox.Value = _lastMemory;
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
        var loggingOn = EnableLoggingCheck.IsChecked == true;
        WriteFileCheck.IsEnabled = loggingOn;
        var filesOn = loggingOn && WriteFileCheck.IsChecked == true;
        FileRows.IsEnabled = filesOn;
        FileSizeBox.IsEnabled = filesOn;
        RolledFilesBox.IsEnabled = filesOn;
    }

    private void UpdateDiskUseHelp()
    {
        var size = (int)(FileSizeBox.Value ?? _lastFileSize);
        var keep = (int)(RolledFilesBox.Value ?? _lastRolled);
        var totalMb = Math.Max(1, size) * (long)Math.Max(1, keep);
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
        var s = _settings.Current;
        s.LoggingEnabled = EnableLoggingCheck.IsChecked == true;
        s.LoggingEnableFile = WriteFileCheck.IsChecked == true;
        s.LoggingMinimumLevel = LevelCombo.SelectedItem as string ?? "Error";

        var fileMb = (int)(FileSizeBox.Value ?? _lastFileSize);
        var rolled = (int)(RolledFilesBox.Value ?? _lastRolled);
        var memory = (int)(MemoryLinesBox.Value ?? _lastMemory);
        fileMb = Math.Clamp(fileMb, 1, 4096);
        rolled = Math.Clamp(rolled, 1, 100);
        memory = Math.Clamp(memory, 100, 50_000);

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
}
