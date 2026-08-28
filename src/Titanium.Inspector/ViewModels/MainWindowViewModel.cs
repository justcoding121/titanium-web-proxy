using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SessionStreamBuffer _buffer;
    private readonly SessionRegistry _registry;
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly InterceptionService _interception;
    private readonly ObservableCollection<SessionSnapshot> _all = new();
    private string _statusText = "Ready";
    private string _searchQuery = "";
    private SessionSnapshot? _selected;
    private string _selectedHeaders = "";
    private string _selectedBody = "";
    private string _selectedHex = "";
    private string _selectedFrames = "";
    private bool _capturing = true;
    private bool _systemProxy;
    private string _autoResponderMatch = "*";
    private string _autoResponderBody = "OK";
    private string _autoResponderContentType = "text/plain";
    private int _autoResponderStatus = 200;
    private string _plusPanelsSummary = "";
    private string _bindAddress = "127.0.0.1";
    private int _bindPort = 8866;
    private bool _breakpointOnResponse;
    private string _breakpointEditBody = "";
    private string? _scriptOnRequest;
    private string? _scriptOnResponse;
    private string _composerMethod = "GET";
    private string _composerUrl = "";
    private string _composerHeaders = "";
    private string _composerBody = "";

    public MainWindowViewModel(
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        SettingsService settings,
        InterceptionService? interception = null)
    {
        _buffer = buffer;
        _registry = registry;
        _updates = updates;
        _settings = settings;
        _interception = interception ?? new InterceptionService();
        Sessions = new ObservableCollection<SessionSnapshot>();
        Breakpoints = new BreakpointViewModel();
        AutoResponder = new AutoResponderViewModel();
        _interception.AutoResponder = AutoResponder;
        _interception.Breakpoints = Breakpoints;

        LoadFromSettings();

        CheckForUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync());
        ExportHarCommand = new RelayCommand(async () => await ExportHarAsync());
        ImportHarCommand = new RelayCommand(async () => await ImportHarAsync());
        ExportArchiveCommand = new RelayCommand(async () => await ExportArchiveAsync());
        ImportArchiveCommand = new RelayCommand(async () => await ImportArchiveAsync());
        StartCaptureCommand = new RelayCommand(async () => await StartCaptureAsync());
        StopCaptureCommand = new RelayCommand(StopCaptureAsync);
        ToggleCapturingCommand = new RelayCommand(ToggleCapturingAsync);
        ClearSessionsCommand = new RelayCommand(ClearSessionsAsync);
        ToggleSystemProxyCommand = new RelayCommand(ToggleSystemProxyAsync);
        InstallCaCommand = new RelayCommand(InstallCaAsync);
        UntrustCaCommand = new RelayCommand(UntrustCaAsync);
        ExportCaCommand = new RelayCommand(ExportCaAsync);
        DeviceCaSetupCommand = new RelayCommand(DeviceCaSetupAsync);
        ReplayCommand = new RelayCommand(async () => await ReplaySelectedAsync());
        LoadFromSelectedCommand = new RelayCommand(LoadFromSelectedAsync);
        SendComposerCommand = new RelayCommand(async () => await SendComposerAsync());
        AddAutoResponderRuleCommand = new RelayCommand(AddAutoResponderRuleAsync);
        DeleteAutoResponderRuleCommand = new RelayCommand(DeleteAutoResponderRuleAsync);
        UpdateAutoResponderRuleCommand = new RelayCommand(UpdateAutoResponderRuleAsync);
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
        ApplyEditBodyCommand = new RelayCommand(ApplyEditBodyAsync);
        ToggleDebugLoggingCommand = new RelayCommand(ToggleDebugLoggingAsync);

        WireEventHandlers();
        LoadPlusPanels();
        _interception.ConfigureLogging(_settings.Current);
        _interception.IgnoreServerCertificateErrors = _settings.Current.IgnoreServerCertificateErrors;
    }

    /// <summary>Exposed for E2E / headless tests.</summary>
    public InterceptionService Interception => _interception;

    /// <summary>Idempotent teardown for window close / app exit (restores WinINET).</summary>
    public void EnsureShutdown()
    {
        try
        {
            PersistSettings();
        }
        catch
        {
            // ignore
        }

        _interception.EnsureShutdown();
        SystemProxy = false;
    }

    private void WireEventHandlers()
    {
        AutoResponder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutoResponderViewModel.SelectedRule) &&
                AutoResponder.SelectedRule is { } selected)
            {
                AutoResponderMatch = selected.MatchUrl;
                AutoResponderStatus = selected.StatusCode;
                AutoResponderBody = selected.Body;
                AutoResponderContentType = selected.ContentType;
            }
        };
        AutoResponder.EnabledChanged += (_, _) => PersistAutoResponder();
        AutoResponder.Rules.CollectionChanged += (_, _) => { /* persistence via explicit commands */ };

        Breakpoints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BreakpointViewModel.Enabled) or nameof(BreakpointViewModel.UrlFilter))
            {
                PersistSettings();
            }
        };

        _buffer.SessionAdded += snapshot =>
        {
            // SessionStreamBuffer publishes from a background reader — marshal to UI thread
            // so ObservableCollection / DataGrid bindings actually update. When no Avalonia
            // Application is running (unit/E2E), invoke synchronously.
            if (Application.Current is null)
            {
                OnSessionAdded(snapshot);
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                OnSessionAdded(snapshot);
            }
            else
            {
                Dispatcher.UIThread.Post(() => OnSessionAdded(snapshot));
            }
        };
        _interception.SessionCaptured += (_, snap) =>
        {
            _registry.Add(snap);
            _buffer.Publish(snap);
        };
        _interception.SessionUpdated += (_, snap) =>
        {
            void Refresh()
            {
                if (ReferenceEquals(SelectedSession, snap))
                {
                    RefreshSelectedInspectors();
                }
            }

            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                Refresh();
            }
            else
            {
                Dispatcher.UIThread.Post(Refresh);
            }
        };
    }

    private void LoadPlusPanels()
    {
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

    private Task StopCaptureAsync()
    {
        _interception.Stop();
        SystemProxy = false;
        PersistSettings();
        StatusText = "Stopped (system proxy restored if it was on)";
        return Task.CompletedTask;
    }

    private Task ToggleCapturingAsync()
    {
        Capturing = !Capturing;
        return Task.CompletedTask;
    }

    private Task ClearSessionsAsync()
    {
        _all.Clear();
        Sessions.Clear();
        SelectedSession = null;
        StatusText = "Sessions cleared";
        return Task.CompletedTask;
    }

    private Task ToggleSystemProxyAsync()
    {
        if (!_interception.IsRunning)
        {
            StatusText = "Start interception before toggling system proxy";
            return Task.CompletedTask;
        }

        var enable = !SystemProxy;
        if (!_interception.SetSystemProxy(enable))
        {
            StatusText = enable
                ? "Failed to enable system proxy (check permissions / platform support)"
                : "Failed to restore system proxy settings";
            return Task.CompletedTask;
        }

        SystemProxy = enable;
        StatusText = enable
            ? "System proxy enabled (identity bypass). For Chrome: disable QUIC (--disable-quic) or H3 may bypass the proxy."
            : "System proxy restored";
        return Task.CompletedTask;
    }

    private Task InstallCaAsync()
    {
        if (!_interception.IsRunning)
        {
            StatusText = "Start interception first";
            return Task.CompletedTask;
        }

        var ok = _interception.InstallRootCertificate(machineStore: false);
        StatusText = ok
            ? "Root CA trusted in current user store"
            : "Root CA install failed or not present in store — try Export CA and install manually";
        return Task.CompletedTask;
    }

    private Task UntrustCaAsync()
    {
        if (!_interception.IsRunning)
        {
            StatusText = "Start interception first";
            return Task.CompletedTask;
        }

        _interception.UntrustRootCertificate(machineStore: false);
        StatusText = _interception.IsRootTrusted
            ? "Untrust requested but CA still present in store"
            : "Root CA removed from current user store";
        return Task.CompletedTask;
    }

    private Task ExportCaAsync()
    {
        var path = _interception.ExportRootCertificate();
        StatusText = path is null ? "No root certificate yet — start interception first" : "Exported CA: " + path;
        return Task.CompletedTask;
    }

    private Task DeviceCaSetupAsync()
    {
        StatusText =
            $"Device CA setup: 1) Export root CA from Capture menu. 2) Install the .cer on the device as a trusted CA. " +
            $"3) Set the device HTTP proxy to this PC's LAN IP on port {BindPort} (bind is {BindAddress}:{BindPort}). " +
            "Use BindAddress 0.0.0.0 so other devices can reach the proxy.";
        return Task.CompletedTask;
    }

    private Task LoadFromSelectedAsync()
    {
        if (SelectedSession is null)
        {
            StatusText = "Select a session to load into Composer";
            return Task.CompletedTask;
        }

        ComposerMethod = SelectedSession.Method;
        ComposerUrl = SelectedSession.Url;
        ComposerHeaders = SelectedSession.RequestHeadersText ?? "";
        ComposerBody = SelectedSession.RequestBodyText ?? "";
        StatusText = "Composer loaded from selected session";
        return Task.CompletedTask;
    }

    private Task AddAutoResponderRuleAsync()
    {
        AutoResponder.Rules.Add(new AutoResponderRule
        {
            MatchUrl = AutoResponderMatch,
            StatusCode = AutoResponderStatus,
            Body = AutoResponderBody,
            ContentType = AutoResponderContentType,
            Enabled = true,
        });
        PersistAutoResponder();
        StatusText = $"AutoResponder rule added ({AutoResponder.Rules.Count} total)";
        return Task.CompletedTask;
    }

    private Task DeleteAutoResponderRuleAsync()
    {
        if (AutoResponder.SelectedRule is null)
        {
            StatusText = "Select an AutoResponder rule to delete";
            return Task.CompletedTask;
        }

        AutoResponder.Rules.Remove(AutoResponder.SelectedRule);
        AutoResponder.SelectedRule = null;
        PersistAutoResponder();
        StatusText = "AutoResponder rule deleted";
        return Task.CompletedTask;
    }

    private Task UpdateAutoResponderRuleAsync()
    {
        if (AutoResponder.SelectedRule is null)
        {
            StatusText = "Select an AutoResponder rule to update";
            return Task.CompletedTask;
        }

        var rule = AutoResponder.SelectedRule;
        rule.MatchUrl = AutoResponderMatch;
        rule.StatusCode = AutoResponderStatus;
        rule.Body = AutoResponderBody;
        rule.ContentType = AutoResponderContentType;
        PersistAutoResponder();
        StatusText = "AutoResponder rule updated";
        return Task.CompletedTask;
    }

    private Task ApplyEditBodyAsync()
    {
        Breakpoints.EditBody(BreakpointEditBody);
        StatusText = "Breakpoint body edit applied (Continue to send)";
        return Task.CompletedTask;
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }
    public BreakpointViewModel Breakpoints { get; }
    public AutoResponderViewModel AutoResponder { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ExportHarCommand { get; }
    public ICommand ImportHarCommand { get; }
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
    public ICommand DeviceCaSetupCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand LoadFromSelectedCommand { get; }
    public ICommand SendComposerCommand { get; }
    public ICommand AddAutoResponderRuleCommand { get; }
    public ICommand DeleteAutoResponderRuleCommand { get; }
    public ICommand UpdateAutoResponderRuleCommand { get; }
    public ICommand ContinueBreakpointCommand { get; }
    public ICommand AbortBreakpointCommand { get; }
    public ICommand ApplyEditBodyCommand { get; }
    public ICommand ToggleDebugLoggingCommand { get; }

    public string BindAddress
    {
        get => _bindAddress;
        set => SetField(ref _bindAddress, value);
    }

    public int BindPort
    {
        get => _bindPort;
        set => SetField(ref _bindPort, value);
    }

    public bool BreakpointOnResponse
    {
        get => _breakpointOnResponse;
        set
        {
            if (SetField(ref _breakpointOnResponse, value))
            {
                _interception.BreakpointOnResponse = value;
                PersistSettings();
            }
        }
    }

    public string BreakpointEditBody
    {
        get => _breakpointEditBody;
        set => SetField(ref _breakpointEditBody, value);
    }

    public string? ScriptOnRequest
    {
        get => _scriptOnRequest;
        set
        {
            if (SetField(ref _scriptOnRequest, value))
            {
                _interception.ScriptOnRequest = value;
            }
        }
    }

    public string? ScriptOnResponse
    {
        get => _scriptOnResponse;
        set
        {
            if (SetField(ref _scriptOnResponse, value))
            {
                _interception.ScriptOnResponse = value;
            }
        }
    }

    public string ComposerMethod
    {
        get => _composerMethod;
        set => SetField(ref _composerMethod, value);
    }

    public string ComposerUrl
    {
        get => _composerUrl;
        set => SetField(ref _composerUrl, value);
    }

    public string ComposerHeaders
    {
        get => _composerHeaders;
        set => SetField(ref _composerHeaders, value);
    }

    public string ComposerBody
    {
        get => _composerBody;
        set => SetField(ref _composerBody, value);
    }

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

    public string AutoResponderContentType
    {
        get => _autoResponderContentType;
        set => SetField(ref _autoResponderContentType, value);
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
    public string SelectedFrames { get => _selectedFrames; set => SetField(ref _selectedFrames, value); }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        BindAddress = s.BindAddress;
        BindPort = s.BindPort is > 0 and < 65536 ? s.BindPort : 8866;
        AutoResponder.Enabled = s.AutoResponderEnabled;
        AutoResponder.LoadFromDtos(s.AutoResponderRules);
        Breakpoints.Enabled = s.BreakpointEnabled;
        Breakpoints.UrlFilter = string.IsNullOrEmpty(s.BreakpointUrlFilter) ? "*" : s.BreakpointUrlFilter;
        _breakpointOnResponse = s.BreakpointOnResponse;
        _scriptOnRequest = s.ScriptOnRequest;
        _scriptOnResponse = s.ScriptOnResponse;
        _interception.BreakpointOnResponse = _breakpointOnResponse;
        _interception.ScriptOnRequest = _scriptOnRequest;
        _interception.ScriptOnResponse = _scriptOnResponse;
        _interception.IgnoreServerCertificateErrors = s.IgnoreServerCertificateErrors;
        _interception.ConfigureLogging(s);
    }

    private void PersistAutoResponder()
    {
        _settings.Current.AutoResponderEnabled = AutoResponder.Enabled;
        _settings.Current.AutoResponderRules = AutoResponder.ToDtos();
        _settings.Save();
        AutoResponder.NotifyRulesChanged();
    }

    private void PersistSettings()
    {
        var s = _settings.Current;
        s.BindAddress = BindAddress;
        s.BindPort = BindPort;
        s.AutoResponderEnabled = AutoResponder.Enabled;
        s.AutoResponderRules = AutoResponder.ToDtos();
        s.BreakpointEnabled = Breakpoints.Enabled;
        s.BreakpointUrlFilter = Breakpoints.UrlFilter;
        s.BreakpointOnResponse = BreakpointOnResponse;
        s.ScriptOnRequest = ScriptOnRequest;
        s.ScriptOnResponse = ScriptOnResponse;
        s.IgnoreServerCertificateErrors = _interception.IgnoreServerCertificateErrors;
        s.LoggingEnabled = _settings.Current.LoggingEnabled;
        s.LoggingMinimumLevel = _settings.Current.LoggingMinimumLevel;
        s.LoggingEnableFile = _settings.Current.LoggingEnableFile;
        s.LoggingFilePath = _settings.Current.LoggingFilePath;
        _settings.Save();
    }

    private Task ToggleDebugLoggingAsync()
    {
        var s = _settings.Current;
        var enable = !s.LoggingEnableFile ||
                     !string.Equals(s.LoggingMinimumLevel, "Debug", StringComparison.OrdinalIgnoreCase);
        s.LoggingEnabled = true;
        s.LoggingEnableFile = enable;
        s.LoggingMinimumLevel = enable ? "Debug" : "Error";
        if (string.IsNullOrWhiteSpace(s.LoggingFilePath))
        {
            s.LoggingFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TitaniumInspector", "logs", "titanium-inspector.log");
        }

        _interception.ConfigureLogging(s);
        _settings.Save();
        StatusText = enable
            ? $"Debug file logging on: {s.LoggingFilePath}"
            : "Debug file logging off (Error level, file sink disabled)";
        return Task.CompletedTask;
    }

    private void RefreshSelectedInspectors()
    {
        if (_selected is null)
        {
            SelectedHeaders = SelectedBody = SelectedHex = SelectedFrames = "";
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

        if (_selected.WebSocketFrames is { Count: > 0 } frames)
        {
            var fb = new StringBuilder();
            foreach (var f in frames)
            {
                fb.Append('[').Append(f.Direction).Append(' ').Append(f.Opcode).Append("] ")
                    .AppendLine(f.PayloadPreview);
            }

            SelectedFrames = fb.ToString();
        }
        else
        {
            SelectedFrames = _selected.IsWebSocket ? "(no frames parsed)" : "";
        }
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
        var address = ParseBindAddress(BindAddress);
        PersistSettings();
        _interception.BreakpointOnResponse = BreakpointOnResponse;
        _interception.ScriptOnRequest = ScriptOnRequest;
        _interception.ScriptOnResponse = ScriptOnResponse;
        _interception.IgnoreServerCertificateErrors = _settings.Current.IgnoreServerCertificateErrors;
        _interception.ConfigureLogging(_settings.Current);
        await _interception.StartAsync(address, BindPort);
        Capturing = true;
        var display = address.Equals(IPAddress.Any) ? "0.0.0.0" : BindAddress;
        StatusText = $"Listening on {display}:{BindPort} — install CA, then enable system proxy (Capture menu). Chrome: use --disable-quic or H3 may bypass.";
    }

    private static IPAddress ParseBindAddress(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (bindAddress == "127.0.0.1")
        {
            return IPAddress.Loopback;
        }

        return IPAddress.Parse(bindAddress);
    }

    private async Task ReplaySelectedAsync()
    {
        if (SelectedSession is null)
        {
            StatusText = "Select a session to replay";
            return;
        }

        StatusText = "Replaying…";
        var result = await ReplayService.ReplayAsync(
            SelectedSession,
            ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors);
        StatusText = result.Ok
            ? $"Replay → HTTP {result.StatusCode}: {Truncate(result.Message, 120)}"
            : "Replay failed: " + result.Message;
    }

    private async Task SendComposerAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerUrl))
        {
            StatusText = "Composer URL is required";
            return;
        }

        StatusText = "Composer sending…";
        var template = new SessionSnapshot
        {
            Method = string.IsNullOrWhiteSpace(ComposerMethod) ? "GET" : ComposerMethod,
            Url = ComposerUrl,
            RequestHeadersText = ComposerHeaders,
            RequestBodyText = ComposerBody,
            ContentType = GuessContentType(ComposerHeaders),
        };

        var result = await ReplayService.ReplayAsync(
            template,
            editedUrl: ComposerUrl,
            editedMethod: ComposerMethod,
            editedBody: ComposerBody,
            editedHeaders: ComposerHeaders,
            ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors);

        if (!result.Ok)
        {
            StatusText = "Composer failed: " + result.Message;
            return;
        }

        var snap = new SessionSnapshot
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Method = template.Method,
            Url = ComposerUrl,
            Host = TryHost(ComposerUrl),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = ComposerHeaders,
            RequestBodyText = ComposerBody,
            StatusCode = result.StatusCode,
            ResponseHeadersText = result.ResponseHeaders,
            ResponseBodyText = result.ResponseBody,
            ContentType = template.ContentType,
            BodySize = result.ResponseBody?.Length,
            Protocol = "Composer",
        };

        _registry.Add(snap);
        _all.Add(snap);
        ApplyFilter();
        SelectedSession = snap;
        StatusText = $"Composer → HTTP {result.StatusCode} (session #{snap.Id})";
    }

    private static string? GuessContentType(string headers)
    {
        var map = SessionInspectors.ParseHeaderBlock(headers);
        return map.TryGetValue("Content-Type", out var ct) ? ct : null;
    }

    private static string? TryHost(string url)
    {
        try
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ExportHarAsync()
    {
        var path = await PickSavePathAsync("Export HAR", "titanium-inspector.har", "HAR", "*.har")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                       $"titanium-inspector-{DateTime.Now:yyyyMMddHHmmss}.har");
        await SessionArchive.ExportHarAsync(_all, path);
        StatusText = "Exported HAR: " + path;
    }

    private async Task ImportHarAsync()
    {
        var path = await PickOpenPathAsync("Import HAR", "HAR", "*.har", "*.zip");
        if (path is null)
        {
            path = FindLatestDesktop("*.har") ?? FindLatestDesktop("titanium-inspector-*.zip");
        }

        if (path is null)
        {
            StatusText = "No .har or archive on Desktop to import";
            return;
        }

        List<SessionSnapshot> imported;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            imported = await SessionArchive.ImportNativeArchiveAsync(path);
        }
        else
        {
            imported = await SessionArchive.ImportHarAsync(path);
        }

        foreach (var snap in imported)
        {
            _registry.Add(snap);
            _all.Add(snap);
        }

        ApplyFilter();
        StatusText = $"Imported {imported.Count} sessions from {Path.GetFileName(path)}";
    }

    private async Task ExportArchiveAsync()
    {
        var path = await PickSavePathAsync("Export archive", "titanium-inspector.zip", "ZIP", "*.zip")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                       $"titanium-inspector-{DateTime.Now:yyyyMMddHHmmss}.zip");
        await SessionArchive.ExportNativeArchiveAsync(_all, path);
        StatusText = "Exported archive: " + path;
    }

    private async Task ImportArchiveAsync()
    {
        var path = await PickOpenPathAsync("Import archive", "ZIP", "*.zip")
                   ?? FindLatestDesktop("titanium-inspector-*.zip");
        if (path is null)
        {
            StatusText = "No titanium-inspector-*.zip on Desktop to import";
            return;
        }

        var imported = await SessionArchive.ImportNativeArchiveAsync(path);
        foreach (var snap in imported)
        {
            _registry.Add(snap);
            _all.Add(snap);
        }

        ApplyFilter();
        StatusText = $"Imported {imported.Count} sessions from {Path.GetFileName(path)}";
    }

    private static string? FindLatestDesktop(string pattern)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Directory.EnumerateFiles(desktop, pattern)
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    private static TopLevel? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }

    private static async Task<string?> PickSavePathAsync(string title, string suggested, string name, string pattern)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanSave: true } sp)
        {
            return null;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            FileTypeChoices =
            [
                new FilePickerFileType(name) { Patterns = [pattern] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    private static async Task<string?> PickOpenPathAsync(string title, string name, params string[] patterns)
    {
        var top = TryGetMainWindow();
        if (top?.StorageProvider is not { CanOpen: true } sp)
        {
            return null;
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(name) { Patterns = patterns.ToList() },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
