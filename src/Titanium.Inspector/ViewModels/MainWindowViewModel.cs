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
using Titanium.Inspector.Views;

namespace Titanium.Inspector.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const string ZipFileFilter = "*.zip";

    private readonly SessionStreamBuffer _buffer;
    private readonly SessionRegistry _registry;
    private readonly SessionStore _store;
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly InterceptionService _interception;
    private readonly IInspectorDialogs _dialogs;
    private readonly IInspectorPathPicker _pathPicker;
    private readonly ObservableCollection<SessionSnapshot> _all;
    private readonly List<SessionSnapshot> _selectedSessions = new();
    private string _statusText = "Ready";
    private string _sessionCountText = "Sessions: 0";
    private string _searchQuery = "";
    private SessionSnapshot? _selected;
    private string _selectedHeaders = "";
    private string _selectedBody = "";
    private string _selectedHex = "";
    private string _selectedFrames = "";
    private bool _capturing = true;
    private bool _systemProxy;
    private bool _autoStartCapture = true;
    private bool _autoSystemProxyOnStart = true;
    private bool _debugFileLogging;
    /// <summary>Prefs as loaded from disk — used for auto-start so MenuItem binding cannot clobber before Opened.</summary>
    private bool _launchAutoStartCapture = true;
    private bool _launchAutoSystemProxyOnStart = true;
    private bool _decryptHttps;
    private bool _decryptHttpsBusy;
    private string _autoResponderMatch = "*";
    private string _autoResponderBody = "OK";
    private string _autoResponderContentType = "text/plain";
    private int _autoResponderStatus = 200;
    private string _plusPanelsSummary = "";
    private string _bindAddress = "127.0.0.1";
    private int _bindPort = 8866;
    private string _endpointStatusText = "Not listening";
    private string _interceptToggleText = "Start interception";
    /// <summary>Sticky intent: re-enable system proxy on the next Start after a Stop that had it on.</summary>
    private bool _reenableSystemProxyOnStart;
    private bool _stopBusy;
    private bool _breakpointOnResponse;
    private string _breakpointEditBody = "";
    private string? _scriptOnRequest;
    private string? _scriptOnResponse;
    private int _selectedOuterPaneIndex;
    private int _selectedInspectTabIndex;
    private int _selectedToolsTabIndex;
    private bool _showSessionDetails;
    private bool _showWsFramesTab;
    private string _composerMethod = "GET";
    private string _composerUrl = "";
    private string _composerHeaders = "";
    private string _composerBody = "";

    public MainWindowViewModel(
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        SettingsService settings,
        InterceptionService? interception = null,
        IInspectorDialogs? dialogs = null,
        IInspectorPathPicker? pathPicker = null)
    {
        _buffer = buffer;
        _registry = registry;
        _store = registry.Store;
        _all = _store.Sessions;
        _updates = updates;
        _settings = settings;
        _interception = interception ?? new InterceptionService();
        _dialogs = dialogs ?? new AvaloniaInspectorDialogs();
        _pathPicker = pathPicker ?? new AvaloniaInspectorPathPicker();
        Sessions = new ObservableCollection<SessionSnapshot>();
        Breakpoints = new BreakpointViewModel();
        AutoResponder = new AutoResponderViewModel();
        _interception.AutoResponder = AutoResponder;
        _interception.Breakpoints = Breakpoints;

        LoadFromSettings();

        CheckForUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync());
        ExportHarCommand = new RelayCommand(async () => await ExportHarAsync());
        ExportSelectedHarCommand = new RelayCommand(async () => await ExportSelectedHarAsync());
        ImportHarCommand = new RelayCommand(async () => await ImportHarAsync());
        ExportArchiveCommand = new RelayCommand(async () => await ExportArchiveAsync());
        ExportSelectedArchiveCommand = new RelayCommand(async () => await ExportSelectedArchiveAsync());
        ImportArchiveCommand = new RelayCommand(async () => await ImportArchiveAsync());
        StartCaptureCommand = new RelayCommand(async () => await StartCaptureAsync());
        StopCaptureCommand = new RelayCommand(StopCaptureAsync);
        ToggleInterceptCommand = new RelayCommand(ToggleInterceptAsync);
        ToggleCapturingCommand = new RelayCommand(ToggleCapturingAsync);
        ToggleAutoStartCaptureCommand = new RelayCommand(() =>
        {
            AutoStartCapture = !AutoStartCapture;
            return Task.CompletedTask;
        });
        ToggleAutoSystemProxyOnStartCommand = new RelayCommand(() =>
        {
            AutoSystemProxyOnStart = !AutoSystemProxyOnStart;
            return Task.CompletedTask;
        });
        ToggleDecryptHttpsCommand = new RelayCommand(() =>
        {
            DecryptHttps = !DecryptHttps;
            return Task.CompletedTask;
        });
        ClearSessionsCommand = new RelayCommand(ClearSessionsAsync);
        RemoveSelectedSessionsCommand = new RelayCommand(RemoveSelectedSessionsAsync);
        ToggleSystemProxyCommand = new RelayCommand(ToggleSystemProxyAsync);
        InstallCaCommand = new RelayCommand(InstallCaAsync);
        UntrustCaCommand = new RelayCommand(UntrustCaAsync);
        ExportCaCommand = new RelayCommand(ExportCaAsync);
        DeviceCaSetupCommand = new RelayCommand(DeviceCaSetupAsync);
        OpenLoopbackExemptCommand = new RelayCommand(OpenLoopbackExemptAsync);
        ReplayCommand = new RelayCommand(async () => await ReplaySelectedAsync());
        LoadFromSelectedCommand = new RelayCommand(LoadFromSelectedAsync);
        LoadIntoComposerCommand = new RelayCommand(LoadIntoComposerAsync);
        CopyUrlCommand = new RelayCommand(CopyUrlAsync);
        FilterByHostCommand = new RelayCommand(FilterByHostAsync);
        FilterByProcessCommand = new RelayCommand(FilterByProcessAsync);
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
        CloseSessionDetailsCommand = new RelayCommand(CloseSessionDetailsAsync);
        OpenToolsComposerCommand = new RelayCommand(() => OpenToolsTabAsync(0));
        OpenToolsBreakpointsCommand = new RelayCommand(() => OpenToolsTabAsync(1));
        OpenToolsAutoResponderCommand = new RelayCommand(() => OpenToolsTabAsync(2));
        OpenToolsScriptsCommand = new RelayCommand(() => OpenToolsTabAsync(3));
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchQuery = SessionSearch.ClearFilters(SearchQuery);
            return Task.CompletedTask;
        });

        WireEventHandlers();
        LoadPlusPanels();
        _interception.ConfigureLogging(_settings.Current);
        _interception.IgnoreServerCertificateErrors = _settings.Current.IgnoreServerCertificateErrors;
        _interception.DecryptHttps = _decryptHttps;
        ShowLoopbackExemptMenu = AppContainerLoopback.IsSupported;
    }

    /// <summary>Exposed for E2E / headless tests.</summary>
    public InterceptionService Interception => _interception;

    /// <summary>Exposed for E2E / headless tests.</summary>
    public IInspectorDialogs Dialogs => _dialogs;

    /// <summary>Exposed for E2E / headless tests.</summary>
    public IInspectorPathPicker PathPicker => _pathPicker;

    /// <summary>Exposed for E2E / headless tests — seeds the in-memory capture list.</summary>
    public void SeedSession(SessionSnapshot snapshot)
    {
        _store.Add(snapshot);
        OnSessionAddedToFilter(snapshot);
    }

    /// <summary>Called from the session grid when Extended multi-select changes.</summary>
    public void SetSelectedSessions(IReadOnlyList<SessionSnapshot> selected)
    {
        _selectedSessions.Clear();
        _selectedSessions.AddRange(selected);
        NotifyFilterSelectionProperties();
    }

    /// <summary>
    /// After the main window is shown: optionally start capture and system proxy.
    /// Idempotent if already running.
    /// </summary>
    public async Task TryAutoStartAsync()
    {
        // MenuItem CheckBox TwoWay bindings can write false during init and PersistSettings.
        // Prefer the disk snapshot from LoadFromSettings for this first-start decision.
        RestoreLaunchPreferencesIfClobbered();

        if (!_launchAutoStartCapture)
        {
            return;
        }

        if (!_interception.IsRunning)
        {
            await StartCaptureAsync();
        }

        if (!_launchAutoSystemProxyOnStart || !_interception.IsRunning || SystemProxy)
        {
            return;
        }

        SystemProxy = true;
        if (SystemProxy)
        {
            StatusText =
                $"Listening on {FormatBindDisplay()}:{BindPort}; system proxy on. HTTPS shows as CONNECT until Decrypt HTTPS is enabled." +
                " Chrome/Edge: --disable-quic or HTTP/3 may bypass the proxy.";
        }
        else
        {
            StatusText =
                $"Listening on {FormatBindDisplay()}:{BindPort}, but system proxy failed to enable — use the System proxy checkbox.";
        }
    }

    /// <summary>
    /// If Avalonia menu bindings flipped prefs before Opened, put launch-time values back
    /// (and rewrite settings) so auto-start and the menu checkboxes stay honest.
    /// </summary>
    private void RestoreLaunchPreferencesIfClobbered()
    {
        var changed = false;
        if (_autoStartCapture != _launchAutoStartCapture)
        {
            _autoStartCapture = _launchAutoStartCapture;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoStartCapture)));
            changed = true;
        }

        if (_autoSystemProxyOnStart != _launchAutoSystemProxyOnStart)
        {
            _autoSystemProxyOnStart = _launchAutoSystemProxyOnStart;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoSystemProxyOnStart)));
            changed = true;
        }

        if (changed)
        {
            PersistSettings();
        }
    }

    /// <summary>Idempotent teardown for tests / process exit (restores WinINET).</summary>
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
        SetSystemProxyCore(false);
        RefreshEndpointAndBindUi();
        _registry.Dispose();
    }

    /// <summary>
    /// Title-bar close: save settings and stop the proxy off the UI thread so WinINET
    /// refresh cannot deadlock the closing window.
    /// </summary>
    public void BeginBackgroundShutdown()
    {
        try
        {
            PersistSettings();
        }
        catch
        {
            // ignore
        }

        // UI flag only — do not call SetSystemProxy on the UI thread (WinINET deadlock risk).
        SetSystemProxyCore(false);
        _interception.BeginBackgroundShutdown();
    }

    private void WireEventHandlers()
    {
        WireAutoResponderHandlers();
        WireBreakpointHandlers();
        WireSessionPipelineHandlers();
    }

    private void WireAutoResponderHandlers()
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
    }

    private void WireBreakpointHandlers()
    {
        Breakpoints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BreakpointViewModel.Enabled) or nameof(BreakpointViewModel.UrlFilter))
            {
                PersistSettings();
            }
        };
    }

    private void WireSessionPipelineHandlers()
    {
        _buffer.SessionAdded += snapshot => MarshalToUi(() =>
        {
            _store.Add(snapshot);
            OnSessionAddedToFilter(snapshot);
        });
        _store.SessionsRemoved += removed => MarshalToUi(() => OnSessionsRemoved(removed));
        _interception.SessionCaptured += (_, snap) => _buffer.Publish(snap);
        _interception.SessionUpdated += (_, snap) =>
            MarshalToUi(() =>
            {
                _store.NotifyUpdated(snap);
                if (ReferenceEquals(SelectedSession, snap))
                {
                    RefreshSelectedInspectors();
                }
            });
    }

    /// <summary>
    /// SessionStreamBuffer publishes from a background reader — marshal to UI thread
    /// so ObservableCollection / DataGrid bindings actually update. When no Avalonia
    /// Application is running (unit/E2E), invoke synchronously.
    /// </summary>
    private static void MarshalToUi(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static Task MarshalToUiAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
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

    private async Task ToggleInterceptAsync()
    {
        if (_interception.IsRunning)
        {
            await StopCaptureAsync();
        }
        else
        {
            await StartCaptureAsync();
        }
    }

    private async Task StopCaptureAsync() =>
        await StopCaptureCoreAsync("Stopped (system proxy restored if it was on)");

    /// <summary>
    /// Tear down the proxy off the UI thread — WinINET restore + listener stop can hang Avalonia
    /// for several seconds if run on the dispatcher (same rationale as <see cref="BeginBackgroundShutdown"/>).
    /// </summary>
    private async Task StopCaptureCoreAsync(string statusAfterStop)
    {
        if (_stopBusy || !_interception.IsRunning)
        {
            return;
        }

        _stopBusy = true;
        _reenableSystemProxyOnStart = SystemProxy;
        StatusText = "Stopping…";

        try
        {
            await Task.Run(() => _interception.Stop()).ConfigureAwait(false);

            await MarshalToUiAsync(() =>
            {
                SetSystemProxyCore(false);
                PersistSettings();
                RefreshEndpointAndBindUi();
                StatusText = statusAfterStop;
            }).ConfigureAwait(false);
        }
        finally
        {
            _stopBusy = false;
        }
    }

    private Task ToggleCapturingAsync()
    {
        Capturing = !Capturing;
        return Task.CompletedTask;
    }

    private Task ClearSessionsAsync()
    {
        _store.Clear();
        Sessions.Clear();
        _selectedSessions.Clear();
        SelectedSession = null;
        RefreshSessionCountText();
        StatusText = "Sessions cleared";
        return Task.CompletedTask;
    }

    private Task RemoveSelectedSessionsAsync()
    {
        var selected = ResolveExportSelection();
        if (selected.Count == 0)
        {
            StatusText = "Select one or more sessions to remove";
            return Task.CompletedTask;
        }

        var ids = selected.Select(s => s.Id).ToHashSet();
        _store.Remove(ids);

        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(Sessions[i].Id))
            {
                Sessions.RemoveAt(i);
            }
        }

        _selectedSessions.Clear();
        if (SelectedSession is not null && ids.Contains(SelectedSession.Id))
        {
            SelectedSession = null;
        }

        RefreshSessionCountText();
        StatusText = selected.Count == 1 ? "Removed 1 session" : $"Removed {selected.Count} sessions";
        return Task.CompletedTask;
    }

    private Task ToggleSystemProxyAsync()
    {
        SystemProxy = !SystemProxy;
        return Task.CompletedTask;
    }

    private async Task InstallCaAsync()
    {
        if (!_interception.IsRunning)
        {
            StatusText = "Start interception first";
            return;
        }

        var ok = _interception.InstallRootCertificate(machineStore: false);
        if (!ok)
        {
            var owner = TryGetMainWindow();
            if (await _dialogs.ConfirmElevateRootCaAsync(owner))
                ok = _interception.InstallRootCertificateAsAdmin(machineStore: false);
            else
            {
                StatusText = "Root CA install cancelled elevation - try Export CA and install manually (Keychain / NSS / cert store)";
                return;
            }
        }

        StatusText = ok
            ? "Root CA trusted - ready to enable Decrypt HTTPS"
            : "Root CA install failed (store / Keychain / NSS) - try Export CA, or allow the admin prompt";
    }


    private async Task UntrustCaAsync()
    {
        if (!_interception.IsRunning)
        {
            StatusText = "Start interception first";
            return;
        }

        var owner = TryGetMainWindow();
        if (!await _dialogs.ConfirmRemoveRootCaAsync(owner))
        {
            StatusText = "Remove root CA cancelled";
            return;
        }

        _interception.UntrustRootCertificate(machineStore: false);
        if (DecryptHttps)
        {
            SetDecryptHttpsCore(false);
        }

        StatusText = _interception.IsRootTrusted
            ? "Remove requested but CA still present in store"
            : "Root CA removed from current user store; Decrypt HTTPS is off until you install the CA again";
    }

    private Task ExportCaAsync()
    {
        var path = _interception.ExportRootCertificate();
        StatusText = path is null ? "No root certificate yet — start interception first" : "Exported CA: " + path;
        return Task.CompletedTask;
    }

    private async Task OpenLoopbackExemptAsync()
    {
        if (!AppContainerLoopback.IsSupported)
        {
            StatusText = "Loopback exemptions require Windows 8 or later";
            return;
        }

        var owner = TryGetMainWindow();
        if (owner is null)
        {
            if (AppContainerLoopback.TryProbeApis(out var msg))
            {
                StatusText = "Loopback APIs OK (no UI owner): " + msg;
            }
            else
            {
                StatusText = msg;
            }

            return;
        }

        await LoopbackExemptWindow.ShowAsync(owner);
        StatusText = "Loopback exemption dialog closed";
    }

    private async Task DeviceCaSetupAsync()
    {
        var message =
            "To decrypt HTTPS from a phone or other device:\n\n" +
            "1. Export the root CA (use Export CA below, or Capture → Export root CA…).\n" +
            "2. Install the .cer on the device as a trusted CA.\n" +
            $"3. Set the device HTTP proxy to this PC's LAN IP on port {BindPort} " +
            $"(current bind is {BindAddress}:{BindPort}).\n\n" +
            "Use Bind address 0.0.0.0 so other devices can reach the proxy.";

        var owner = TryGetMainWindow();
        if (await _dialogs.ShowDeviceCaSetupAsync(owner, message))
        {
            await ExportCaAsync();
        }
    }

    private async Task LoadFromSelectedAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
        {
            StatusText = "Select a session to load into Composer";
            return;
        }

        await _store.EnsureBodiesLoadedAsync(selected).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            ComposerMethod = selected.Method;
            ComposerUrl = selected.Url;
            ComposerHeaders = selected.RequestHeadersText ?? "";
            ComposerBody = selected.RequestBodyText ?? "";
            StatusText = "Composer loaded from selected session";
        }).ConfigureAwait(false);
    }

    private async Task LoadIntoComposerAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
        {
            StatusText = "Select a session to load into Composer";
            return;
        }

        await _store.EnsureBodiesLoadedAsync(selected).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            ComposerMethod = selected.Method;
            ComposerUrl = selected.Url;
            ComposerHeaders = selected.RequestHeadersText ?? "";
            ComposerBody = selected.RequestBodyText ?? "";
            StatusText = "Composer loaded from selected session";
        }).ConfigureAwait(false);
        await OpenToolsTabAsync(0).ConfigureAwait(false);
    }

    private async Task CopyUrlAsync()
    {
        var urls = ResolveCopyUrls();
        if (urls.Count == 0)
        {
            StatusText = "Select a session with a URL to copy";
            return;
        }

        var text = string.Join(Environment.NewLine, urls);
        var window = TryGetMainWindow();
        if (window?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }

        StatusText = urls.Count == 1 ? "Copied URL" : $"Copied {urls.Count} URLs";
    }

    private Task FilterByHostAsync()
    {
        var host = ResolveUnanimousFilterHost();
        if (string.IsNullOrEmpty(host))
        {
            StatusText = "Filter by host needs one shared host in the selection";
            return Task.CompletedTask;
        }

        SearchQuery = SessionSearch.SetKeyedToken(SearchQuery, "host", host);
        StatusText = $"Filtered by host:{host}";
        return Task.CompletedTask;
    }

    private Task FilterByProcessAsync()
    {
        var process = ResolveUnanimousFilterProcess();
        if (string.IsNullOrEmpty(process))
        {
            StatusText = "Filter by process needs one shared process in the selection";
            return Task.CompletedTask;
        }

        SearchQuery = SessionSearch.SetKeyedToken(SearchQuery, "process", process);
        StatusText = $"Filtered by process:{process}";
        return Task.CompletedTask;
    }

    /// <summary>True when selection shares one non-empty host (single or multi-select).</summary>
    public bool CanFilterByHost => ResolveUnanimousFilterHost() is not null;

    /// <summary>True when selection shares one non-empty process (single or multi-select).</summary>
    public bool CanFilterByProcess => ResolveUnanimousFilterProcess() is not null;

    /// <summary>True when at least one session is selected.</summary>
    public bool HasSelectedSessions => ResolveFilterSelection().Count > 0;

    /// <summary>True when exactly one session is selected (Replay / Composer).</summary>
    public bool HasSingleSelectedSession => ResolveFilterSelection().Count == 1;

    /// <summary>True when at least one selected session has a URL to copy.</summary>
    public bool CanCopyUrl => ResolveCopyUrls().Count > 0;

    private string? ResolveUnanimousFilterHost()
    {
        var selection = ResolveFilterSelection();
        if (selection.Count == 0)
        {
            return null;
        }

        string? host = null;
        foreach (var session in selection)
        {
            var value = ResolveSessionHost(session);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (host is null)
            {
                host = value;
            }
            else if (!host.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return host;
    }

    private string? ResolveUnanimousFilterProcess()
    {
        var selection = ResolveFilterSelection();
        if (selection.Count == 0)
        {
            return null;
        }

        string? process = null;
        foreach (var session in selection)
        {
            var value = ResolveSessionProcess(session);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (process is null)
            {
                process = value;
            }
            else if (!process.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return process;
    }

    private IReadOnlyList<SessionSnapshot> ResolveFilterSelection()
    {
        if (_selectedSessions.Count > 0)
        {
            return _selectedSessions;
        }

        return SelectedSession is null ? Array.Empty<SessionSnapshot>() : [SelectedSession];
    }

    private static string? ResolveSessionHost(SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.Host))
        {
            return session.Host.Trim();
        }

        return Uri.TryCreate(session.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }

    private static string? ResolveSessionProcess(SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.ProcessName))
        {
            return session.ProcessName.Trim();
        }

        return session.ProcessId > 0 ? session.ProcessId.ToString() : null;
    }

    private void NotifyFilterSelectionProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFilterByHost)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFilterByProcess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedSessions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSingleSelectedSession)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopyUrl)));
    }

    private List<string> ResolveCopyUrls() =>
        ResolveFilterSelection()
            .Where(snap => !string.IsNullOrEmpty(snap.Url))
            .Select(snap => snap.Url)
            .ToList();

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
    public ICommand ExportSelectedHarCommand { get; }
    public ICommand ImportHarCommand { get; }
    public ICommand ExportArchiveCommand { get; }
    public ICommand ExportSelectedArchiveCommand { get; }
    public ICommand ImportArchiveCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand ToggleInterceptCommand { get; }
    public ICommand ToggleCapturingCommand { get; }
    public ICommand ToggleAutoStartCaptureCommand { get; }
    public ICommand ToggleAutoSystemProxyOnStartCommand { get; }
    public ICommand ToggleDecryptHttpsCommand { get; }
    public ICommand ClearSessionsCommand { get; }
    public ICommand RemoveSelectedSessionsCommand { get; }
    public ICommand ToggleSystemProxyCommand { get; }
    public ICommand InstallCaCommand { get; }
    public ICommand UntrustCaCommand { get; }
    public ICommand ExportCaCommand { get; }
    public ICommand DeviceCaSetupCommand { get; }
    public ICommand OpenLoopbackExemptCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand LoadFromSelectedCommand { get; }
    public ICommand LoadIntoComposerCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand FilterByHostCommand { get; }
    public ICommand FilterByProcessCommand { get; }
    public ICommand SendComposerCommand { get; }
    public ICommand AddAutoResponderRuleCommand { get; }
    public ICommand DeleteAutoResponderRuleCommand { get; }
    public ICommand UpdateAutoResponderRuleCommand { get; }
    public ICommand ContinueBreakpointCommand { get; }
    public ICommand AbortBreakpointCommand { get; }
    public ICommand ApplyEditBodyCommand { get; }
    public ICommand ToggleDebugLoggingCommand { get; }
    public ICommand CloseSessionDetailsCommand { get; }
    public ICommand OpenToolsComposerCommand { get; }
    public ICommand OpenToolsBreakpointsCommand { get; }
    public ICommand OpenToolsAutoResponderCommand { get; }
    public ICommand OpenToolsScriptsCommand { get; }
    public ICommand ClearFiltersCommand { get; }

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

    /// <summary>Bind address/port are start-time config; editable only while the proxy is stopped.</summary>
    public bool BindFieldsEnabled => !_interception.IsRunning;

    /// <summary>True while the proxy endpoint is listening (drives toolbar accent / live indicator).</summary>
    public bool IsIntercepting => _interception.IsRunning;

    /// <summary>Toolbar button label: Start or Stop interception.</summary>
    public string InterceptToggleText
    {
        get => _interceptToggleText;
        private set => SetField(ref _interceptToggleText, value);
    }

    /// <summary>Compact live endpoint label (toolbar / status); distinct from transient <see cref="StatusText"/>.</summary>
    public string EndpointStatusText
    {
        get => _endpointStatusText;
        private set => SetField(ref _endpointStatusText, value);
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
        set // NOSONAR S4275 -- fail paths leave _systemProxy unchanged and re-raise PropertyChanged to snap the checkbox back
        {
            if (_systemProxy == value)
            {
                return;
            }

            if (value)
            {
                if (!_interception.IsRunning)
                {
                    StatusText = "Start interception before enabling system proxy";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                    return;
                }

                if (!_interception.SetSystemProxy(true))
                {
                    StatusText =
                        "Failed to enable system proxy (permissions, cancelled admin prompt, or unsupported desktop environment)";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                    return;
                }

                SetSystemProxyCore(true);
                StatusText =
                    "System proxy enabled (identity bypass). For Chrome: disable QUIC (--disable-quic) or H3 may bypass the proxy.";
                return;
            }

            if (_interception.IsRunning && _interception.SystemProxyEnabled &&
                !_interception.SetSystemProxy(false))
            {
                StatusText = "Failed to restore system proxy settings";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                return;
            }

            SetSystemProxyCore(false);
            StatusText = "System proxy restored";
        }
    }

    private void SetSystemProxyCore(bool enabled)
    {
        if (_systemProxy == enabled)
        {
            return;
        }

        _systemProxy = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
    }

    public bool AutoStartCapture
    {
        get => _autoStartCapture;
        set
        {
            if (SetField(ref _autoStartCapture, value))
            {
                PersistSettings();
            }
        }
    }

    public bool AutoSystemProxyOnStart
    {
        get => _autoSystemProxyOnStart;
        set
        {
            if (SetField(ref _autoSystemProxyOnStart, value))
            {
                PersistSettings();
            }
        }
    }

    /// <summary>When true, file logging is on at Debug level.</summary>
    public bool DebugFileLogging
    {
        get => _debugFileLogging;
        private set => SetField(ref _debugFileLogging, value);
    }

    /// <summary>When true, MITM decrypts HTTPS; when false, tunnels stay CONNECT.</summary>
    public bool DecryptHttps
    {
        get => _decryptHttps;
        set // NOSONAR S4275 -- true path updates _decryptHttps via SetDecryptHttpsCore after async trust flow
        {
            if (_decryptHttpsBusy || _decryptHttps == value)
            {
                return;
            }

            if (value)
            {
                _ = EnableDecryptHttpsAsync();
            }
            else
            {
                SetDecryptHttpsCore(false);
                StatusText = "Decrypt HTTPS off — HTTPS shown as CONNECT tunnels";
            }
        }
    }

    public bool ShowLoopbackExemptMenu { get; }

    /// <summary>Right pane visibility (Inspect + Tools). Kept name for tests.</summary>
    public bool ShowSessionDetails
    {
        get => _showSessionDetails;
        set
        {
            if (SetField(ref _showSessionDetails, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionDetailsPaneWidth)));
            }
        }
    }

    public GridLength SessionDetailsPaneWidth =>
        _showSessionDetails ? new GridLength(420) : new GridLength(0);

    public bool HasSelectedSession => _selected is not null;

    public bool ShowInspectEmpty => _selected is null;

    public bool ShowWsFramesTab
    {
        get => _showWsFramesTab;
        private set => SetField(ref _showWsFramesTab, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                ApplyFilter();
                RefreshSessionCountText();
                NotifyQuickFilterProperties();
            }
        }
    }

    /// <summary>Quick filter: exclude CONNECT/tunnel rows (<c>hide:tunnel</c>).</summary>
    public bool HideTunnelsFilter
    {
        get => SessionSearch.ContainsToken(_searchQuery, "hide", "tunnel");
        set
        {
            if (value == HideTunnelsFilter)
            {
                return;
            }

            SearchQuery = SessionSearch.ToggleToken(_searchQuery, "hide", "tunnel");
        }
    }

    /// <summary>Quick filter: exclude image/static rows (<c>hide:image</c>).</summary>
    public bool HideImagesFilter
    {
        get => SessionSearch.ContainsToken(_searchQuery, "hide", "image");
        set
        {
            if (value == HideImagesFilter)
            {
                return;
            }

            SearchQuery = SessionSearch.ToggleToken(_searchQuery, "hide", "image");
        }
    }

    /// <summary>Quick filter: only 4xx/5xx responses (<c>is:error</c>).</summary>
    public bool ErrorsOnlyFilter
    {
        get => SessionSearch.ContainsToken(_searchQuery, "is", "error");
        set
        {
            if (value == ErrorsOnlyFilter)
            {
                return;
            }

            SearchQuery = SessionSearch.ToggleToken(_searchQuery, "is", "error");
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

            _store.PinnedSessionId = value?.Id;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedSession)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowInspectEmpty)));
            NotifyFilterSelectionProperties();

            if (value is not null)
            {
                ShowSessionDetails = true;
                SelectedOuterPaneIndex = 0;
            }

            UpdateWsFramesVisibility();
            if (value is { BodiesOnDisk: true })
            {
                _ = LoadSelectedBodiesAsync(value);
            }
            else
            {
                RefreshSelectedInspectors();
            }
        }
    }

    public string SelectedHeaders { get => _selectedHeaders; set => SetField(ref _selectedHeaders, value); }
    public string SelectedBody { get => _selectedBody; set => SetField(ref _selectedBody, value); }
    public string SelectedHex { get => _selectedHex; set => SetField(ref _selectedHex, value); }
    public string SelectedFrames { get => _selectedFrames; set => SetField(ref _selectedFrames, value); }

    /// <summary>0 = Inspect, 1 = Tools.</summary>
    public int SelectedOuterPaneIndex
    {
        get => _selectedOuterPaneIndex;
        set
        {
            if (SetField(ref _selectedOuterPaneIndex, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
            }
        }
    }

    /// <summary>Inspect tabs: 0 Headers, 1 Body, 2 Hex, 3 WS Frames.</summary>
    public int SelectedInspectTabIndex
    {
        get => _selectedInspectTabIndex;
        set
        {
            if (SetField(ref _selectedInspectTabIndex, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
            }
        }
    }

    /// <summary>Tools tabs: 0 Composer, 1 Breakpoints, 2 AutoResponder, 3 Scripts.</summary>
    public int SelectedToolsTabIndex
    {
        get => _selectedToolsTabIndex;
        set
        {
            if (SetField(ref _selectedToolsTabIndex, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
            }
        }
    }

    /// <summary>
    /// Compatibility index for tests: 0–3 Inspect, 4–7 Tools (Composer…Scripts).
    /// </summary>
    public int SelectedDetailTabIndex
    {
        get => SelectedOuterPaneIndex == 0
            ? SelectedInspectTabIndex
            : 4 + SelectedToolsTabIndex;
        set
        {
            if (value < 4)
            {
                SelectedOuterPaneIndex = 0;
                SelectedInspectTabIndex = Math.Clamp(value, 0, 3);
            }
            else
            {
                SelectedOuterPaneIndex = 1;
                SelectedToolsTabIndex = Math.Clamp(value - 4, 0, 3);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>Live session total; kept separate so capture traffic does not wipe command feedback.</summary>
    public string SessionCountText
    {
        get => _sessionCountText;
        private set => SetField(ref _sessionCountText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionGridLayoutDto? GetSessionGridLayout() => _settings.Current.SessionGridLayout;

    public void PersistSessionGridLayout(SessionGridLayoutDto layout)
    {
        _settings.Current.SessionGridLayout = layout;
        _settings.Save();
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        BindAddress = s.BindAddress;
        BindPort = s.BindPort is > 0 and < 65536 ? s.BindPort : 8866;
        _launchAutoStartCapture = _autoStartCapture = s.AutoStartCapture;
        _launchAutoSystemProxyOnStart = _autoSystemProxyOnStart = s.AutoSystemProxyOnStart;
        _decryptHttps = s.DecryptHttps;
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
        _interception.DecryptHttps = _decryptHttps;
        _debugFileLogging = IsDebugFileLoggingEnabled(s);
        _interception.ConfigureLogging(s);
    }

    private static bool IsDebugFileLoggingEnabled(InspectorSettings s) =>
        s.LoggingEnableFile &&
        string.Equals(s.LoggingMinimumLevel, "Debug", StringComparison.OrdinalIgnoreCase);

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
        s.AutoStartCapture = AutoStartCapture;
        s.AutoSystemProxyOnStart = AutoSystemProxyOnStart;
        s.DecryptHttps = DecryptHttps;
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

    private Task CloseSessionDetailsAsync()
    {
        ShowSessionDetails = false;
        return Task.CompletedTask;
    }

    private Task OpenToolsTabAsync(int toolsTabIndex)
    {
        ShowSessionDetails = true;
        SelectedOuterPaneIndex = 1;
        SelectedToolsTabIndex = Math.Clamp(toolsTabIndex, 0, 3);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        return Task.CompletedTask;
    }

    private void UpdateWsFramesVisibility()
    {
        var show = _selected?.IsWebSocket == true;
        ShowWsFramesTab = show;
        if (!show && SelectedInspectTabIndex == 3)
        {
            SelectedInspectTabIndex = 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        }
    }

    private async Task EnableDecryptHttpsAsync()
    {
        _decryptHttpsBusy = true;
        try
        {
            if (!_interception.IsRunning)
            {
                StatusText = "Start interception before enabling Decrypt HTTPS";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
                return;
            }

            _interception.RefreshTrustState();
            if (!_interception.IsRootTrusted)
            {
                var owner = TryGetMainWindow();
                if (!await _dialogs.ConfirmInstallRootCaAsync(owner))
                {
                    StatusText = "Decrypt HTTPS cancelled — root CA not installed";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
                    return;
                }

                if (!_interception.InstallRootCertificate(machineStore: false) &&
                    !await TryElevateRootCaInstallAsync(owner))
                {
                    StatusText = "Root CA install failed - Decrypt HTTPS stays off (try Export CA or allow admin prompt)";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
                    return;
                }
            }

            SetDecryptHttpsCore(true);
            StatusText = "Decrypt HTTPS on — MITM decrypting TLS";
        }
        finally
        {
            _decryptHttpsBusy = false;
        }
    }

    private async Task<bool> TryElevateRootCaInstallAsync(Window? owner) =>
        await _dialogs.ConfirmElevateRootCaAsync(owner) &&
        _interception.InstallRootCertificateAsAdmin(machineStore: false);

    private void SetDecryptHttpsCore(bool enabled)
    {
        _decryptHttps = enabled;
        _interception.DecryptHttps = enabled;
        PersistSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
    }

    private string FormatBindDisplay()
    {
        var address = ParseBindAddress(BindAddress);
        return address.Equals(IPAddress.Any) ? "0.0.0.0" : BindAddress;
    }

    private Task ToggleDebugLoggingAsync()
    {
        var s = _settings.Current;
        var enable = !IsDebugFileLoggingEnabled(s);
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
        DebugFileLogging = enable;
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

        SelectedBody = SessionInspectors.FormatLabeledBody(
            _selected.RequestHeadersText,
            _selected.ResponseHeadersText,
            _selected.RequestBodyText,
            _selected.ResponseBodyText,
            _selected.RequestBodyBytes,
            _selected.ResponseBodyBytes);
        SelectedHex = SessionInspectors.FormatLabeledHex(
            _selected.RequestHeadersText,
            _selected.ResponseHeadersText,
            _selected.RequestBodyBytes,
            _selected.ResponseBodyBytes);

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

    private void OnSessionAddedToFilter(SessionSnapshot snapshot)
    {
        // Store already holds the row — append to the filtered grid in place.
        if (SessionSearch.Matches(snapshot, SearchQuery))
        {
            Sessions.Add(snapshot);
        }

        RefreshSessionCountText();
    }

    private void OnSessionsRemoved(IReadOnlyList<SessionSnapshot> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var ids = removed.Select(s => s.Id).ToHashSet();
        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(Sessions[i].Id))
            {
                Sessions.RemoveAt(i);
            }
        }

        _selectedSessions.RemoveAll(s => ids.Contains(s.Id));
        if (SelectedSession is not null && ids.Contains(SelectedSession.Id))
        {
            SelectedSession = null;
        }

        RefreshSessionCountText();
        if (removed.Count == 1)
        {
            StatusText = "Evicted 1 old session (retention limit)";
        }
        else
        {
            StatusText = $"Evicted {removed.Count} old sessions (retention limit)";
        }
    }

    private async Task LoadSelectedBodiesAsync(SessionSnapshot snap)
    {
        try
        {
            await _store.EnsureBodiesLoadedAsync(snap).ConfigureAwait(false);
            await MarshalToUiAsync(() =>
            {
                if (ReferenceEquals(_selected, snap))
                {
                    RefreshSelectedInspectors();
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            await MarshalToUiAsync(() =>
            {
                if (ReferenceEquals(_selected, snap))
                {
                    RefreshSelectedInspectors();
                }
            }).ConfigureAwait(false);
        }
    }

    private void RefreshSessionCountText()
    {
        var spilled = _store.SpilledCount;
        var spilledSuffix = spilled > 0 ? $" ({spilled} on disk)" : "";
        SessionCountText = string.IsNullOrWhiteSpace(SearchQuery)
            ? $"Sessions: {_all.Count}{spilledSuffix}"
            : $"Sessions: {Sessions.Count} / {_all.Count}{spilledSuffix}";
    }

    private void NotifyQuickFilterProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideTunnelsFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideImagesFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorsOnlyFilter)));
    }

    private void ApplyFilter()
    {
        var previouslySelected = SelectedSession;
        Sessions.Clear();
        foreach (var s in SessionSearch.Filter(_all, SearchQuery))
        {
            Sessions.Add(s);
        }

        // Restore single selection used by the detail pane when the row still matches the filter.
        if (previouslySelected is not null && Sessions.Contains(previouslySelected))
        {
            SelectedSession = previouslySelected;
        }
        else if (previouslySelected is not null)
        {
            SelectedSession = null;
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
        _interception.DecryptHttps = _decryptHttps;
        _interception.ConfigureLogging(_settings.Current);
        await _interception.StartAsync(address, BindPort);
        Capturing = true;
        RefreshEndpointAndBindUi();

        var wantSystemProxy = _reenableSystemProxyOnStart || AutoSystemProxyOnStart;
        _reenableSystemProxyOnStart = false;
        if (wantSystemProxy && !SystemProxy)
        {
            SystemProxy = true;
        }

        // If settings asked for decrypt but CA is gone, fall back to CONNECT (no silent re-trust).
        if (_decryptHttps && !_interception.RefreshTrustState())
        {
            SetDecryptHttpsCore(false);
            StatusText = SystemProxy
                ? $"Listening on {FormatBindDisplay()}:{BindPort}; system proxy on — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS."
                : $"Listening on {FormatBindDisplay()}:{BindPort} — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS.";
            return;
        }

        if (SystemProxy)
        {
            StatusText = _decryptHttps
                ? $"Listening on {FormatBindDisplay()}:{BindPort}; system proxy on. Decrypt HTTPS on. Chrome: --disable-quic or H3 may bypass."
                : $"Listening on {FormatBindDisplay()}:{BindPort}; system proxy on. HTTPS shows as CONNECT until Decrypt HTTPS is enabled." +
                  " Chrome/Edge: --disable-quic or HTTP/3 may bypass the proxy.";
            return;
        }

        StatusText = _decryptHttps
            ? $"Listening on {FormatBindDisplay()}:{BindPort} — Decrypt HTTPS on. Enable System proxy if needed. Chrome: --disable-quic or H3 may bypass."
            : $"Listening on {FormatBindDisplay()}:{BindPort} — HTTPS as CONNECT until Decrypt HTTPS is enabled. Enable System proxy if needed.";
    }

    private void RefreshEndpointAndBindUi()
    {
        EndpointStatusText = _interception.IsRunning
            ? $"Listening {FormatBindDisplay()}:{BindPort}"
            : "Not listening";
        InterceptToggleText = _interception.IsRunning ? "Stop interception" : "Start interception";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindFieldsEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIntercepting)));
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
        await _store.EnsureBodiesLoadedAsync(SelectedSession).ConfigureAwait(false);
        var result = await ReplayService.ReplayAsync(
            SelectedSession,
            ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            StatusText = result.Ok
                ? $"Replay → HTTP {result.StatusCode}: {Truncate(result.Message, 120)}"
                : "Replay failed: " + result.Message;
        }).ConfigureAwait(false);
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

        _store.Add(snap);
        ApplyFilter();
        RefreshSessionCountText();
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
        if (_all.Count == 0)
        {
            StatusText = "No sessions to export";
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            StatusText = "Export HAR cancelled";
            return;
        }

        try
        {
            var sessions = _all.ToList();
            await _store.EnsureBodiesLoadedAsync(sessions).ConfigureAwait(false);
            await SessionArchive.ExportHarAsync(sessions, path).ConfigureAwait(false);
            await MarshalToUiAsync(() => StatusText = $"Exported {sessions.Count} sessions to {path}");
        }
        catch (Exception ex)
        {
            await MarshalToUiAsync(() => StatusText = "Export HAR failed: " + Truncate(ex.Message, 160));
        }
    }

    private async Task ExportSelectedHarAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            StatusText = "Select a session to export";
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            StatusText = "Export HAR cancelled";
            return;
        }

        try
        {
            await _store.EnsureBodiesLoadedAsync(sessions).ConfigureAwait(false);
            await SessionArchive.ExportHarAsync(sessions, path).ConfigureAwait(false);
            await MarshalToUiAsync(() => StatusText = $"Exported {sessions.Count} sessions to {path}");
        }
        catch (Exception ex)
        {
            await MarshalToUiAsync(() => StatusText = "Export HAR failed: " + Truncate(ex.Message, 160));
        }
    }

    private async Task ImportHarAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import HAR", "HAR", "*.har", ZipFileFilter);
        if (path is null)
        {
            StatusText = "No .har or archive to import";
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
            _store.Add(snap);
        }

        ApplyFilter();
        RefreshSessionCountText();
        StatusText = $"Appended {imported.Count} sessions from {Path.GetFileName(path)}";
    }

    private async Task ExportArchiveAsync()
    {
        if (_all.Count == 0)
        {
            StatusText = "No sessions to export";
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            StatusText = "Export archive cancelled";
            return;
        }

        try
        {
            var sessions = _all.ToList();
            await _store.EnsureBodiesLoadedAsync(sessions).ConfigureAwait(false);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path).ConfigureAwait(false);
            StatusText = $"Exported {sessions.Count} sessions to {path}";
        }
        catch (Exception ex)
        {
            StatusText = "Export archive failed: " + Truncate(ex.Message, 160);
        }
    }

    private async Task ExportSelectedArchiveAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            StatusText = "Select a session to export";
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            StatusText = "Export archive cancelled";
            return;
        }

        try
        {
            await _store.EnsureBodiesLoadedAsync(sessions).ConfigureAwait(false);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path).ConfigureAwait(false);
            StatusText = $"Exported {sessions.Count} sessions to {path}";
        }
        catch (Exception ex)
        {
            StatusText = "Export archive failed: " + Truncate(ex.Message, 160);
        }
    }

    private async Task ImportArchiveAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import archive", "ZIP", ZipFileFilter);
        if (path is null)
        {
            StatusText = "No titanium-inspector archive to import";
            return;
        }

        StatusText = "Importing archive…";
        try
        {
            // Off the UI sync context for zip IO so headless WaitUntil pumps cannot deadlock the import.
            var imported = await SessionArchive.ImportNativeArchiveAsync(path).ConfigureAwait(false);
            await MarshalToUiAsync(() =>
            {
                foreach (var snap in imported)
                {
                    _store.Add(snap);
                }

                ApplyFilter();
                RefreshSessionCountText();
                StatusText = $"Appended {imported.Count} sessions from {Path.GetFileName(path)}";
            });
        }
        catch (Exception ex)
        {
            await MarshalToUiAsync(() => StatusText = "Import archive failed: " + Truncate(ex.Message, 160));
        }
    }

    private IReadOnlyList<SessionSnapshot> ResolveExportSelection()
    {
        if (_selectedSessions.Count > 0)
        {
            return _selectedSessions.ToList();
        }

        return SelectedSession is null ? Array.Empty<SessionSnapshot>() : [SelectedSession];
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

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}

internal sealed class RelayCommand(Func<Task> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        try
        {
            // Preserve Avalonia UI sync context so StatusText / collection updates after
            // awaits are applied on the UI thread (ConfigureAwait(false) caused macOS
            // headless flakes where export wrote the file but StatusText stayed "Ready").
            await execute();
        }
        catch
        {
            // UI commands must not tear down the process (async void).
        }
    }

#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
