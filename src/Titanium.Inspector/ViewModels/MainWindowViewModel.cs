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
    private bool _capturing = true;
    private bool _systemProxy;
    private string _autoResponderMatch = "*";
    private string _autoResponderBody = "OK";
    private int _autoResponderStatus = 200;
    private string _plusPanelsSummary = "";

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
        _interception.AutoResponder = AutoResponder;
        _interception.Breakpoints = Breakpoints;

        CheckForUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync());
        ExportHarCommand = new RelayCommand(async () => await ExportHarAsync());
        ExportArchiveCommand = new RelayCommand(async () => await ExportArchiveAsync());
        ImportArchiveCommand = new RelayCommand(async () => await ImportArchiveAsync());
        StartCaptureCommand = new RelayCommand(async () => await StartCaptureAsync());
        StopCaptureCommand = new RelayCommand(() =>
        {
            _interception.Stop();
            SystemProxy = false;
            StatusText = "Stopped (system proxy restored if it was on)";
            return Task.CompletedTask;
        });
        ToggleCapturingCommand = new RelayCommand(() =>
        {
            Capturing = !Capturing;
            return Task.CompletedTask;
        });
        ClearSessionsCommand = new RelayCommand(() =>
        {
            _all.Clear();
            Sessions.Clear();
            SelectedSession = null;
            StatusText = "Sessions cleared";
            return Task.CompletedTask;
        });
        ToggleSystemProxyCommand = new RelayCommand(() =>
        {
            if (!_interception.IsRunning)
            {
                StatusText = "Start interception before toggling system proxy";
                return Task.CompletedTask;
            }

            SystemProxy = !SystemProxy;
            _interception.SetSystemProxy(SystemProxy);
            StatusText = SystemProxy ? "System proxy enabled (with identity bypass)" : "System proxy restored";
            return Task.CompletedTask;
        });
        InstallCaCommand = new RelayCommand(() =>
        {
            if (!_interception.IsRunning)
            {
                StatusText = "Start interception first";
                return Task.CompletedTask;
            }

            _interception.InstallRootCertificate(machineStore: false);
            StatusText = "Root CA trusted in current user store";
            return Task.CompletedTask;
        });
        UntrustCaCommand = new RelayCommand(() =>
        {
            if (!_interception.IsRunning)
            {
                StatusText = "Start interception first";
                return Task.CompletedTask;
            }

            _interception.UntrustRootCertificate(machineStore: false);
            StatusText = "Root CA removed from current user store";
            return Task.CompletedTask;
        });
        ExportCaCommand = new RelayCommand(() =>
        {
            var path = _interception.ExportRootCertificate();
            StatusText = path is null ? "No root certificate yet — start interception first" : "Exported CA: " + path;
            return Task.CompletedTask;
        });
        ReplayCommand = new RelayCommand(async () => await ReplaySelectedAsync());
        AddAutoResponderRuleCommand = new RelayCommand(() =>
        {
            AutoResponder.Rules.Add(new AutoResponderRule
            {
                MatchUrl = AutoResponderMatch,
                StatusCode = AutoResponderStatus,
                Body = AutoResponderBody,
                Enabled = true,
            });
            StatusText = $"AutoResponder rule added ({AutoResponder.Rules.Count} total)";
            return Task.CompletedTask;
        });
        ContinueBreakpointCommand = new RelayCommand(() =>
        {
            Breakpoints.Continue();
            return Task.CompletedTask;
        });
        AbortBreakpointCommand = new RelayCommand(() =>
        {
            Breakpoints.Abort();
            return Task.CompletedTask;
        });

        _buffer.SessionAdded += OnSessionAdded;
        _interception.SessionCaptured += (_, snap) =>
        {
            _registry.Add(snap);
            _buffer.Publish(snap);
        };
        _interception.SessionUpdated += (_, snap) =>
        {
            if (ReferenceEquals(SelectedSession, snap))
            {
                RefreshSelectedInspectors();
            }
        };

        var panels = PlusInspectorLoader.TryLoadPanels(out var plusWarning);
        if (plusWarning is not null)
        {
            StatusText = plusWarning;
        }

        if (panels.Count > 0)
        {
            PlusPanelsSummary = string.Join("; ", panels.Select(DescribePanel));
        }
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }
    public BreakpointViewModel Breakpoints { get; }
    public AutoResponderViewModel AutoResponder { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ExportHarCommand { get; }
    public ICommand ExportArchiveCommand { get; }
    public ICommand ImportArchiveCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand ToggleCapturingCommand { get; }
    public ICommand ClearSessionsCommand { get; }
    public ICommand ToggleSystemProxyCommand { get; }
    public ICommand InstallCaCommand { get; }
    public ICommand UntrustCaCommand { get; }
    public ICommand ExportCaCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand AddAutoResponderRuleCommand { get; }
    public ICommand ContinueBreakpointCommand { get; }
    public ICommand AbortBreakpointCommand { get; }

    public string AutoResponderMatch
    {
        get => _autoResponderMatch;
        set => SetField(ref _autoResponderMatch, value);
    }

    public string AutoResponderBody
    {
        get => _autoResponderBody;
        set => SetField(ref _autoResponderBody, value);
    }

    public int AutoResponderStatus
    {
        get => _autoResponderStatus;
        set => SetField(ref _autoResponderStatus, value);
    }

    public string PlusPanelsSummary
    {
        get => _plusPanelsSummary;
        set => SetField(ref _plusPanelsSummary, value);
    }

    public bool Capturing
    {
        get => _capturing;
        set
        {
            if (!SetField(ref _capturing, value))
            {
                return;
            }

            _interception.Capturing = value;
            StatusText = value ? "Capturing on" : "Capturing paused (proxy still listening)";
        }
    }

    public bool SystemProxy
    {
        get => _systemProxy;
        set => SetField(ref _systemProxy, value);
    }

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

            RefreshSelectedInspectors();
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

    private void RefreshSelectedInspectors()
    {
        if (_selected is null)
        {
            SelectedHeaders = SelectedBody = SelectedHex = "";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== Request ===");
        sb.AppendLine(_selected.RequestHeadersText);
        if (!string.IsNullOrEmpty(_selected.ResponseHeadersText))
        {
            sb.AppendLine("=== Response ===");
            sb.AppendLine(_selected.ResponseHeadersText);
        }

        var cookies = SessionInspectors.ParseCookies(SessionInspectors.ParseHeaderBlock(_selected.RequestHeadersText));
        var query = SessionInspectors.ParseQuery(_selected.Url);
        if (cookies.Count > 0)
        {
            sb.AppendLine("=== Cookies ===");
            foreach (var c in cookies)
            {
                sb.Append(c.Key).Append('=').AppendLine(c.Value);
            }
        }

        if (query.Count > 0)
        {
            sb.AppendLine("=== Query ===");
            foreach (var q in query)
            {
                sb.Append(q.Key).Append('=').AppendLine(q.Value);
            }
        }

        SelectedHeaders = sb.ToString();

        var headers = SessionInspectors.ParseHeaderBlock(_selected.ResponseHeadersText);
        headers.TryGetValue("Content-Encoding", out var encoding);
        var bytes = SessionInspectors.TryDecompress(_selected.ResponseBodyBytes ?? _selected.RequestBodyBytes, encoding);
        SelectedBody = SessionInspectors.TryFormatJson(
            _selected.ResponseBodyText ?? _selected.RequestBodyText ?? Encoding.UTF8.GetString(bytes ?? []));
        SelectedHex = SessionInspectors.ToHex(bytes);
    }

    private void OnSessionAdded(SessionSnapshot snapshot)
    {
        _all.Add(snapshot);
        ApplyFilter();
        StatusText = $"Sessions: {_all.Count}";
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
        Capturing = true;
        StatusText = "Listening on 127.0.0.1:8866 — install CA and enable system proxy from Capture menu";
    }

    private async Task ReplaySelectedAsync()
    {
        if (SelectedSession is null)
        {
            StatusText = "Select a session to replay";
            return;
        }

        StatusText = "Replaying…";
        var result = await ReplayService.ReplayAsync(SelectedSession);
        StatusText = result.Ok
            ? $"Replay → HTTP {result.StatusCode}: {Truncate(result.Message, 120)}"
            : "Replay failed: " + result.Message;
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

    private async Task ImportArchiveAsync()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var latest = Directory.EnumerateFiles(desktop, "titanium-inspector-*.zip")
            .OrderByDescending(f => f)
            .FirstOrDefault();
        if (latest is null)
        {
            StatusText = "No titanium-inspector-*.zip on Desktop to import";
            return;
        }

        var imported = await SessionArchive.ImportNativeArchiveAsync(latest);
        foreach (var snap in imported)
        {
            _registry.Add(snap);
            _all.Add(snap);
        }

        ApplyFilter();
        StatusText = $"Imported {imported.Count} sessions from {Path.GetFileName(latest)}";
    }

    private static string DescribePanel(object panel)
    {
        var type = panel.GetType();
        var title = type.GetProperty("Title")?.GetValue(panel)?.ToString();
        var desc = type.GetProperty("Description")?.GetValue(panel)?.ToString();
        return string.IsNullOrEmpty(title) ? type.Name : $"{title}: {desc}";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

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
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
