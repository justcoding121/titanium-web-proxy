using Titanium.Inspector.Localization;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class SessionRetentionWindow : Window
{
    private readonly SettingsService _settings;
    private readonly SessionStore? _store;
    private bool _saved;

    public SessionRetentionWindow() : this(SettingsService.Load())
    {
    }

    public SessionRetentionWindow(SettingsService settings, SessionStore? store = null)
    {
        _settings = settings;
        _store = store;
        InitializeComponent();
        LanguageService.AttachWindow(this);
        LoadFromSettings();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        OpenCacheFolderButton.Click += OnOpenCacheFolder;
        ClearSavedCacheButton.Click += OnClearSavedCache;
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(Window owner, SettingsService settings, SessionStore? store = null)
    {
        var w = new SessionRetentionWindow(settings, store);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    public static string FormatCacheUsage(SessionCacheStats stats)
    {
        if (stats.RunCount <= 0 && stats.TotalBytes <= 0)
        {
            return "No saved sessions on disk.";
        }

        return DescribeRuns(stats.RunCount) + ", " + SessionDisplayFormat.FormatByteSize(stats.TotalBytes) + " on disk.";
    }

    public static string FormatClearCacheConfirm(SessionCacheStats stats)
    {
        var size = SessionDisplayFormat.FormatByteSize(stats.TotalBytes);
        return "Delete " + DescribeRuns(stats.RunCount) + " (" + size + ")? "
            + "This removes saved session files from every run, including the current one. "
            + "Sessions stay in the list, but their saved bodies are removed.";
    }

    public static string FormatSessionsClearedStatus(long freedBytes) =>
        freedBytes > 0
            ? "Sessions cleared (freed " + SessionDisplayFormat.FormatByteSize(freedBytes) + ")"
            : "Sessions cleared";

    private static string DescribeRuns(int runCount) =>
        runCount == 1
            ? "1 saved run"
            : runCount.ToString(CultureInfo.InvariantCulture) + " saved runs";

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        DiskCacheMaxMbBox.Text = BytesToMb(s.DiskCacheMaxBytes).ToString();
        MaxSessionsBox.Text = s.MaxSessionsInMemory.ToString();
        CacheFolderPathBox.Text = SessionBodyDiskCache.GetDefaultDirectory();
        RefreshCacheUsage();
    }

    private void RefreshCacheUsage()
    {
        CacheUsageText.Text = _store is null
            ? "Saved session cache is not available."
            : FormatCacheUsage(_store.GetCacheStats());
    }

    private async void OnClearSavedCache(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ClearSavedCacheAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not clear saved session cache: " + ex.Message;
        }
    }

    private async Task ClearSavedCacheAsync()
    {
        StatusText.Text = string.Empty;
        if (_store is null)
        {
            StatusText.Text = "Saved session cache is not available.";
            return;
        }

        var stats = _store.GetCacheStats();
        if (stats.RunCount <= 0 && stats.TotalBytes <= 0)
        {
            StatusText.Text = "No saved sessions on disk.";
            RefreshCacheUsage();
            return;
        }

        var accepted = await SimpleConfirmDialog.ShowAsync(
            this,
            "Clear saved session cache",
            FormatClearCacheConfirm(stats),
            "Delete",
            "Cancel",
            height: 240);
        if (!accepted)
        {
            StatusText.Text = "Clear saved session cache cancelled";
            return;
        }

        var freed = _store.ClearAllSavedRuns();
        StatusText.Text = freed > 0
            ? "Cleared saved session cache (freed " + SessionDisplayFormat.FormatByteSize(freed) + ")"
            : "Cleared saved session cache";
        RefreshCacheUsage();
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
