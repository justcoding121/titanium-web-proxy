using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class SessionRetentionWindow : Window
{
    private readonly SettingsService _settings;
    private bool _saved;

    public SessionRetentionWindow() : this(SettingsService.Load())
    {
    }

    public SessionRetentionWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        OpenCacheFolderButton.Click += OnOpenCacheFolder;
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(Window owner, SettingsService settings)
    {
        var w = new SessionRetentionWindow(settings);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        DiskCacheMaxMbBox.Text = BytesToMb(s.DiskCacheMaxBytes).ToString();
        MaxSessionsBox.Text = s.MaxSessionsInMemory.ToString();
        CacheFolderPathBox.Text = SessionBodyDiskCache.GetDefaultDirectory();
    }

    private void OnOpenCacheFolder(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        var path = SessionBodyDiskCache.GetDefaultDirectory();
        if (!DesktopShell.TryOpenDirectory(path, out var error))
        {
            StatusText.Text = "Could not open cache folder: " + (error ?? "unknown error");
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!TryParsePositiveInt(MaxSessionsBox.Text, out var maxSessions))
        {
            StatusText.Text = FormatPositiveNumberError("Maximum sessions", MaxSessionsBox.Text);
            return;
        }

        if (!TryParsePositiveLong(DiskCacheMaxMbBox.Text, out var diskMb))
        {
            StatusText.Text = FormatPositiveNumberError("Disk space for saved sessions (MB)", DiskCacheMaxMbBox.Text);
            return;
        }

        var s = _settings.Current;
        s.DiskCacheMaxBytes = MbToBytes(diskMb);
        s.MaxSessionsInMemory = maxSessions;
        // Bodies always spill; keep legacy fields stable for older settings JSON readers.
        s.SpillBodiesToDisk = true;
        _settings.Save();
        _saved = true;
        Close();
    }

    public static long BytesToMb(long bytes) => Math.Max(1, bytes / (1024L * 1024L));

    public static long MbToBytes(long mb) => mb * 1024L * 1024L;

    public static bool TryParsePositiveInt(string? text, out int value)
    {
        value = 0;
        return int.TryParse(text?.Trim(), out value) && value > 0;
    }

    public static bool TryParsePositiveLong(string? text, out long value)
    {
        value = 0;
        return long.TryParse(text?.Trim(), out value) && value > 0;
    }

    public static string FormatPositiveNumberError(string field, string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? $"{field}: enter a whole number greater than 0."
            : $"{field}: '{text.Trim()}' is not a whole number greater than 0.";
}
