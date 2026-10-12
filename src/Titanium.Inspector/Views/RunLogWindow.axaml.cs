using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class RunLogWindow : Window
{
    private static RunLogWindow? _open;

    private readonly RunLogViewModel _model;
    private readonly SettingsService _settings;
    private readonly Action _openLogging;
    private readonly DispatcherTimer _timer;

    public RunLogWindow() : this(new RunLogRing(200), SettingsService.Load(), () => { })
    {
    }

    private RunLogWindow(RunLogRing ring, SettingsService settings, Action openLogging)
    {
        _settings = settings;
        _openLogging = openLogging;
        _model = new RunLogViewModel(ring, () => settings.Current.LoggingMinimumLevel);
        InitializeComponent();
        DataContext = _model;
        LanguageService.AttachWindow(this);
        DialogWorkArea.Attach(this);
        LevelFilter.ItemsSource = new[]
        {
            LanguageService.Get("runLog.all"),
            "Error",
            "Warning",
            "Information",
            "Debug",
            "Trace",
        };
        LevelFilter.SelectedIndex = 0;
        Width = settings.Current.RunLogWindowWidth;
        Height = settings.Current.RunLogWindowHeight;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            var follow = _model.Following;
            _model.Refresh();
            if (follow && Lines.ItemCount > 0)
            {
                Lines.ScrollIntoView(Lines.ItemCount - 1);
            }
        };
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            settings.Current.RunLogWindowWidth = Width;
            settings.Current.RunLogWindowHeight = Height;
            settings.Save();
            ClearOpenIf(this);
        };
    }

    public static void Show(Window owner, RunLogRing ring, SettingsService settings, Action openLogging)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new RunLogWindow(ring, settings, openLogging);
        RegisterOpen(window);
        window.Show(owner);
    }

    private static void RegisterOpen(RunLogWindow window) => _open = window;

    private static void ClearOpenIf(RunLogWindow window)
    {
        if (ReferenceEquals(_open, window))
        {
            _open = null;
        }
    }

    private void OnClear(object? sender, RoutedEventArgs e) => _model.Clear();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_model.CopyText());
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        InspectorLogFiles.TryOpenLogsFolder(_settings.Current.LoggingFilePath, out _);
    }

    private void OnOpenLogging(object? sender, RoutedEventArgs e) => _openLogging();

    private void OnLevelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LevelFilter.SelectedIndex <= 0)
        {
            _model.Level = RunLogViewModel.AllLevels;
            return;
        }

        if (LevelFilter.SelectedItem is string level)
        {
            _model.Level = level;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _model.NoteScroll(IsNearBottom()), DispatcherPriority.Background);
    }

    private bool IsNearBottom()
    {
        var scroll = Lines.FindDescendantOfType<ScrollViewer>();
        if (scroll is null)
        {
            return true;
        }

        var max = scroll.Extent.Height - scroll.Viewport.Height;
        return max <= 1 || scroll.Offset.Y >= max - 8;
    }
}
