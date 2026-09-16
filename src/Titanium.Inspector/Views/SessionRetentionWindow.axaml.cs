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
        SpillBodiesCheck.IsCheckedChanged += (_, _) => SyncDiskFieldsEnabled();
        SyncDiskFieldsEnabled();
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
        SpillBodiesCheck.IsChecked = s.SpillBodiesToDisk;
        DiskCacheMaxMbBox.Text = BytesToMb(s.DiskCacheMaxBytes).ToString();
        DiskCacheMaxAgeDaysBox.Text = s.DiskCacheMaxAgeDays.ToString();
        MaxSessionsBox.Text = s.MaxSessionsInMemory.ToString();
        HotBodySessionsBox.Text = s.HotBodySessions.ToString();
        MaxBodyRamMbBox.Text = BytesToMb(s.MaxCaptureBytesInMemory).ToString();
        CacheFolderPathBox.Text = SessionBodyDiskCache.GetDefaultDirectory();
    }

    private void SyncDiskFieldsEnabled()
    {
        var on = SpillBodiesCheck.IsChecked == true;
        DiskFieldsPanel.IsEnabled = on;
        DiskFieldsPanel.Opacity = on ? 1 : 0.5;
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

        if (!TryParsePositiveInt(HotBodySessionsBox.Text, out var hotBodies))
        {
            StatusText.Text = FormatPositiveNumberError("Keep full bodies in memory", HotBodySessionsBox.Text);
            return;
        }

        if (!TryParsePositiveInt(DiskCacheMaxAgeDaysBox.Text, out var maxAgeDays))
        {
            StatusText.Text = FormatPositiveNumberError("Delete cached bodies older than (days)", DiskCacheMaxAgeDaysBox.Text);
            return;
        }

        if (!TryParsePositiveLong(DiskCacheMaxMbBox.Text, out var diskMb))
        {
            StatusText.Text = FormatPositiveNumberError("Disk cache size limit (MB)", DiskCacheMaxMbBox.Text);
            return;
        }

        if (!TryParsePositiveLong(MaxBodyRamMbBox.Text, out var ramMb))
        {
            StatusText.Text = FormatPositiveNumberError("Memory for request/response bodies (MB)", MaxBodyRamMbBox.Text);
            return;
        }

        var s = _settings.Current;
        s.SpillBodiesToDisk = SpillBodiesCheck.IsChecked == true;
        s.DiskCacheMaxBytes = MbToBytes(diskMb);
        s.DiskCacheMaxAgeDays = maxAgeDays;
        s.MaxSessionsInMemory = maxSessions;
        s.HotBodySessions = hotBodies;
        s.MaxCaptureBytesInMemory = MbToBytes(ramMb);
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
