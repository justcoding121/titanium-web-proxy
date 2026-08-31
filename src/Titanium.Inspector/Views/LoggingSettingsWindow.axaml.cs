using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class LoggingSettingsWindow : Window
{
    private static readonly string[] Levels = ["Error", "Warning", "Information", "Debug"];

    private readonly SettingsService _settings;
    private readonly Action<InspectorSettings>? _applyLogging;
    private bool _saved;

    public LoggingSettingsWindow() : this(SettingsService.Load(), null)
    {
    }

    public LoggingSettingsWindow(SettingsService settings, Action<InspectorSettings>? applyLogging)
    {
        _settings = settings;
        _applyLogging = applyLogging;
        InitializeComponent();
        LevelCombo.ItemsSource = Levels;
        LoadFromSettings();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowse;
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(
        Window owner,
        SettingsService settings,
        Action<InspectorSettings>? applyLogging)
    {
        var w = new LoggingSettingsWindow(settings, applyLogging);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        EnableLoggingCheck.IsChecked = s.LoggingEnabled;
        WriteFileCheck.IsChecked = s.LoggingEnableFile;
        var level = Levels.FirstOrDefault(l =>
            string.Equals(l, s.LoggingMinimumLevel, StringComparison.OrdinalIgnoreCase)) ?? "Error";
        LevelCombo.SelectedItem = level;
        PathBox.Text = string.IsNullOrWhiteSpace(s.LoggingFilePath)
            ? DefaultLogPath()
            : s.LoggingFilePath;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = StorageProvider;
        var file = await folders.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Log file",
            SuggestedFileName = "titanium-inspector.log",
            FileTypeChoices =
            [
                new FilePickerFileType("Log") { Patterns = ["*.log"] },
                new FilePickerFileType("All") { Patterns = ["*.*"] },
            ],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            PathBox.Text = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.LoggingEnabled = EnableLoggingCheck.IsChecked == true;
        s.LoggingEnableFile = WriteFileCheck.IsChecked == true;
        s.LoggingMinimumLevel = LevelCombo.SelectedItem as string ?? "Error";
        var path = PathBox.Text?.Trim();
        s.LoggingFilePath = string.IsNullOrWhiteSpace(path) ? DefaultLogPath() : path;
        _settings.Save();
        _applyLogging?.Invoke(s);
        _saved = true;
        Close();
    }

    public static string DefaultLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector", "logs", "titanium-inspector.log");
}
