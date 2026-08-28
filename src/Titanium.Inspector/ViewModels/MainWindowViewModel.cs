using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SessionStreamBuffer _buffer;
    private readonly SessionRegistry _registry;
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly InterceptionService _interception = new();
    private readonly ObservableCollection<SessionSnapshot> _all = new();
    private string _statusText = "Ready";
    private string _searchQuery = "";
    private SessionSnapshot? _selected;
    private string _selectedHeaders = "";
    private string _selectedBody = "";
    private string _selectedHex = "";

    public MainWindowViewModel(
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        SettingsService settings)
    {
        _buffer = buffer;
        _registry = registry;
        _updates = updates;
        _settings = settings;
        Sessions = new ObservableCollection<SessionSnapshot>();
        Breakpoints = new BreakpointViewModel();
        AutoResponder = new AutoResponderViewModel();
        _ = registry; // retained for future inspector host DI wiring
        CheckForUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync());
        ExportHarCommand = new RelayCommand(async () => await ExportHarAsync());
        ExportArchiveCommand = new RelayCommand(async () => await ExportArchiveAsync());
        StartCaptureCommand = new RelayCommand(async () => await StartCaptureAsync());
        StopCaptureCommand = new RelayCommand(() => { _interception.Stop(); StatusText = "Capture stopped"; return Task.CompletedTask; });
        SystemProxyCommand = new RelayCommand(() => { _interception.SetSystemProxy(true); StatusText = "System proxy enabled"; return Task.CompletedTask; });
        ContinueBreakpointCommand = new RelayCommand(() => { Breakpoints.Continue(); return Task.CompletedTask; });
        AbortBreakpointCommand = new RelayCommand(() => { Breakpoints.Abort(); return Task.CompletedTask; });
        _buffer.SessionAdded += OnSessionAdded;
        _interception.SessionCaptured += (_, snap) => _buffer.Publish(snap);
        _ = PlusInspectorLoader.TryLoadPanels(out var plusWarning);
        if (plusWarning is not null)
        {
            StatusText = plusWarning;
        }
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }
    public BreakpointViewModel Breakpoints { get; }
    public AutoResponderViewModel AutoResponder { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ExportHarCommand { get; }
    public ICommand ExportArchiveCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand SystemProxyCommand { get; }
    public ICommand ContinueBreakpointCommand { get; }
    public ICommand AbortBreakpointCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public SessionSnapshot? SelectedSession
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
            {
                return;
            }

            if (value is null)
            {
                SelectedHeaders = SelectedBody = SelectedHex = "";
                return;
            }

            SelectedHeaders = value.RequestHeadersText ?? "";
            var headers = SessionInspectors.ParseHeaderBlock(value.ResponseHeadersText);
            headers.TryGetValue("Content-Encoding", out var encoding);
            var bytes = SessionInspectors.TryDecompress(value.ResponseBodyBytes ?? value.RequestBodyBytes, encoding);
            SelectedBody = SessionInspectors.TryFormatJson(value.ResponseBodyText ?? value.RequestBodyText ?? Encoding.UTF8.GetString(bytes ?? []));
            SelectedHex = SessionInspectors.ToHex(bytes);
        }
    }

    public string SelectedHeaders { get => _selectedHeaders; set => SetField(ref _selectedHeaders, value); }
    public string SelectedBody { get => _selectedBody; set => SetField(ref _selectedBody, value); }
    public string SelectedHex { get => _selectedHex; set => SetField(ref _selectedHex, value); }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnSessionAdded(SessionSnapshot snapshot)
    {
        if (AutoResponder.TryRespond(snapshot, out _))
        {
            StatusText = "AutoResponder matched " + snapshot.Url;
        }
        else if (Breakpoints.TryEnter(snapshot, out _))
        {
            StatusText = "Breakpoint hit: " + snapshot.Url;
        }

        _all.Add(snapshot);
        ApplyFilter();
        StatusText = $"Sessions: {Sessions.Count}";
    }

    private void ApplyFilter()
    {
        Sessions.Clear();
        foreach (var s in SessionSearch.Filter(_all, SearchQuery))
        {
            Sessions.Add(s);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        StatusText = "Checking for updates…";
        var result = await _updates.CheckAsync();
        StatusText = result.Message;
    }

    private async Task StartCaptureAsync()
    {
        await _interception.StartAsync(System.Net.IPAddress.Loopback, 8866);
        StatusText = "Capturing on 127.0.0.1:8866";
    }

    private async Task ExportHarAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"titanium-inspector-{DateTime.Now:yyyyMMddHHmmss}.har");
        await SessionArchive.ExportHarAsync(_all, path);
        StatusText = "Exported HAR: " + path;
    }

    private async Task ExportArchiveAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"titanium-inspector-{DateTime.Now:yyyyMMddHHmmss}.zip");
        await SessionArchive.ExportNativeArchiveAsync(_all, path);
        StatusText = "Exported archive: " + path;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

internal sealed class RelayCommand(Func<Task> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await execute();
#pragma warning disable CS0067 // Avalonia binds to CanExecuteChanged; raise unused until dynamic enablement is added.
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
