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
using Titanium.Web.Proxy.Network;

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
    private IStatusNotifier _statusNotifier;
    private readonly ObservableCollection<SessionSnapshot> _all;
    private readonly List<SessionSnapshot> _selectedSessions = new();
    private readonly RelayCommand _clearSessionsCommand;
    private readonly RelayCommand _removeSelectedSessionsCommand;
    private readonly RelayCommand _exportSelectedHarCommand;
    private readonly RelayCommand _exportSelectedArchiveCommand;
    private string _statusText = "Ready";
    private StatusSeverity _statusSeverity = StatusSeverity.Neutral;
    private bool _isStatusBusy;
    private int _statusAttentionTick;
    private bool _settingStatus;
    private string _sessionCountText = "Sessions: 0";
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
    private int _autoResponderStatus = 200;
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
        IInspectorPathPicker? pathPicker = null,
        IStatusNotifier? statusNotifier = null)
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
        _statusNotifier = statusNotifier ?? NullStatusNotifier.Instance;
        Sessions = new ObservableCollection<SessionSnapshot>();
        Breakpoints = new BreakpointViewModel();
        AutoResponder = new AutoResponderViewModel();
        _interception.AutoResponder = AutoResponder;
        _interception.Breakpoints = Breakpoints;

        LoadFromSettings();

        CheckForUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync(promptIfAvailable: true));
        SetUpdateChannelStableCommand = new RelayCommand(() =>
        {
            UpdateChannelIsBeta = false;
            return Task.CompletedTask;
        });
        SetUpdateChannelBetaCommand = new RelayCommand(() =>
        {
            UpdateChannelIsBeta = true;
            return Task.CompletedTask;
        });
        ToggleCheckForUpdatesOnStartupCommand = new RelayCommand(() =>
        {
            CheckForUpdatesOnStartup = !CheckForUpdatesOnStartup;
            return Task.CompletedTask;
        });
        ExportHarCommand = new RelayCommand(async () => await ExportHarAsync());
        _exportSelectedHarCommand = new RelayCommand(async () => await ExportSelectedHarAsync(), () => HasSelectedSessions);
        ExportSelectedHarCommand = _exportSelectedHarCommand;
        ImportHarCommand = new RelayCommand(async () => await ImportHarAsync());
        ExportArchiveCommand = new RelayCommand(async () => await ExportArchiveAsync());
        _exportSelectedArchiveCommand = new RelayCommand(async () => await ExportSelectedArchiveAsync(), () => HasSelectedSessions);
        ExportSelectedArchiveCommand = _exportSelectedArchiveCommand;
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
        ToggleIgnoreServerCertificateErrorsCommand = new RelayCommand(() =>
        {
            IgnoreServerCertificateErrors = !IgnoreServerCertificateErrors;
            return Task.CompletedTask;
        });
        _clearSessionsCommand = new RelayCommand(ClearSessionsAsync, () => HasSessions);
        ClearSessionsCommand = _clearSessionsCommand;
        _removeSelectedSessionsCommand = new RelayCommand(RemoveSelectedSessionsAsync, () => HasSelectedSessions);
        RemoveSelectedSessionsCommand = _removeSelectedSessionsCommand;
        ToggleSystemProxyCommand = new RelayCommand(ToggleSystemProxyAsync);
        InstallCaCommand = new RelayCommand(InstallCaAsync);
        TrustFirefoxCaCommand = new RelayCommand(TrustFirefoxCaAsync);
        UntrustCaCommand = new RelayCommand(UntrustCaAsync);
        RotateCaCommand = new RelayCommand(RotateCaAsync);
        ExportCaCommand = new RelayCommand(ExportCaAsync);
        DeviceCaSetupCommand = new RelayCommand(DeviceCaSetupAsync);
        OpenLoopbackExemptCommand = new RelayCommand(OpenLoopbackExemptAsync);
        OpenSessionRetentionCommand = new RelayCommand(OpenSessionRetentionAsync);
        OpenLoggingSettingsCommand = new RelayCommand(OpenLoggingSettingsAsync);
        OpenAboutCommand = new RelayCommand(OpenAboutAsync);
        OpenHttpsDecryptHostsCommand = new RelayCommand(OpenHttpsDecryptHostsAsync);
        ResetSettingsCommand = new RelayCommand(ResetSettingsAsync);
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
        ApplyDecryptHostListsFromSettings();
        ShowLoopbackExemptMenu = AppContainerLoopback.IsSupported;
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

    /// <summary>
    /// Update status bar text, severity, busy indicator, and optionally toast important outcomes.
    /// </summary>
    public void SetStatus(string text, StatusSeverity severity = StatusSeverity.Neutral, bool toastImportant = false)
    {
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
                $"Proxy running on {FormatBindDisplay()}:{BindPort}; system proxy on. HTTPS shown as encrypted tunnels until Decrypt HTTPS is enabled." +
                " Chrome/Edge: --disable-quic or HTTP/3 may bypass the proxy.";
        }
        else
        {
            StatusText =
                $"Proxy running on {FormatBindDisplay()}:{BindPort}, but system proxy failed to enable — use the System proxy checkbox.";
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

    private static async Task MarshalToUiAsync(Action action)
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
                await tcs.Task.ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex) when (
                attempt < maxAttempts
                && ex.Message.Contains("IFontManagerImpl", StringComparison.Ordinal))
            {
                last = ex;
                await Task.Delay(25 * attempt).ConfigureAwait(false);
            }
        }

        if (last is not null)
        {
            throw last;
        }
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
            await Task.Run(() => _interception.Stop()).ConfigureAwait(false);

            await MarshalToUiAsync(() =>
            {
                SetSystemProxyCore(false);
                PersistSettings();
                RefreshEndpointAndBindUi();
                SetStatus(statusAfterStop, StatusSeverity.Success);
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
        _userRemovalDepth++;
        try
        {
            _store.Clear();
        }
        finally
        {
            _userRemovalDepth--;
        }

        Sessions.Clear();
        _selectedSessions.Clear();
        SelectedSession = null;
        _retentionEvictedTotal = 0;
        _interception.ResetSessionIdSequence();
        RefreshSessionCountText();
        NotifyFilterSelectionProperties();
        SetStatus("Sessions cleared", StatusSeverity.Success, toastImportant: true);
        return Task.CompletedTask;
    }

    private Task RemoveSelectedSessionsAsync()
    {
        var selected = ResolveExportSelection();
        if (selected.Count == 0)
        {
            SetStatus("Select one or more sessions to remove", StatusSeverity.Warning);
            return Task.CompletedTask;
        }

        var ids = selected.Select(s => s.Id).ToHashSet();
        _userRemovalDepth++;
        try
        {
            _store.Remove(ids);
        }
        finally
        {
            _userRemovalDepth--;
        }

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
        NotifyFilterSelectionProperties();
        SetStatus(
            selected.Count == 1 ? "Removed 1 session" : $"Removed {selected.Count} sessions",
            StatusSeverity.Success,
            toastImportant: true);
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
            SetStatus("Start the proxy first", StatusSeverity.Warning);
            return;
        }

        SetStatus("Trusting root CA…", StatusSeverity.Busy);
        var ok = await EnsureRootCaTrustedAsync(promptIfNeeded: true);
        if (ok)
            SetOsTrustSuccessStatus();
        else if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled ||
                 string.IsNullOrEmpty(_interception.LastOsTrustResult?.Message))
            SetStatus("Root CA install cancelled", StatusSeverity.Warning);
        else
            SetStatus(
                FormatOsTrustFailureStatus(_interception.LastOsTrustResult),
                StatusSeverity.Error,
                toastImportant: true);
    }

    private async Task TrustFirefoxCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetStatus("Start the proxy first", StatusSeverity.Warning);
            return;
        }

        var owner = TryGetMainWindow();
        if (!_interception.IsRootTrusted)
        {
            if (!await _dialogs.ConfirmInstallRootCaBeforeFirefoxAsync(owner))
            {
                SetStatus("Trust CA in Firefox cancelled — install root CA first", StatusSeverity.Warning);
                return;
            }

            SetStatus("Trusting root CA…", StatusSeverity.Busy);
            if (!await EnsureRootCaTrustedAsync(promptIfNeeded: true))
            {
                SetStatus(
                    FormatOsTrustFailureStatus(_interception.LastOsTrustResult),
                    StatusSeverity.Error,
                    toastImportant: true);
                return;
            }
        }

        if (!FirefoxCertificateTrust.IsFirefoxProfilePresent())
        {
            SetStatus(
                "Firefox profile not found — install Firefox or use Export CA and import under Firefox → Authorities",
                StatusSeverity.Warning,
                toastImportant: true);
            return;
        }

        SetStatus("Updating Firefox trust…", StatusSeverity.Busy);
        var result = await TrustFirefoxWithRecoveryAsync(owner);
        SetStatus(
            result.Succeeded
                ? result.Message
                : result.Message + (result.Kind is CertificateOsTrustKind.CertutilMissing
                    or CertificateOsTrustKind.HomebrewMissing
                    ? " — try Export CA"
                    : ""),
            result.Succeeded ? StatusSeverity.Success : StatusSeverity.Error,
            toastImportant: true);
    }

    private async Task<CertificateOsTrustResult> TrustFirefoxWithRecoveryAsync(Window? owner)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = _interception.TrustFirefox();
            if (result.Succeeded)
                return result;

            if (result.Kind is CertificateOsTrustKind.CertutilMissing or CertificateOsTrustKind.HomebrewMissing)
            {
                var choice = await _dialogs.ShowTrustRecoveryAsync(owner, result);
                if (choice == TrustRecoveryChoice.Primary &&
                    result.Kind == CertificateOsTrustKind.CertutilMissing &&
                    (OperatingSystem.IsLinux() || result.BrewAvailable))
                {
                    SetStatus("Installing browser certificate tools…", StatusSeverity.Busy);
                    var install = _interception.InstallNssToolsAndRetryTrust();
                    if (!install.Succeeded && install.Kind != CertificateOsTrustKind.Succeeded)
                    {
                        // InstallNssTools also retries OS NSS; for Firefox we only needed certutil.
                        // Continue to retry TrustFirefox if certutil appeared.
                    }

                    continue;
                }

                if (choice == TrustRecoveryChoice.Secondary ||
                    (choice == TrustRecoveryChoice.Primary && result.Kind == CertificateOsTrustKind.HomebrewMissing))
                {
                    await ExportCaAsync();
                }

                return result;
            }

            if (result.Message.Contains("Quit Firefox", StringComparison.OrdinalIgnoreCase) ||
                result.Message.Contains("running", StringComparison.OrdinalIgnoreCase))
            {
                if (!await _dialogs.ConfirmQuitFirefoxForTrustAsync(owner))
                    return CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "Firefox trust cancelled");
                continue;
            }

            return result;
        }

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.Failed, "Firefox trust failed after retries");
    }

    /// <summary>
    /// Attempts user OS trust and adaptive recovery (certutil install / Keychain / elevate).
    /// </summary>
    private async Task<bool> EnsureRootCaTrustedAsync(bool promptIfNeeded)
    {
        var owner = TryGetMainWindow();
        var ok = _interception.InstallRootCertificate(machineStore: false);
        var result = _interception.LastOsTrustResult;

        if (ok && result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            return true;

        if (!promptIfNeeded)
            return ok;

        // Adaptive recovery loop (certutil / Keychain / elevate).
        for (var i = 0; i < 4; i++)
        {
            result = _interception.LastOsTrustResult;
            if (_interception.IsRootTrusted &&
                result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return true;

            if (result?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm ||
                (!ok && result != null) ||
                !ok)
            {
                var choice = await _dialogs.ShowTrustRecoveryAsync(owner, result);
                if (choice == TrustRecoveryChoice.Cancel)
                {
                    _interception.SetLastOsTrustCancelled();
                    return false;
                }

                if (result?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                {
                    if (choice == TrustRecoveryChoice.Primary)
                    {
                        _interception.OpenMacKeychainGuidance();
                        SetStatus(
                            "Set Always Trust in Keychain Access, then click I've confirmed in the next prompt",
                            StatusSeverity.Neutral);
                        // Re-show same recovery so user can Continue.
                        continue;
                    }

                    // Secondary = I've confirmed — Continue
                    SetStatus("Verifying Keychain trust…", StatusSeverity.Busy);
                    if (_interception.VerifyOsUserSslTrust())
                        return true;

                    SetStatus(
                        "Keychain trust not verified yet — set Always Trust, then continue",
                        StatusSeverity.Warning);
                    continue;
                }

                if (result?.Kind == CertificateOsTrustKind.CertutilMissing &&
                    (result.BrewAvailable || OperatingSystem.IsLinux()))
                {
                    if (choice == TrustRecoveryChoice.Primary)
                    {
                        SetStatus("Installing browser certificate tools…", StatusSeverity.Busy);
                        var install = _interception.InstallNssToolsAndRetryTrust();
                        if (install.Succeeded ||
                            install.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                        {
                            ok = install.Succeeded ||
                                 install.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm;
                            if (install.Succeeded)
                                return true;
                            continue;
                        }

                        SetStatus(install.Message, StatusSeverity.Error);
                        continue;
                    }

                    if (choice == TrustRecoveryChoice.Secondary)
                        await ExportCaAsync();
                    return false;
                }

                if (result?.Kind == CertificateOsTrustKind.HomebrewMissing)
                {
                    if (choice == TrustRecoveryChoice.Primary)
                        await ExportCaAsync();
                    return false;
                }

                // Default: elevate or export
                if (choice == TrustRecoveryChoice.Primary)
                {
                    SetStatus("Trusting root CA (administrator)…", StatusSeverity.Busy);
                    ok = _interception.InstallRootCertificateAsAdmin(machineStore: false);
                    if (ok && _interception.LastOsTrustResult?.Kind !=
                        CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                        return true;
                    continue;
                }

                if (choice == TrustRecoveryChoice.Secondary)
                    await ExportCaAsync();
                return false;
            }

            break;
        }

        return _interception.IsRootTrusted;
    }

    private void SetOsTrustSuccessStatus()
    {
        var msg = "Root CA trusted — ready to decrypt HTTPS";
        if (!_firefoxTrustHintShown && _interception.IsFirefoxProfilePresent)
        {
            _firefoxTrustHintShown = true;
            msg += " · Using Firefox? Capture → Trust CA in Firefox…";
        }

        SetStatus(msg, StatusSeverity.Success, toastImportant: true);
    }

    private static string FormatOsTrustFailureStatus(CertificateOsTrustResult? result)
    {
        if (result is null)
            return "Root CA install failed — try Export CA, or allow the admin prompt";

        return result.Kind switch
        {
            CertificateOsTrustKind.CertutilMissing =>
                result.Message + " — Capture → Install root CA to install tools, or Export CA",
            CertificateOsTrustKind.MacNeedsManualTrustConfirm =>
                "Root CA needs Always Trust in Keychain Access",
            CertificateOsTrustKind.HomebrewMissing =>
                result.Message,
            _ => string.IsNullOrWhiteSpace(result.Message)
                ? "Root CA install failed — try Export CA, or allow the admin prompt"
                : result.Message,
        };
    }


    private async Task UntrustCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetStatus("Start the proxy first", StatusSeverity.Warning);
            return;
        }

        var owner = TryGetMainWindow();
        if (!await _dialogs.ConfirmRemoveRootCaAsync(owner))
        {
            SetStatus("Remove root CA cancelled", StatusSeverity.Neutral);
            return;
        }

        _interception.UntrustRootCertificate(machineStore: false);
        if (DecryptHttps)
        {
            SetDecryptHttpsCore(false);
        }

        var stillPresent = _interception.IsRootTrusted;
        SetStatus(
            stillPresent
                ? "Remove requested but CA still present in store"
                : "Root CA removed from current user store; Decrypt HTTPS is off until you install the CA again",
            stillPresent ? StatusSeverity.Warning : StatusSeverity.Success,
            toastImportant: true);
    }

    private async Task RotateCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetStatus("Start the proxy first", StatusSeverity.Warning);
            return;
        }

        var owner = TryGetMainWindow();
        if (!await _dialogs.ConfirmRotateRootCaAsync(owner))
        {
            SetStatus("Clear and reinstall root CA cancelled", StatusSeverity.Neutral);
            return;
        }

        if (DecryptHttps)
            SetDecryptHttpsCore(false);

        var oldThumb = _interception.RootCertificate?.Thumbprint;
        var ok = _interception.RotateRootCertificate(machineStore: false);
        if (!ok)
        {
            SetStatus("Clear and reinstall root CA failed — see logs", StatusSeverity.Error, toastImportant: true);
            return;
        }

        var newThumb = _interception.RootCertificate?.Thumbprint;
        var changed = !string.IsNullOrEmpty(newThumb) &&
                      !string.Equals(oldThumb, newThumb, StringComparison.OrdinalIgnoreCase);

        if (await _dialogs.ConfirmInstallRootCaAsync(owner))
        {
            SetStatus("Trusting root CA…", StatusSeverity.Busy);
            var trusted = await EnsureRootCaTrustedAsync(promptIfNeeded: true);
            var message = trusted
                ? (changed
                    ? "Root CA cleared and trusted — ready to enable Decrypt HTTPS"
                    : "Root CA trusted — ready to enable Decrypt HTTPS")
                : FormatOsTrustFailureStatus(_interception.LastOsTrustResult);
            if (trusted)
                SetOsTrustSuccessStatus();
            else
                SetStatus(message, StatusSeverity.Error, toastImportant: true);
            return;
        }

        SetStatus(FormatRotateCaDeferredTrustStatus(changed), StatusSeverity.Warning, toastImportant: true);
    }

    private static string FormatRotateCaInstallStatus(bool trusted, bool changed)
    {
        if (!trusted)
            return "Root CA cleared but trust failed — use Install root CA or Export CA";
        return changed ? "Root CA cleared and reinstalled — enable Decrypt HTTPS when ready" : "Root CA recreate completed and trusted";
    }

    private static string FormatRotateCaDeferredTrustStatus(bool changed) =>
        changed ? "Root CA cleared — Install root CA (or enable Decrypt HTTPS) to trust the new certificate" : "Root CA recreate completed — Install root CA to trust";

    private Task ExportCaAsync()
    {
        var path = _interception.ExportRootCertificate();
        if (path is null)
        {
            SetStatus("No root certificate yet — Start the proxy first", StatusSeverity.Warning);
        }
        else
        {
            SetStatus("Exported CA: " + path, StatusSeverity.Success, toastImportant: true);
        }

        return Task.CompletedTask;
    }

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

        await LoopbackExemptWindow.ShowAsync(owner);
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

        var saved = await SessionRetentionWindow.ShowAsync(owner, _settings);
        StatusText = saved
            ? "Session retention saved — restart Inspector to apply"
            : "Session retention cancelled";
    }

    private async Task OpenAboutAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            return;
        }

        await AboutWindow.ShowAsync(owner);
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

        var saved = await LoggingSettingsWindow.ShowAsync(
            owner,
            _settings,
            s =>
            {
                _interception.ConfigureLogging(s);
                DebugFileLogging = IsDebugFileLoggingEnabled(s);
            });
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

    private async Task OpenHttpsDecryptHostsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            StatusText = "HTTPS sites to decrypt requires the main window";
            return;
        }

        var saved = await HttpsDecryptHostsWindow.ShowAsync(
            owner,
            _settings,
            ApplyDecryptHostListsFromSettings);
        StatusText = saved
            ? "HTTPS sites to decrypt saved (applies to new connections)"
            : "HTTPS sites to decrypt cancelled";
    }

    private async Task ResetSettingsAsync()
    {
        var owner = TryGetMainWindow();
        if (!await _dialogs.ConfirmResetSettingsAsync(owner))
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

    private void ApplyDecryptHostListsFromSettings()
    {
        var s = _settings.Current;
        _interception.DecryptSkipHosts = s.DecryptSkipHosts?.ToList() ?? [];
        _interception.DecryptOnlyHosts = s.DecryptOnlyHosts?.ToList() ?? [];
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
        RaiseSessionCommandCanExecuteChanged();
    }

    private void RaiseSessionCommandCanExecuteChanged()
    {
        _clearSessionsCommand.RaiseCanExecuteChanged();
        _removeSelectedSessionsCommand.RaiseCanExecuteChanged();
        _exportSelectedHarCommand.RaiseCanExecuteChanged();
        _exportSelectedArchiveCommand.RaiseCanExecuteChanged();
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
    public ICommand SetUpdateChannelStableCommand { get; }
    public ICommand SetUpdateChannelBetaCommand { get; }
    public ICommand ToggleCheckForUpdatesOnStartupCommand { get; }
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
    public ICommand ToggleIgnoreServerCertificateErrorsCommand { get; }
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
    public ICommand ResetSettingsCommand { get; }
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
                    StatusText = "Start the proxy before enabling system proxy";
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
                    "System proxy enabled. For Chrome: disable QUIC (--disable-quic) or H3 may bypass the proxy.";
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
        _interception.DecryptHttps = _decryptHttps;
        ApplyDecryptHostListsFromSettings();
        _debugFileLogging = IsDebugFileLoggingEnabled(s);
        _interception.ConfigureLogging(s);

        AutoResponder.Enabled = s.AutoResponderEnabled;
        AutoResponder.LoadFromDtos(s.AutoResponderRules);
        Breakpoints.Enabled = s.BreakpointEnabled;
        Breakpoints.UrlFilter = string.IsNullOrEmpty(s.BreakpointUrlFilter) ? "*" : s.BreakpointUrlFilter;
    }

    private void NotifySettingsUiChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindAddress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoStartCapture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoSystemProxyOnStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoreServerCertificateErrors)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BreakpointOnResponse)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptOnRequest)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptOnResponse)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugFileLogging)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsBeta)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChannelIsStable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckForUpdatesOnStartup)));
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
        s.IgnoreServerCertificateErrors = _interception.IgnoreServerCertificateErrors;
        s.AutoResponderEnabled = AutoResponder.Enabled;
        s.AutoResponderRules = AutoResponder.ToDtos();
        s.BreakpointEnabled = Breakpoints.Enabled;
        s.BreakpointUrlFilter = Breakpoints.UrlFilter;
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
                StatusText = "Start the proxy before enabling Decrypt HTTPS";
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

                SetStatus("Trusting root CA…", StatusSeverity.Busy);
                if (!await EnsureRootCaTrustedAsync(promptIfNeeded: true))
                {
                    StatusText = FormatOsTrustFailureStatus(_interception.LastOsTrustResult);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
                    return;
                }
            }

            SetDecryptHttpsCore(true);
            StatusText = "Decrypting HTTPS";
        }
        finally
        {
            _decryptHttpsBusy = false;
        }
    }

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

        if (_userRemovalDepth > 0)
        {
            RefreshSessionCountText();
            return;
        }

        _retentionEvictedTotal += removed.Count;
        RefreshSessionCountText();
        if (removed.Count == 1)
        {
            StatusText = "Removed 1 oldest session to stay under limits";
        }
        else
        {
            StatusText = $"Removed {removed.Count} oldest sessions to stay under limits";
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
        DateTimeOffset? oldest = null;
        if (_retentionEvictedTotal > 0 && _all.Count > 0)
        {
            oldest = _all[0].StartedUtc;
            for (var i = 1; i < _all.Count; i++)
            {
                var t = _all[i].StartedUtc;
                if (t < oldest.Value)
                {
                    oldest = t;
                }
            }
        }

        SessionCountText = SessionSearch.BuildSessionCountText(
            Sessions.Count,
            _all.Count,
            SearchQuery,
            _store.SpilledCount,
            _retentionEvictedTotal,
            oldest);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSessions)));
        RaiseSessionCommandCanExecuteChanged();
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

    /// <summary>Startup or Help → Check for updates. When <paramref name="promptIfAvailable"/>, offer install dialog.</summary>
    public async Task CheckUpdatesAsync(bool promptIfAvailable = true)
    {
        var channel = _updates.ChannelDisplayName;
        SetStatus($"Checking for updates ({channel})…", StatusSeverity.Busy);
        var result = await _updates.CheckAsync();
        if (!result.UpdateAvailable || string.IsNullOrEmpty(result.AssetUrl))
        {
            var upToDate = result.Message.Contains("up to date", StringComparison.OrdinalIgnoreCase);
            SetStatus(
                result.Message,
                upToDate ? StatusSeverity.Success : StatusSeverity.Warning,
                toastImportant: true);
            return;
        }

        SetStatus(result.Message, StatusSeverity.Success, toastImportant: true);
        if (!promptIfAvailable)
        {
            return;
        }

        var owner = TryGetMainWindow();
        var version = result.RemoteVersion ?? "";
        if (!await _dialogs.ConfirmInstallUpdateAsync(owner, version, result.ChannelDisplay, result.OfferKind))
        {
            SetStatus(result.Message, StatusSeverity.Success);
            return;
        }

        SetStatus("Downloading update…", StatusSeverity.Busy);
        var (ok, message) = await _updates.DownloadAndStartApplyAsync(result);
        SetStatus(message, ok ? StatusSeverity.Success : StatusSeverity.Error, toastImportant: true);
        if (!ok)
        {
            return;
        }

        SetStatus($"Installing {version} ({result.ChannelDisplay})… restarting.", StatusSeverity.Busy);
        BeginBackgroundShutdown();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            TryGetMainWindow()?.Close();
        }
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
        SetStatus("Starting proxy…", StatusSeverity.Busy);
        await _interception.StartAsync(address, BindPort);
        if (_interception.BoundPort > 0)
        {
            BindPort = _interception.BoundPort;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BindPort)));
        }

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
            SetStatus(
                SystemProxy
                    ? $"Proxy running on {FormatBindDisplay()}:{BindPort}; system proxy on — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS."
                    : $"Proxy running on {FormatBindDisplay()}:{BindPort} — Decrypt HTTPS off (root CA not trusted). Install CA or enable Decrypt HTTPS.",
                StatusSeverity.Warning);
            return;
        }

        if (SystemProxy)
        {
            SetStatus(
                _decryptHttps
                    ? $"Proxy running on {FormatBindDisplay()}:{BindPort}; system proxy on. Decrypt HTTPS on. Chrome: --disable-quic or H3 may bypass."
                    : $"Proxy running on {FormatBindDisplay()}:{BindPort}; system proxy on. HTTPS shown as encrypted tunnels until Decrypt HTTPS is enabled." +
                      " Chrome/Edge: --disable-quic or HTTP/3 may bypass the proxy.",
                StatusSeverity.Success);
            return;
        }

        SetStatus(
            _decryptHttps
                ? $"Proxy running on {FormatBindDisplay()}:{BindPort} — Decrypt HTTPS on. Enable System proxy if needed. Chrome: --disable-quic or H3 may bypass."
                : $"Proxy running on {FormatBindDisplay()}:{BindPort} — HTTPS shown as encrypted tunnels until Decrypt HTTPS is enabled. Enable System proxy if needed.",
            StatusSeverity.Success);
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

    private async Task ReplaySelectedAsync()
    {
        if (SelectedSession is null)
        {
            SetStatus("Select a session to replay", StatusSeverity.Warning);
            return;
        }

        SetStatus("Replaying…", StatusSeverity.Busy);
        await _store.EnsureBodiesLoadedAsync(SelectedSession).ConfigureAwait(false);
        var result = await ReplayService.ReplayAsync(
            SelectedSession,
            ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            SetStatus(
                result.Ok
                    ? $"Replay → HTTP {result.StatusCode}: {Truncate(result.Message, 120)}"
                    : "Replay failed: " + result.Message,
                result.Ok ? StatusSeverity.Success : StatusSeverity.Error,
                toastImportant: !result.Ok);
        }).ConfigureAwait(false);
    }

    private async Task SendComposerAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerUrl))
        {
            SetStatus("Composer URL is required", StatusSeverity.Warning);
            return;
        }

        SetStatus("Composer sending…", StatusSeverity.Busy);
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
            SetStatus("Composer failed: " + result.Message, StatusSeverity.Error, toastImportant: true);
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
        SetStatus($"Composer → HTTP {result.StatusCode} (session #{snap.Id})", StatusSeverity.Success);
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
            SetStatus("No sessions to export", StatusSeverity.Warning);
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            SetStatus("Export HAR cancelled", StatusSeverity.Neutral);
            return;
        }

        try
        {
            var sessions = _all.ToList();
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + StatusText update
            // raced with headless WaitUntil pumps on macOS (file written, StatusText stayed Ready).
            SetStatus("Exporting HAR…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions);
            await SessionArchive.ExportHarAsync(sessions, path);
            SetStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetStatus("Export HAR failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }

    private async Task ExportSelectedHarAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            SetStatus("Select a session to export", StatusSeverity.Warning);
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            SetStatus("Export HAR cancelled", StatusSeverity.Neutral);
            return;
        }

        try
        {
            SetStatus("Exporting HAR…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions);
            await SessionArchive.ExportHarAsync(sessions, path);
            SetStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetStatus("Export HAR failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }

    private async Task ImportHarAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import HAR", "HAR", "*.har", ZipFileFilter);
        if (path is null)
        {
            SetStatus("No .har or archive to import", StatusSeverity.Warning);
            return;
        }

        SetStatus("Importing…", StatusSeverity.Busy);
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
        SetStatus($"Appended {imported.Count} sessions from {Path.GetFileName(path)}", StatusSeverity.Success, toastImportant: true);
    }

    private async Task ExportArchiveAsync()
    {
        if (_all.Count == 0)
        {
            SetStatus("No sessions to export", StatusSeverity.Warning);
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetStatus("Export archive cancelled", StatusSeverity.Neutral);
            return;
        }

        try
        {
            var sessions = _all.ToList();
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + StatusText update
            // raced with headless WaitUntil pumps on macOS (file written, StatusText stayed Ready).
            SetStatus("Exporting archive…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path);
            SetStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetStatus("Export archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }

    private async Task ExportSelectedArchiveAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            SetStatus("Select a session to export", StatusSeverity.Warning);
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetStatus("Export archive cancelled", StatusSeverity.Neutral);
            return;
        }

        try
        {
            SetStatus("Exporting archive…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path);
            SetStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetStatus("Export archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }

    private async Task ImportArchiveAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import archive", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetStatus("No titanium-inspector archive to import", StatusSeverity.Warning);
            return;
        }

        SetStatus("Importing archive…", StatusSeverity.Busy);
        try
        {
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + off-thread
            // StatusText throws Avalonia "Call from invalid thread" on Windows CI, and
            // nested MarshalToUiAsync StatusText updates flaked on macOS headless.
            var imported = await SessionArchive.ImportNativeArchiveAsync(path);
            foreach (var snap in imported)
            {
                _store.Add(snap);
            }

            ApplyFilter();
            RefreshSessionCountText();
            SetStatus($"Appended {imported.Count} sessions from {Path.GetFileName(path)}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetStatus("Import archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
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

internal sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
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
        catch
        {
            // UI commands must not tear down the process (async void).
        }
    }

    public event EventHandler? CanExecuteChanged;
}
