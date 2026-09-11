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
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged
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
    private IStatusNotifier _statusNotifier;
    private readonly ObservableCollection<SessionSnapshot> _all;
    private readonly List<SessionSnapshot> _selectedSessions = new();
    private readonly RelayCommand _clearSessionsCommand;
    private readonly RelayCommand _removeSelectedSessionsCommand;
    private readonly RelayCommand _exportSelectedHarCommand;
    private readonly RelayCommand _exportSelectedArchiveCommand;
    private readonly RelayCommand _copyAsCurlCommand;
    private readonly RelayCommand _copyAsFetchCommand;
    private readonly RelayCommand _diffSessionsCommand;
    private string _sessionDiffText = "";
    private const string StatusReady = "Ready";
    private const string StartProxyFirstStatus = "Start the proxy first";
    private const string SystemProxyRestoredStatus = "System proxy restored";
    private const string TrustingRootCaWindowsStatus =
        "Trusting root CA… if Windows asks Trusted Root Yes/No, choose Yes";
    private const string TrustingRootCaStatus = "Trusting root CA…";
    private string _statusText = StatusReady;
    private StatusSeverity _statusSeverity = StatusSeverity.Neutral;
    private bool _isStatusBusy;
    private int _statusAttentionTick;
    private int _themeRefreshTick;
    private bool _settingStatus;
    private CancellationTokenSource? _statusRevertCts;
    private CancellationToken StatusCancelToken => _statusRevertCts?.Token ?? CancellationToken.None;
    private const int GuardStatusRevertMs = 3000;
    private const int OutcomeSuccessRevertMs = 5000;
    /// <summary>Match Error/Warning toast duration so status bar stays readable.</summary>
    private const int OutcomeErrorRevertMs = 15000;
    private const int OutcomeWarningRevertMs = 15000;
    private string _sessionCountText = "Sessions: 0";
    private string _exclusionSummaryText = "";
    private string _searchQuery = "";
    private bool _firefoxTrustHintShown;
    /// <summary>Sessions hard-evicted by retention this process (not user clear/remove).</summary>
    private int _retentionEvictedTotal;
    /// <summary>When &gt; 0, <see cref="OnSessionsRemoved"/> skips retention accounting/status.</summary>
    private int _userRemovalDepth;
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
    private string _autoResponderLocalFilePath = string.Empty;
    private int _autoResponderStatus = 200;
    private string _mapRemoteMatch = "*";
    private string _mapRemoteTarget = "http://127.0.0.1/";
    private string _mapRemoteGraphQlOperation = string.Empty;
    private string _autoResponderGraphQlOperation = string.Empty;
    private string _plusPanelsSummary = "";
    private string _bindAddress = "127.0.0.1";
    private int _bindPort = 8866;
    private string _endpointStatusText = "Proxy stopped";
    private string _interceptToggleText = "Start proxy";
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
    /// <summary>
    /// When true, assigning <see cref="SelectedSession"/> must not force the details pane open
    /// (filter restore / bulk removal — DataGrid may briefly re-select a neighbor row).
    /// </summary>
    private bool _suppressOpenSessionDetails;
    private bool _showWsFramesTab;
    private bool _showSseTab;
    private bool _showProtobufTab;
    private string _selectedSseEvents = "";
    private string _selectedProtobufDecoded = "";
    private string _networkThrottleProfile = "None";
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
        : this(new InspectorViewModelServices(buffer, registry, updates, settings, interception, dialogs, pathPicker))
    {
    }

    public MainWindowViewModel(InspectorViewModelServices services)
    {
        _buffer = services.Buffer;
        _registry = services.Registry;
        _store = services.Registry.Store;
        _all = _store.Sessions;
        _updates = services.Updates;
        _settings = services.Settings;
        _interception = services.Interception ?? new InterceptionService();
        _dialogs = services.Dialogs ?? new AvaloniaInspectorDialogs();
        _pathPicker = services.PathPicker ?? new AvaloniaInspectorPathPicker();
        _statusNotifier = services.StatusNotifier ?? NullStatusNotifier.Instance;
        Sessions = new ObservableCollection<SessionSnapshot>();
        Breakpoints = new BreakpointViewModel();
        AutoResponder = new AutoResponderViewModel();
        MapRemote = new MapRemoteViewModel();
        _interception.AutoResponder = AutoResponder;
        _interception.MapRemote = MapRemote;
        _interception.Breakpoints = Breakpoints;

        LoadFromSettings();

        CheckForUpdatesCommand = Cmd(async () => await CheckUpdatesAsync(promptIfAvailable: true));
        SetUpdateChannelStableCommand = Cmd(() =>
        {
            UpdateChannelIsBeta = false;
            return Task.CompletedTask;
        });
        SetUpdateChannelBetaCommand = Cmd(() =>
        {
            UpdateChannelIsBeta = true;
            return Task.CompletedTask;
        });
        SetThemeLightCommand = Cmd(() =>
        {
            SetThemeMode(ThemeMode.Light);
            return Task.CompletedTask;
        });
        SetThemeDarkCommand = Cmd(() =>
        {
            SetThemeMode(ThemeMode.Dark);
            return Task.CompletedTask;
        });
        SetThemeAutomaticCommand = Cmd(() =>
        {
            SetThemeMode(ThemeMode.Automatic);
            return Task.CompletedTask;
        });
        ToggleCheckForUpdatesOnStartupCommand = Cmd(() =>
        {
            CheckForUpdatesOnStartup = !CheckForUpdatesOnStartup;
            return Task.CompletedTask;
        });
        ExportHarCommand = Cmd(async () => await ExportHarAsync());
        _exportSelectedHarCommand = Cmd(async () => await ExportSelectedHarAsync(), () => HasSelectedSessions);
        ExportSelectedHarCommand = _exportSelectedHarCommand;
        ImportHarCommand = Cmd(async () => await ImportHarAsync());
        ExportArchiveCommand = Cmd(async () => await ExportArchiveAsync());
        _exportSelectedArchiveCommand = Cmd(async () => await ExportSelectedArchiveAsync(), () => HasSelectedSessions);
        ExportSelectedArchiveCommand = _exportSelectedArchiveCommand;
        ImportArchiveCommand = Cmd(async () => await ImportArchiveAsync());
        ExitCommand = Cmd(ExitAsync);
        StartCaptureCommand = Cmd(async () => await StartCaptureAsync());
        StopCaptureCommand = Cmd(StopCaptureAsync);
        ToggleInterceptCommand = Cmd(ToggleInterceptAsync);
        ToggleCapturingCommand = Cmd(ToggleCapturingAsync);
        ToggleAutoStartCaptureCommand = Cmd(() =>
        {
            AutoStartCapture = !AutoStartCapture;
            return Task.CompletedTask;
        });
        ToggleAutoSystemProxyOnStartCommand = Cmd(() =>
        {
            AutoSystemProxyOnStart = !AutoSystemProxyOnStart;
            return Task.CompletedTask;
        });
        ToggleDecryptHttpsCommand = Cmd(() =>
        {
            DecryptHttps = !DecryptHttps;
            return Task.CompletedTask;
        });
        ToggleIgnoreServerCertificateErrorsCommand = Cmd(() =>
        {
            IgnoreServerCertificateErrors = !IgnoreServerCertificateErrors;
            return Task.CompletedTask;
        });
        ToggleAddViaHeaderCommand = Cmd(() =>
        {
            AddViaHeader = !AddViaHeader;
            return Task.CompletedTask;
        });
        _clearSessionsCommand = Cmd(ClearSessionsAsync, () => HasSessions);
        ClearSessionsCommand = _clearSessionsCommand;
        _removeSelectedSessionsCommand = Cmd(RemoveSelectedSessionsAsync, () => HasSelectedSessions);
        RemoveSelectedSessionsCommand = _removeSelectedSessionsCommand;
        ToggleSystemProxyCommand = Cmd(ToggleSystemProxyAsync);
        InstallCaCommand = Cmd(InstallCaAsync);
        TrustFirefoxCaCommand = Cmd(TrustFirefoxCaAsync);
        UntrustCaCommand = Cmd(UntrustCaAsync);
        RotateCaCommand = Cmd(RotateCaAsync);
        ExportCaCommand = Cmd(ExportCaAsync);
        DeviceCaSetupCommand = Cmd(DeviceCaSetupAsync);
        OpenLoopbackExemptCommand = Cmd(OpenLoopbackExemptAsync);
        OpenSessionRetentionCommand = Cmd(OpenSessionRetentionAsync);
        OpenLoggingSettingsCommand = Cmd(OpenLoggingSettingsAsync);
        OpenAboutCommand = Cmd(OpenAboutAsync);
        OpenHttpsDecryptHostsCommand = Cmd(OpenExcludedHostsAsync);
        ExcludeHostCommand = Cmd(ExcludeHostAsync);
        ResetSettingsCommand = Cmd(ResetSettingsAsync);
        ReplayCommand = Cmd(async () => await ReplaySelectedAsync());
        LoadFromSelectedCommand = Cmd(LoadFromSelectedAsync);
        LoadIntoComposerCommand = Cmd(LoadIntoComposerAsync);
        CopyUrlCommand = Cmd(CopyUrlAsync);
        _copyAsCurlCommand = Cmd(CopyAsCurlAsync, () => CanCopyAsCurl);
        CopyAsCurlCommand = _copyAsCurlCommand;
        _copyAsFetchCommand = Cmd(CopyAsFetchAsync, () => CanCopyAsCurl);
        CopyAsFetchCommand = _copyAsFetchCommand;
        _diffSessionsCommand = Cmd(DiffSessionsAsync, () => CanDiffSessions);
        DiffSessionsCommand = _diffSessionsCommand;
        FilterByHostCommand = Cmd(FilterByHostAsync);
        FilterByProcessCommand = Cmd(FilterByProcessAsync);
        OpenExclusionSummaryCommand = Cmd(OpenExcludedHostsAsync);
        SendComposerCommand = Cmd(async () => await SendComposerAsync());
        AddAutoResponderRuleCommand = Cmd(AddAutoResponderRuleAsync);
        DeleteAutoResponderRuleCommand = Cmd(DeleteAutoResponderRuleAsync);
        UpdateAutoResponderRuleCommand = Cmd(UpdateAutoResponderRuleAsync);
        BrowseAutoResponderLocalFileCommand = Cmd(BrowseAutoResponderLocalFileAsync);
        AddMapRemoteRuleCommand = Cmd(AddMapRemoteRuleAsync);
        DeleteMapRemoteRuleCommand = Cmd(DeleteMapRemoteRuleAsync);
        UpdateMapRemoteRuleCommand = Cmd(UpdateMapRemoteRuleAsync);
        ContinueBreakpointCommand = Cmd(() =>
        {
            Breakpoints.Continue();
            return Task.CompletedTask;
        });
        AbortBreakpointCommand = Cmd(() =>
        {
            Breakpoints.Abort();
            return Task.CompletedTask;
        });
        ApplyEditBodyCommand = Cmd(ApplyEditBodyAsync);
        ToggleDebugLoggingCommand = Cmd(ToggleDebugLoggingAsync);
        CloseSessionDetailsCommand = Cmd(CloseSessionDetailsAsync);
        OpenToolsComposerCommand = Cmd(() => OpenToolsTabAsync(0));
        OpenToolsBreakpointsCommand = Cmd(() => OpenToolsTabAsync(1));
        OpenToolsAutoResponderCommand = Cmd(() => OpenToolsTabAsync(2));
        OpenToolsScriptsCommand = Cmd(() => OpenToolsTabAsync(3));
        OpenToolsMapRemoteCommand = Cmd(() => OpenToolsTabAsync(4));
        ClearFiltersCommand = Cmd(() =>
        {
            SearchQuery = SessionSearch.ClearFilters(SearchQuery);
            return Task.CompletedTask;
        });

        WireEventHandlers();
        LoadPlusPanels();
        _interception.ConfigureLogging(_settings.Current);
        _interception.IgnoreServerCertificateErrors = _settings.Current.IgnoreServerCertificateErrors;
        _interception.AddViaHeader = _settings.Current.AddViaHeader;
        _interception.DecryptHttps = _decryptHttps;
        ApplyExclusionSettingsFromSettings();
        ShowLoopbackExemptMenu = AppContainerLoopback.IsSupported;
        ShowProcessColumn = ClientProcessId.IsSupported;
    }

    /// <summary>Exposed for E2E / headless tests.</summary>
    public InterceptionService Interception => _interception;

    /// <summary>Exposed for E2E / headless tests.</summary>
    public IInspectorDialogs Dialogs => _dialogs;

    /// <summary>Exposed for E2E / headless tests.</summary>
    public IInspectorPathPicker PathPicker => _pathPicker;

    /// <summary>Attach window toast host after the main window template is ready.</summary>
    public void AttachStatusNotifier(IStatusNotifier notifier) =>
        _statusNotifier = notifier ?? NullStatusNotifier.Instance;

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

    /// <summary>True when the store has at least one session (Clear sessions).</summary>
    public bool HasSessions => _all.Count > 0;

    /// <summary>Semantic color / busy state for the status bar.</summary>
    public StatusSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetField(ref _statusSeverity, value);
    }

    /// <summary>True while an async menu/action is waiting for a result.</summary>
    public bool IsStatusBusy
    {
        get => _isStatusBusy;
        private set => SetField(ref _isStatusBusy, value);
    }

    /// <summary>Increments when a non-busy result should briefly pulse the status text.</summary>
    public int StatusAttentionTick
    {
        get => _statusAttentionTick;
        private set => SetField(ref _statusAttentionTick, value);
    }

    /// <summary>Increments when the active theme variant changes so status-code brushes rebind.</summary>
    public int ThemeRefreshTick
    {
        get => _themeRefreshTick;
        private set => SetField(ref _themeRefreshTick, value);
    }

    /// <summary>
    /// Update status bar text, severity, busy indicator, and optionally toast important outcomes.
    /// </summary>
    public void SetStatus(string text, StatusSeverity severity = StatusSeverity.Neutral, bool toastImportant = false)
    {
        CancelStatusRevert();
        _settingStatus = true;
        try
        {
            SetField(ref _statusText, text, nameof(StatusText));
            StatusSeverity = severity;
            IsStatusBusy = severity == StatusSeverity.Busy;
            if (severity is StatusSeverity.Success or StatusSeverity.Warning or StatusSeverity.Error)
            {
                StatusAttentionTick++;
            }

            if (toastImportant)
            {
                _statusNotifier.Show(text, severity);
            }
        }
        finally
        {
            _settingStatus = false;
        }
    }

    private void SetSteadyStatus(string text) => SetStatus(text, StatusSeverity.Neutral);

    internal void SetTransientStatus(
        string text,
        StatusSeverity severity,
        bool toastImportant = false,
        int revertMs = OutcomeSuccessRevertMs,
        StatusSeverity? toastSeverity = null)
    {
        SetStatus(text, severity);
        if (toastImportant)
        {
            _statusNotifier.Show(text, toastSeverity ?? severity);
        }

        ScheduleStatusRevert(revertMs);
    }

    private void SetGuardStatus(string text) =>
        SetTransientStatus(text, StatusSeverity.Warning, revertMs: GuardStatusRevertMs);

    private void SetOutcomeStatus(
        string text,
        StatusSeverity severity,
        bool toastImportant = false,
        StatusSeverity? toastSeverity = null)
    {
        var revertMs = severity switch
        {
            StatusSeverity.Error => OutcomeErrorRevertMs,
            StatusSeverity.Warning => OutcomeWarningRevertMs,
            _ => OutcomeSuccessRevertMs,
        };
        SetTransientStatus(text, severity, toastImportant, revertMs, toastSeverity);
    }

    private void RestoreBaselineStatus()
    {
        if (_interception.IsRunning)
        {
            SetSteadyStatus(StatusReady);
        }
        else
        {
            SetSteadyStatus("Proxy stopped");
        }
    }

    private void CancelStatusRevert()
    {
        if (_statusRevertCts is null)
        {
            return;
        }

        _statusRevertCts.Cancel();
        _statusRevertCts.Dispose();
        _statusRevertCts = null;
    }

    private void ScheduleStatusRevert(int revertMs)
    {
        CancelStatusRevert();
        if (revertMs <= 0)
        {
            RestoreBaselineStatus();
            return;
        }

        _statusRevertCts = new CancellationTokenSource();
        var token = _statusRevertCts.Token;
        _ = RevertStatusAfterDelayAsync(revertMs, token);
    }

    private async Task RevertStatusAfterDelayAsync(int revertMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(revertMs, token).ConfigureAwait(false);
            await MarshalToUiAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    RestoreBaselineStatus();
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer status message
        }
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
        if (!SystemProxy)
        {
            SetStatus(
                $"Proxy running on {FormatBindDisplay()}:{BindPort}, but system proxy failed to enable — use the System proxy checkbox.",
                StatusSeverity.Warning);
        }
        // On success the SystemProxy setter already shows restart-browser guidance — do not overwrite with Ready.
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
        CancelStatusRevert();
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
        CancelStatusRevert();
    }

    private void WireEventHandlers()
    {
        WireAutoResponderHandlers();
        WireMapRemoteHandlers();
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
                AutoResponderLocalFilePath = selected.LocalFilePath;
                AutoResponderGraphQlOperation = selected.GraphQlOperationName;
            }
        };
        AutoResponder.EnabledChanged += (_, _) => PersistAutoResponder();
        AutoResponder.Rules.CollectionChanged += (_, _) => { /* persistence via explicit commands */ };
    }

    private void WireMapRemoteHandlers()
    {
        MapRemote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapRemoteViewModel.SelectedRule) &&
                MapRemote.SelectedRule is { } selected)
            {
                MapRemoteMatch = selected.MatchUrl;
                MapRemoteTarget = selected.TargetUrl;
                MapRemoteGraphQlOperation = selected.GraphQlOperationName;
            }
        };
        MapRemote.EnabledChanged += (_, _) => PersistMapRemote();
        MapRemote.Rules.CollectionChanged += (_, _) => { /* persistence via explicit commands */ };
    }

    private void WireBreakpointHandlers()
    {
        Breakpoints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BreakpointViewModel.Enabled)
                or nameof(BreakpointViewModel.UrlFilter)
                or nameof(BreakpointViewModel.GraphQlOperationName))
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
        _interception.DecryptFailureBypassLearned += (_, entry) =>
            MarshalToUi(() =>
            {
                StatusText =
                    $"Auto-tunneled {entry.Host} (origin TLS). Manage in Excluded hosts.";
                UpdateExclusionSummary();
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

    private static async Task MarshalToUiAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        // Prefer Post over InvokeAsync so headless WaitUntil pumps (RunJobs) can drain the
        // callback without a nested InvokeAsync wait. Retry IFontManagerImpl races on macOS CI.
        const int maxAttempts = 8;
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex) when (
                attempt < maxAttempts
                && ex.Message.Contains("IFontManagerImpl", StringComparison.Ordinal))
            {
                last = ex;
                await Task.Delay(25 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last!;
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
        SetStatus("Stopping…", StatusSeverity.Busy);

        try
        {
            await Task.Run(() => _interception.Stop(), _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            await MarshalToUiAsync(() =>
            {
                SetSystemProxyCore(false);
                PersistSettings();
                RefreshEndpointAndBindUi();
                SetSteadyStatus(statusAfterStop);
            }, StatusCancelToken).ConfigureAwait(false);
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



    private Task ToggleSystemProxyAsync() => TryToggleSystemProxyAsync();

    private async Task TryToggleSystemProxyAsync()
    {
        if (SystemProxy)
        {
            SystemProxy = false;
            return;
        }

        if (!_interception.IsRunning)
        {
            SetGuardStatus("Start the proxy before enabling system proxy");
            return;
        }

        var s = _settings.Current;
        if (!s.WarnedAboutPacReplace && SystemProxyPacHelper.HasActivePacScript())
        {
            var owner = TryGetMainWindow();
            if (!await AwaitCancellableAsync(_dialogs.ConfirmPacReplaceAsync(owner)))
            {
                StatusText = "System proxy not enabled (PAC replace cancelled)";
                return;
            }

            s.WarnedAboutPacReplace = true;
            _settings.Save();
        }

        SystemProxy = true;
    }

    private Task AwaitCancellableAsync(Task task) => task.WaitAsync(StatusCancelToken);








    private Task<T> AwaitCancellableAsync<T>(Task<T> task) => task.WaitAsync(StatusCancelToken);









    private async Task OpenLoopbackExemptAsync()
    {
        if (!AppContainerLoopback.IsSupported)
        {
            StatusText = "Allowing Store apps requires Windows 8 or later";
            return;
        }

        var owner = TryGetMainWindow();
        if (owner is null)
        {
            if (AppContainerLoopback.TryProbeApis(out var msg))
            {
                StatusText = "Store app allow-list OK (no UI owner): " + msg;
            }
            else
            {
                StatusText = msg;
            }

            return;
        }

        await AwaitCancellableAsync(LoopbackExemptWindow.ShowAsync(owner));
        StatusText = "Allow Store apps dialog closed";
    }

    private async Task OpenSessionRetentionAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            StatusText = "Session retention requires the main window";
            return;
        }

        var saved = await AwaitCancellableAsync(SessionRetentionWindow.ShowAsync(owner, _settings));
        StatusText = saved
            ? "Session retention saved — restart Inspector to apply"
            : "Session retention cancelled";
    }


    private async Task OpenLoggingSettingsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            // Headless / unit tests: apply defaults path without UI.
            StatusText = "Logging settings require the main window";
            return;
        }

        var saved = await AwaitCancellableAsync(LoggingSettingsWindow.ShowAsync(
            owner,
            _settings,
            s =>
            {
                _interception.ConfigureLogging(s);
                DebugFileLogging = IsDebugFileLoggingEnabled(s);
            }));
        if (saved)
        {
            var path = _settings.Current.LoggingFilePath ?? LoggingSettingsWindow.DefaultLogPath();
            StatusText = _settings.Current.LoggingEnableFile
                ? $"Logging saved: {path}"
                : "Logging saved (file logging off)";
        }
        else
        {
            StatusText = "Logging settings cancelled";
        }
    }

    private async Task OpenExcludedHostsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            StatusText = "Excluded hosts requires the main window";
            return;
        }

        var saved = await AwaitCancellableAsync(ExcludedHostsWindow.ShowAsync(
            owner,
            _settings,
            readOnly: false,
            ApplyExclusionSettingsFromSettings,
            _interception));
        if (saved)
        {
            if (SystemProxy && !_interception.ReapplySystemProxyIfEnabled())
            {
                StatusText = "Exclusions saved; re-toggle System proxy to apply OS bypass changes";
            }
            else
            {
                StatusText = "Excluded hosts saved (applies to new connections)";
            }

            UpdateExclusionSummary();
        }
        else
        {
            StatusText = "Excluded hosts cancelled";
        }
    }

    private async Task ExcludeHostAsync()
    {
        var selected = SelectedSession;
        if (selected is null || string.IsNullOrWhiteSpace(selected.Host))
        {
            StatusText = "Select a session with a host to exclude";
            return;
        }

        var owner = TryGetMainWindow();
        if (owner is null)
        {
            StatusText = "Exclude host requires the main window";
            return;
        }

        var (saved, kind, _) = await AwaitCancellableAsync(ExcludeHostDialog.ShowAsync(owner, _settings, selected.Host));
        if (!saved)
        {
            StatusText = "Exclude host cancelled";
            return;
        }

        ApplyExclusionSettingsFromSettings();
        if (kind == ExcludeHostKind.BypassProxy && SystemProxy)
        {
            _interception.ReapplySystemProxyIfEnabled();
        }

        UpdateExclusionSummary();
        StatusText = kind == ExcludeHostKind.BypassProxy
            ? $"Added {selected.Host} to OS bypass exclusions (new connections)"
            : $"Added {selected.Host} to tunnel-only exclusions (new connections)";
    }

    private async Task ResetSettingsAsync()
    {
        var owner = TryGetMainWindow();
        if (!await AwaitCancellableAsync(_dialogs.ConfirmResetSettingsAsync(owner)))
        {
            StatusText = "Reset settings cancelled";
            return;
        }

        _settings.ResetToFactoryDefaults();
        LoadFromSettings();
        NotifySettingsUiChanged();
        StatusText =
            "Settings restored to defaults — restart Inspector so retention limits fully apply. Root CA and sessions were not changed.";
    }

    private void ApplyExclusionSettingsFromSettings()
    {
        var s = _settings.Current;
        _interception.DecryptSkipHosts = s.DecryptSkipHosts?.ToList() ?? [];
        _interception.DecryptOnlyHosts = s.DecryptOnlyHosts?.ToList() ?? [];
        _interception.SystemProxyBypassHosts = s.SystemProxyBypassHosts?.ToList() ?? [];
        _interception.ProxyLoopback = s.ProxyLoopback;
        _interception.EnableDecryptFailureBypass = s.EnableDecryptFailureBypass;
        _interception.ApplyDecryptFailureBypassSetting();
        _interception.SystemProxySettings = s;
        UpdateExclusionSummary();
    }

    private void UpdateExclusionSummary()
    {
        var learned = _interception.GetDecryptFailureBypassEntries().Count(e => e.BypassActive);
        ExclusionSummaryText = ExclusionPreview.ExclusionSummary(_settings.Current, learned);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExclusionSummaryText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasExclusionSummary)));
    }











    private SessionSnapshot? ResolveSingleCopySession()
    {
        var selection = ResolveFilterSelection();
        return selection.Count == 1 ? selection[0] : null;
    }





    public bool CanExcludeHost => CanFilterByHost;

    public string ExclusionSummaryText
    {
        get => _exclusionSummaryText;
        private set => SetField(ref _exclusionSummaryText, value);
    }

    public bool HasExclusionSummary => !string.IsNullOrEmpty(_exclusionSummaryText);

    public string SelectedOpaqueHint =>
        _selected is { IsTunnel: true } && _selected.OpaqueReason != OpaqueTunnelReason.None
            ? _selected.OpaqueReasonDisplay
            : "";

    public bool ShowSelectedOpaqueHint => !string.IsNullOrEmpty(SelectedOpaqueHint);

    /// <summary>True when selection shares one non-empty process (single or multi-select).</summary>
    public bool CanFilterByProcess =>
        ShowProcessColumn && ResolveUnanimousFilterProcess() is not null;




    /// <summary>True when exactly one non-tunnel session with a URL is selected (curl/fetch).</summary>
    public bool CanCopyAsCurl
    {
        get
        {
            var session = ResolveSingleCopySession();
            return SessionRequestCodegen.CanGenerate(session);
        }
    }


    /// <summary>Last Session Diff text (Inspect Diff tab / probe).</summary>
    public string SessionDiffText
    {
        get => _sessionDiffText;
        private set
        {
            if (SetField(ref _sessionDiffText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanShowSessionDiffTab)));
            }
        }
    }

    /// <summary>Show Inspect Diff tab after a Session Diff has been computed.</summary>
    public bool CanShowSessionDiffTab => !string.IsNullOrEmpty(SessionDiffText);
















    private Task ApplyEditBodyAsync()
    {
        Breakpoints.EditBody(BreakpointEditBody);
        StatusText = "Breakpoint body edit applied (Continue to send)";
        return Task.CompletedTask;
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }
    public BreakpointViewModel Breakpoints { get; }
    public AutoResponderViewModel AutoResponder { get; }
    public MapRemoteViewModel MapRemote { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand SetUpdateChannelStableCommand { get; }
    public ICommand SetUpdateChannelBetaCommand { get; }
    public ICommand SetThemeLightCommand { get; }
    public ICommand SetThemeDarkCommand { get; }
    public ICommand SetThemeAutomaticCommand { get; }
    public ICommand ToggleCheckForUpdatesOnStartupCommand { get; }
    public ICommand ExportHarCommand { get; }
    public ICommand ExportSelectedHarCommand { get; }
    public ICommand ImportHarCommand { get; }
    public ICommand ExportArchiveCommand { get; }
    public ICommand ExportSelectedArchiveCommand { get; }
    public ICommand ImportArchiveCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand ToggleInterceptCommand { get; }
    public ICommand ToggleCapturingCommand { get; }
    public ICommand ToggleAutoStartCaptureCommand { get; }
    public ICommand ToggleAutoSystemProxyOnStartCommand { get; }
    public ICommand ToggleDecryptHttpsCommand { get; }
    public ICommand ToggleIgnoreServerCertificateErrorsCommand { get; }
    public ICommand ToggleAddViaHeaderCommand { get; }
    public ICommand ClearSessionsCommand { get; }
    public ICommand RemoveSelectedSessionsCommand { get; }
    public ICommand ToggleSystemProxyCommand { get; }
    public ICommand InstallCaCommand { get; }
    public ICommand TrustFirefoxCaCommand { get; }
    public ICommand UntrustCaCommand { get; }
    public ICommand RotateCaCommand { get; }
    public ICommand ExportCaCommand { get; }
    public ICommand DeviceCaSetupCommand { get; }
    public ICommand OpenLoopbackExemptCommand { get; }
    public ICommand OpenSessionRetentionCommand { get; }
    public ICommand OpenLoggingSettingsCommand { get; }
    public ICommand OpenAboutCommand { get; }
    public ICommand OpenHttpsDecryptHostsCommand { get; }
    public ICommand ExcludeHostCommand { get; }
    public ICommand OpenExclusionSummaryCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand LoadFromSelectedCommand { get; }
    public ICommand LoadIntoComposerCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand CopyAsCurlCommand { get; }
    public ICommand CopyAsFetchCommand { get; }
    public ICommand DiffSessionsCommand { get; }
    public ICommand FilterByHostCommand { get; }
    public ICommand FilterByProcessCommand { get; }
    public ICommand SendComposerCommand { get; }
    public ICommand AddAutoResponderRuleCommand { get; }
    public ICommand DeleteAutoResponderRuleCommand { get; }
    public ICommand UpdateAutoResponderRuleCommand { get; }
    public ICommand BrowseAutoResponderLocalFileCommand { get; }
    public ICommand AddMapRemoteRuleCommand { get; }
    public ICommand DeleteMapRemoteRuleCommand { get; }
    public ICommand UpdateMapRemoteRuleCommand { get; }
    public ICommand ContinueBreakpointCommand { get; }
    public ICommand AbortBreakpointCommand { get; }
    public ICommand ApplyEditBodyCommand { get; }
    public ICommand ToggleDebugLoggingCommand { get; }
    public ICommand CloseSessionDetailsCommand { get; }
    public ICommand OpenToolsComposerCommand { get; }
    public ICommand OpenToolsBreakpointsCommand { get; }
    public ICommand OpenToolsAutoResponderCommand { get; }
    public ICommand OpenToolsScriptsCommand { get; }
    public ICommand OpenToolsMapRemoteCommand { get; }
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

    /// <summary>True while the proxy endpoint is Proxy running on (drives toolbar accent / live indicator).</summary>
    public bool IsIntercepting => _interception.IsRunning;

    /// <summary>Toolbar button label: Start or Stop proxy.</summary>
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

    /// <summary>Optional Map Local file path; when set, response body is read from disk.</summary>
    public string AutoResponderLocalFilePath
    {
        get => _autoResponderLocalFilePath;
        set => SetField(ref _autoResponderLocalFilePath, value);
    }

    public string MapRemoteMatch
    {
        get => _mapRemoteMatch;
        set => SetField(ref _mapRemoteMatch, value);
    }

    public string MapRemoteTarget
    {
        get => _mapRemoteTarget;
        set => SetField(ref _mapRemoteTarget, value);
    }

    public string MapRemoteGraphQlOperation
    {
        get => _mapRemoteGraphQlOperation;
        set => SetField(ref _mapRemoteGraphQlOperation, value);
    }

    public string AutoResponderGraphQlOperation
    {
        get => _autoResponderGraphQlOperation;
        set => SetField(ref _autoResponderGraphQlOperation, value);
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
                    SetGuardStatus("Start the proxy before enabling system proxy");
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                    return;
                }

                if (!_interception.SetSystemProxy(true, _settings.Current))
                {
                    var detail = _interception.LastSystemProxyError;
                    var text = string.IsNullOrWhiteSpace(detail)
                        ? "Failed to enable system proxy (permissions, cancelled admin prompt, or unsupported desktop environment)"
                        : "Failed to enable system proxy: " + Truncate(detail, 180);
                    SetOutcomeStatus(text, StatusSeverity.Error, toastImportant: true);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                    return;
                }

                SetSystemProxyCore(true);
                SetOutcomeStatus(
                    SystemProxyEnabledStatusMessage(),
                    StatusSeverity.Success,
                    toastImportant: OperatingSystem.IsWindows());
                return;
            }

            if (_interception.IsRunning && _interception.SystemProxyEnabled &&
                !_interception.SetSystemProxy(false))
            {
                var detail = _interception.LastSystemProxyError;
                var text = string.IsNullOrWhiteSpace(detail)
                    ? "Failed to restore system proxy settings"
                    : "Failed to restore system proxy: " + Truncate(detail, 180);
                SetOutcomeStatus(text, StatusSeverity.Error, toastImportant: true);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemProxy)));
                return;
            }

            SetSystemProxyCore(false);
            SetOutcomeStatus(
                SystemProxyRestoredStatus,
                StatusSeverity.Success);
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

    public bool UpdateChannelIsBeta
    {
        get => _settings.Current.UpdateChannel.Equals("Beta", StringComparison.OrdinalIgnoreCase);
        set
        {
            var next = value ? "Beta" : "Stable";
            if (string.Equals(_settings.Current.UpdateChannel, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Current.UpdateChannel = next;
            PersistSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsBeta)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsStable)));
        }
    }

    public bool UpdateChannelIsStable
    {
        get => !UpdateChannelIsBeta;
        set
        {
            if (value)
            {
                UpdateChannelIsBeta = false;
            }
        }
    }

    public bool ThemeModeIsLight => _settings.Current.ThemeMode == ThemeMode.Light;

    public bool ThemeModeIsDark => _settings.Current.ThemeMode == ThemeMode.Dark;

    public bool ThemeModeIsAutomatic => _settings.Current.ThemeMode == ThemeMode.Automatic;

    public bool CheckForUpdatesOnStartup
    {
        get => _settings.Current.CheckForUpdatesOnStartup;
        set
        {
            if (_settings.Current.CheckForUpdatesOnStartup == value)
            {
                return;
            }

            _settings.Current.CheckForUpdatesOnStartup = value;
            PersistSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckForUpdatesOnStartup)));
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
                StatusText = "Decrypt HTTPS off — HTTPS shown as encrypted tunnels (not decrypted)";
            }
        }
    }

    /// <summary>When true, accept upstream TLS certs that would otherwise fail validation.</summary>
    public bool IgnoreServerCertificateErrors
    {
        get => _interception.IgnoreServerCertificateErrors;
        set
        {
            if (_interception.IgnoreServerCertificateErrors == value)
            {
                return;
            }

            _interception.IgnoreServerCertificateErrors = value;
            PersistSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoreServerCertificateErrors)));
            StatusText = value
                ? "Ignoring insecure server certificates"
                : "Validating server certificates";
        }
    }

    /// <summary>When true, append the default Via header on intercepted traffic.</summary>
    public bool AddViaHeader
    {
        get => _interception.AddViaHeader;
        set
        {
            if (_interception.AddViaHeader == value)
            {
                return;
            }

            _interception.AddViaHeader = value;
            PersistSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddViaHeader)));
            StatusText = value
                ? $"Via header on ({ProxyServer.DefaultViaHeaderPseudonym})"
                : "Via header off";
        }
    }

    public bool ShowLoopbackExemptMenu { get; }

    /// <summary>True when this OS can resolve local client process ids for the Process column.</summary>
    public bool ShowProcessColumn { get; }

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

    public bool ShowSseTab
    {
        get => _showSseTab;
        private set => SetField(ref _showSseTab, value);
    }

    public bool ShowProtobufTab
    {
        get => _showProtobufTab;
        private set => SetField(ref _showProtobufTab, value);
    }

    public string SelectedSseEvents
    {
        get => _selectedSseEvents;
        private set => SetField(ref _selectedSseEvents, value);
    }

    public string SelectedProtobufDecoded
    {
        get => _selectedProtobufDecoded;
        private set => SetField(ref _selectedProtobufDecoded, value);
    }

    /// <summary>Network throttle profile name applied to capture (None / Slow 3G / Fast 3G / LTE).</summary>
    public string NetworkThrottleProfile
    {
        get => _networkThrottleProfile;
        set
        {
            if (!SetField(ref _networkThrottleProfile, value ?? "None"))
            {
                return;
            }

            _interception.ThrottleProfile = NetworkThrottle.Find(_networkThrottleProfile) is { IsEnabled: true } p
                ? p
                : null;
            _settings.Current.NetworkThrottleProfile = _networkThrottleProfile;
            _settings.Save();
        }
    }

    public IReadOnlyList<string> NetworkThrottleProfileNames { get; } =
        NetworkThrottle.Profiles.Select(p => p.Name).ToArray();

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOpaqueHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSelectedOpaqueHint)));
            NotifyFilterSelectionProperties();

            if (value is not null && !_suppressOpenSessionDetails)
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

    /// <summary>Inspect tabs: 0 Headers, 1 Body, 2 Hex, 3 Diff, 4 WS Frames.</summary>
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

    /// <summary>Tools tabs: 0 Composer, 1 Breakpoints, 2 AutoResponder, 3 Scripts, 4 Map Remote.</summary>
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
    /// Compatibility index for tests: 0–3 Inspect, 4–8 Tools (Composer…Map Remote).
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
                SelectedInspectTabIndex = Math.Clamp(value, 0, 6);
            }
            else
            {
                SelectedOuterPaneIndex = 1;
                SelectedToolsTabIndex = Math.Clamp(value - 4, 0, 4);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_settingStatus)
            {
                SetField(ref _statusText, value);
                return;
            }

            // Direct assignments (toggles / guards) stay Neutral and clear busy.
            SetStatus(value, StatusSeverity.Neutral);
        }
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
        _breakpointOnResponse = s.BreakpointOnResponse;
        _scriptOnRequest = s.ScriptOnRequest;
        _scriptOnResponse = s.ScriptOnResponse;
        // Apply interception flags before AutoResponder/Breakpoints mutations — those can PersistSettings.
        _interception.BreakpointOnResponse = _breakpointOnResponse;
        _interception.ScriptOnRequest = _scriptOnRequest;
        _interception.ScriptOnResponse = _scriptOnResponse;
        _interception.IgnoreServerCertificateErrors = s.IgnoreServerCertificateErrors;
        _interception.AddViaHeader = s.AddViaHeader;
        _interception.DecryptHttps = _decryptHttps;
        _interception.ProtobufDescriptorSetPath = s.ProtobufDescriptorSetPath;
        _networkThrottleProfile = string.IsNullOrWhiteSpace(s.NetworkThrottleProfile) ? "None" : s.NetworkThrottleProfile;
        _interception.ThrottleProfile = NetworkThrottle.Find(_networkThrottleProfile) is { IsEnabled: true } tp
            ? tp
            : null;
        ApplyExclusionSettingsFromSettings();
        _debugFileLogging = IsDebugFileLoggingEnabled(s);
        _interception.ConfigureLogging(s);

        AutoResponder.Enabled = s.AutoResponderEnabled;
        AutoResponder.LoadFromDtos(s.AutoResponderRules);
        MapRemote.Enabled = s.MapRemoteEnabled;
        MapRemote.LoadFromDtos(s.MapRemoteRules);
        Breakpoints.Enabled = s.BreakpointEnabled;
        Breakpoints.UrlFilter = string.IsNullOrEmpty(s.BreakpointUrlFilter) ? "*" : s.BreakpointUrlFilter;
        Breakpoints.GraphQlOperationName = s.BreakpointGraphQlOperationName ?? "";
    }

    private void NotifySettingsUiChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindAddress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoStartCapture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoSystemProxyOnStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoreServerCertificateErrors)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddViaHeader)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BreakpointOnResponse)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptOnRequest)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptOnResponse)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugFileLogging)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsBeta)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsStable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckForUpdatesOnStartup)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsLight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsDark)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsAutomatic)));
        ThemeService.ApplyThemeMode(_settings.Current.ThemeMode);
    }

    private void SetThemeMode(ThemeMode mode)
    {
        if (_settings.Current.ThemeMode == mode)
        {
            return;
        }

        _settings.Current.ThemeMode = mode;
        _settings.Save();
        ThemeService.ApplyThemeMode(mode);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsLight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsDark)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeModeIsAutomatic)));
    }

    /// <summary>Rebind theme-aware brushes after <see cref="Application.ActualThemeVariant"/> changes.</summary>
    public void NotifyThemeVariantChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusSeverity)));
        ThemeRefreshTick++;
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

    private void PersistMapRemote()
    {
        _settings.Current.MapRemoteEnabled = MapRemote.Enabled;
        _settings.Current.MapRemoteRules = MapRemote.ToDtos();
        _settings.Save();
        MapRemote.NotifyRulesChanged();
    }

    private void PersistSettings()
    {
        var s = _settings.Current;
        s.BindAddress = BindAddress;
        s.BindPort = BindPort;
        s.AutoStartCapture = AutoStartCapture;
        s.AutoSystemProxyOnStart = AutoSystemProxyOnStart;
        s.DecryptHttps = DecryptHttps;
        s.IgnoreServerCertificateErrors = _interception.IgnoreServerCertificateErrors;
        s.AddViaHeader = _interception.AddViaHeader;
        s.AutoResponderEnabled = AutoResponder.Enabled;
        s.AutoResponderRules = AutoResponder.ToDtos();
        s.MapRemoteEnabled = MapRemote.Enabled;
        s.MapRemoteRules = MapRemote.ToDtos();
        s.BreakpointEnabled = Breakpoints.Enabled;
        s.BreakpointUrlFilter = Breakpoints.UrlFilter;
        s.BreakpointGraphQlOperationName = string.IsNullOrWhiteSpace(Breakpoints.GraphQlOperationName)
            ? null
            : Breakpoints.GraphQlOperationName;
        s.BreakpointOnResponse = BreakpointOnResponse;
        s.ScriptOnRequest = ScriptOnRequest;
        s.ScriptOnResponse = ScriptOnResponse;
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
        SelectedToolsTabIndex = Math.Clamp(toolsTabIndex, 0, 4);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        return Task.CompletedTask;
    }

    private void UpdateWsFramesVisibility()
    {
        ShowWsFramesTab = _selected?.IsWebSocket == true;
        ShowSseTab = _selected?.IsServerSentEvents == true ||
                     (_selected?.SseEvents?.Count > 0);
        ShowProtobufTab = _selected?.IsGrpc == true ||
                          _selected?.IsTranscoded == true ||
                          !string.IsNullOrEmpty(_selected?.ProtobufDecodedText);
        // Inspect tabs: 0 Headers, 1 Body, 2 Hex, 3 Diff, 4 WS, 5 SSE, 6 Protobuf
        if ((!ShowWsFramesTab && SelectedInspectTabIndex == 4) ||
            (!ShowSseTab && SelectedInspectTabIndex == 5) ||
            (!ShowProtobufTab && SelectedInspectTabIndex == 6))
        {
            SelectedInspectTabIndex = 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTabIndex)));
        }
    }








    private string FormatBindDisplay()
    {
        var address = ParseBindAddress(BindAddress);
        return address.Equals(IPAddress.Any) ? "0.0.0.0" : BindAddress;
    }

    private Task ToggleDebugLoggingAsync()
    {
        // Kept for tests that invoke ToggleDebugLoggingCommand; opens Logging… when UI is available,
        // otherwise toggles the previous Debug-file latch in settings.
        if (TryGetMainWindow() is not null)
        {
            return OpenLoggingSettingsAsync();
        }

        var s = _settings.Current;
        var enable = !IsDebugFileLoggingEnabled(s);
        s.LoggingEnabled = true;
        s.LoggingEnableFile = enable;
        s.LoggingMinimumLevel = enable ? "Debug" : "Error";
        if (string.IsNullOrWhiteSpace(s.LoggingFilePath))
        {
            s.LoggingFilePath = LoggingSettingsWindow.DefaultLogPath();
        }

        _interception.ConfigureLogging(s);
        _settings.Save();
        DebugFileLogging = enable;
        StatusText = enable
            ? $"Debug file logging on: {s.LoggingFilePath}"
            : "Debug file logging off (Error level, file logging off)";
        return Task.CompletedTask;
    }

    private void RefreshSelectedInspectors()
    {
        if (_selected is null)
        {
            SelectedHeaders = SelectedBody = SelectedHex = SelectedFrames = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOpaqueHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSelectedOpaqueHint)));
            return;
        }

        SelectedHeaders = BuildSelectedHeadersText(_selected);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOpaqueHint)));
        SelectedBody = BuildSelectedBodyText(_selected);
        SelectedHex = SessionInspectors.FormatLabeledHex(
            _selected.RequestHeadersText,
            _selected.ResponseHeadersText,
            _selected.RequestBodyBytes,
            _selected.ResponseBodyBytes);
        SelectedFrames = BuildSelectedFramesText(_selected);
        SelectedSseEvents = BuildSelectedSseText(_selected);
        SelectedProtobufDecoded = BuildSelectedProtobufText(_selected);
    }

    private static string BuildSelectedHeadersText(SessionSnapshot selected)
    {
        var sb = new StringBuilder();
        if (selected.IsTunnel && selected.OpaqueReason != OpaqueTunnelReason.None)
        {
            sb.AppendLine(selected.OpaqueReasonDisplay);
            sb.AppendLine();
        }

        if (selected.IsTranscoded)
        {
            sb.AppendLine("=== gRPC-JSON transcoded ===");
            sb.Append("Client: ").Append(selected.ClientMethod ?? selected.Method)
                .Append(' ').AppendLine(selected.ClientPathAndQuery ?? selected.Url);
            if (!string.IsNullOrEmpty(selected.ClientContentType))
                sb.Append("Client Content-Type: ").AppendLine(selected.ClientContentType);
            sb.Append("Upstream: ").Append(selected.UpstreamMethod ?? "POST")
                .Append(' ').AppendLine(selected.UpstreamPath ?? "");
            if (!string.IsNullOrEmpty(selected.UpstreamContentType))
                sb.Append("Upstream Content-Type: ").AppendLine(selected.UpstreamContentType);
            sb.AppendLine();
        }

        sb.AppendLine("=== Request ===");
        sb.AppendLine(selected.RequestHeadersText);
        if (!string.IsNullOrEmpty(selected.ResponseHeadersText))
        {
            sb.AppendLine("=== Response ===");
            sb.AppendLine(selected.ResponseHeadersText);
        }

        AppendNameValues(sb, "=== Cookies ===",
            SessionInspectors.ParseCookies(SessionInspectors.ParseHeaderBlock(selected.RequestHeadersText)));
        AppendNameValues(sb, "=== Query ===", SessionInspectors.ParseQuery(selected.Url));
        return sb.ToString();
    }

    private static void AppendNameValues(
        StringBuilder sb, string heading, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
            return;
        sb.AppendLine(heading);
        foreach (var pair in values)
            sb.Append(pair.Key).Append('=').AppendLine(pair.Value);
    }

    private static string BuildSelectedBodyText(SessionSnapshot selected)
    {
        var body = SessionInspectors.FormatLabeledBody(
            selected.RequestHeadersText,
            selected.ResponseHeadersText,
            selected.RequestBodyText,
            selected.ResponseBodyText,
            selected.RequestBodyBytes,
            selected.ResponseBodyBytes);
        if (!selected.IsTranscoded)
            return body;

        var prefix = new StringBuilder();
        prefix.AppendLine("=== Client (JSON/REST) ===");
        prefix.AppendLine(selected.RequestBodyText ?? "(empty)");
        prefix.AppendLine();
        prefix.AppendLine("=== Client response (JSON) ===");
        prefix.AppendLine(selected.ResponseBodyText ?? "(empty)");
        if (selected.UpstreamRequestBodyBytes is { Length: > 0 } ||
            selected.UpstreamResponseBodyBytes is { Length: > 0 })
        {
            prefix.AppendLine();
            prefix.AppendLine("=== Upstream gRPC frames (see Hex / frame preview) ===");
            if (selected.GrpcFrames is { Count: > 0 } gf)
            {
                foreach (var f in gf)
                    prefix.Append("frame compressed=").Append(f.Compressed)
                        .Append(" len=").Append(f.Length)
                        .Append(" preview=").AppendLine(f.HexPreview);
            }
        }

        prefix.AppendLine();
        prefix.Append(body);
        return prefix.ToString();
    }

    private static string BuildSelectedFramesText(SessionSnapshot selected)
    {
        if (selected.WebSocketFrames is not { Count: > 0 } frames)
            return selected.IsWebSocket ? "(no frames parsed)" : "";

        var fb = new StringBuilder();
        foreach (var f in frames)
        {
            fb.Append('[').Append(f.Direction).Append(' ').Append(f.Opcode).Append("] ")
                .AppendLine(f.PayloadPreview);
        }

        return fb.ToString();
    }

    private static string BuildSelectedSseText(SessionSnapshot selected)
    {
        if (selected.SseEvents is not { Count: > 0 } sse)
            return selected.IsServerSentEvents ? "(no events parsed)" : "";

        var sseSb = new StringBuilder();
        foreach (var ev in sse)
        {
            sseSb.Append("event=").Append(ev.Event);
            if (!string.IsNullOrEmpty(ev.Id))
                sseSb.Append(" id=").Append(ev.Id);
            sseSb.AppendLine();
            sseSb.AppendLine(ev.Data);
            sseSb.AppendLine("---");
        }

        return sseSb.ToString();
    }

    private static string BuildSelectedProtobufText(SessionSnapshot selected)
    {
        if (!string.IsNullOrEmpty(selected.ProtobufDecodedText))
            return selected.ProtobufDecodedText;
        if (selected.IsGrpc || selected.IsTranscoded)
        {
            return ProtobufMessageDecoder.DecodeWireFormat(
                selected.UpstreamResponseBodyBytes ?? selected.UpstreamRequestBodyBytes ?? selected.ResponseBodyBytes);
        }

        return "";
    }








    private Task ExitAsync()
    {
        // Close the main window so OnClosing runs BeginBackgroundShutdown (system proxy restore).
        var window = TryGetMainWindow();
        if (window is not null)
        {
            window.Close();
            return Task.CompletedTask;
        }

        BeginBackgroundShutdown();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }

        return Task.CompletedTask;
    }

    private async Task StartCaptureAsync()
    {
        var address = ParseBindAddress(BindAddress);
        PersistSettings();
        _interception.BreakpointOnResponse = BreakpointOnResponse;
        _interception.ScriptOnRequest = ScriptOnRequest;
        _interception.ScriptOnResponse = ScriptOnResponse;
        _interception.IgnoreServerCertificateErrors = _settings.Current.IgnoreServerCertificateErrors;
        _interception.AddViaHeader = _settings.Current.AddViaHeader;
        _interception.DecryptHttps = _decryptHttps;
        _interception.ConfigureLogging(_settings.Current);
        SetStatus("Starting proxy…", StatusSeverity.Busy);
        await _interception.StartAsync(address, BindPort, _statusRevertCts?.Token ?? CancellationToken.None);
        if (_interception.BoundPort > 0)
        {
            BindPort = _interception.BoundPort;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindPort)));
        }

        Capturing = true;
        RefreshEndpointAndBindUi();

        var wantSystemProxy = _reenableSystemProxyOnStart || AutoSystemProxyOnStart;
        _reenableSystemProxyOnStart = false;
        var showedSystemProxyGuidance = false;
        if (wantSystemProxy && !SystemProxy)
        {
            SystemProxy = true;
            showedSystemProxyGuidance = SystemProxy;
        }

        // If settings asked for decrypt but CA is gone, fall back to CONNECT (no silent re-trust).
        if (_decryptHttps && !_interception.RefreshTrustState())
        {
            SetDecryptHttpsCore(false);
            SetStatus(
                SystemProxy
                    ? $"Proxy running on {FormatBindDisplay()}:{BindPort}; system proxy on — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS."
                    : $"Proxy running on {FormatBindDisplay()}:{BindPort} — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS.",
                StatusSeverity.Warning);
            return;
        }

        // Keep the system-proxy restart guidance visible; do not replace it with Ready.
        if (!showedSystemProxyGuidance)
        {
            SetSteadyStatus(StatusReady);
        }
    }

    private void RefreshEndpointAndBindUi()
    {
        EndpointStatusText = _interception.IsRunning
            ? $"Proxy running on {FormatBindDisplay()}:{BindPort}"
            : "Proxy stopped";
        InterceptToggleText = _interception.IsRunning ? "Stop proxy" : "Start proxy";
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








    private static string DescribePanel(object panel)
    {
        var type = panel.GetType();
        var title = type.GetProperty("Title")?.GetValue(panel)?.ToString();
        var desc = type.GetProperty("Description")?.GetValue(panel)?.ToString();
        return string.IsNullOrEmpty(title) ? type.Name : $"{title}: {desc}";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private RelayCommand Cmd(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, canExecute, ReportActionFailure);

    internal void ReportActionFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        SetOutcomeStatus(
            "Action failed: " + Truncate(ex.Message, 160),
            StatusSeverity.Error,
            toastImportant: true);
    }

    private static string SystemProxyEnabledStatusMessage()
    {
        if (OperatingSystem.IsWindows())
            return "System proxy enabled. For Chrome: disable QUIC (--disable-quic) or H3 may bypass the proxy.";

        if (OperatingSystem.IsMacOS())
            return "System proxy enabled. Restart Firefox if it was already open so it picks up the proxy.";

        return "System proxy enabled";
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

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}


internal sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter)
    {
        try
        {
            // Preserve Avalonia UI sync context so StatusText / collection updates after
            // awaits are applied on the UI thread (ConfigureAwait(false) caused macOS
            // headless flakes where export wrote the file but StatusText stayed "Ready").
            await execute();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return;
            }

            // UI commands must not tear down the process (async void).
            onError?.Invoke(ex);
        }
    }

    public event EventHandler? CanExecuteChanged;
}
