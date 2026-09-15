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

public sealed partial class MainWindowViewModel
{
    private Task ClearSessionsAsync()
    {
        // Drop selection before mutating the grid so the DataGrid cannot cascade-select
        // a neighbor row (SelectedSession setter would reopen a closed details pane).
        _selectedSessions.Clear();
        SelectedSession = null;
        ShowSessionDetails = false;

        _userRemovalDepth++;
        _suppressOpenSessionDetails = true;
        try
        {
            _store.Clear();
            Sessions.Clear();
        }
        finally
        {
            _suppressOpenSessionDetails = false;
            _userRemovalDepth--;
        }

        _retentionEvictedTotal = 0;
        _interception.ResetSessionIdSequence();
        RefreshSessionCountText();
        NotifyFilterSelectionProperties();
        SetOutcomeStatus("Sessions cleared", StatusSeverity.Success, toastImportant: true);
        return Task.CompletedTask;
    }
    private Task RemoveSelectedSessionsAsync()
    {
        var selected = ResolveExportSelection();
        if (selected.Count == 0)
        {
            SetGuardStatus("Select one or more sessions to remove");
            return Task.CompletedTask;
        }

        var ids = selected.Select(s => s.Id).ToHashSet();
        // Clear selection of removed rows before store/grid mutation (same cascade as Clear).
        _selectedSessions.RemoveAll(s => ids.Contains(s.Id));
        if (SelectedSession is not null && ids.Contains(SelectedSession.Id))
        {
            SelectedSession = null;
            ShowSessionDetails = false;
        }

        _userRemovalDepth++;
        _suppressOpenSessionDetails = true;
        try
        {
            _store.Remove(ids);
            for (var i = Sessions.Count - 1; i >= 0; i--)
            {
                if (ids.Contains(Sessions[i].Id))
                {
                    Sessions.RemoveAt(i);
                }
            }
        }
        finally
        {
            _suppressOpenSessionDetails = false;
            _userRemovalDepth--;
        }

        RefreshSessionCountText();
        NotifyFilterSelectionProperties();
        SetOutcomeStatus(
            selected.Count == 1 ? "Removed 1 session" : $"Removed {selected.Count} sessions",
            StatusSeverity.Success,
            toastImportant: true);
        return Task.CompletedTask;
    }
    private async Task LoadFromSelectedAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
        {
            StatusText = "Select a session to load into Composer";
            return;
        }

        await _store.EnsureBodiesLoadedAsync(selected, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            ComposerMethod = selected.Method;
            ComposerUrl = selected.Url;
            ComposerHeaders = selected.RequestHeadersText ?? "";
            ComposerBody = selected.RequestBodyText ?? "";
            ComposerBodyFilePath = null;
            StatusText = selected.RequestBodyCapture is BodyCaptureState.Truncated or BodyCaptureState.NotCaptured
                ? "Composer loaded (request body was truncated or not fully captured)"
                : "Composer loaded from selected session";
        }, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FillGraphQlFromSelectedAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
        {
            SetGuardStatus("Select a session to copy its GraphQL operation");
            return;
        }

        await _store.EnsureBodiesLoadedAsync(selected, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            if (!GraphQlOperationMatcher.TryGetOperationName(selected.RequestBodyText, out var name) ||
                string.IsNullOrWhiteSpace(name))
            {
                SetGuardStatus("Selected session has no GraphQL operation name in the request body");
                return;
            }

            if (ShowBreakpointsPane)
            {
                Breakpoints.GraphQlOperationName = name;
            }
            else if (ShowAutoResponderPane)
            {
                AutoResponderGraphQlOperation = name;
            }
            else if (ShowMapRemotePane)
            {
                MapRemoteGraphQlOperation = name;
            }
            else
            {
                SetGuardStatus("Open Breakpoints, AutoResponder, or Map Remote to set the GraphQL filter");
                return;
            }

            StatusText = $"GraphQL filter set to {name}";
        }, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    private async Task LoadIntoComposerAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
        {
            StatusText = "Select a session to load into Composer";
            return;
        }

        await _store.EnsureBodiesLoadedAsync(selected, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            ComposerMethod = selected.Method;
            ComposerUrl = selected.Url;
            ComposerHeaders = selected.RequestHeadersText ?? "";
            ComposerBody = selected.RequestBodyText ?? "";
            ComposerBodyFilePath = null;
            StatusText = selected.RequestBodyCapture is BodyCaptureState.Truncated or BodyCaptureState.NotCaptured
                ? "Composer loaded (request body was truncated or not fully captured)"
                : "Composer loaded from selected session";
        }, StatusCancelToken).ConfigureAwait(false);
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
    private async Task CopyAsCurlAsync()
    {
        if (!TryBuildCopyAsCurl(out var curl))
        {
            StatusText = "Select one session with a URL to copy as curl";
            return;
        }

        await CopyTextToClipboardAsync(curl).ConfigureAwait(false);
        StatusText = "Copied as curl";
    }
    private async Task CopyAsFetchAsync()
    {
        if (!TryBuildCopyAsFetch(out var fetch))
        {
            StatusText = "Select one session with a URL to copy as fetch";
            return;
        }

        await CopyTextToClipboardAsync(fetch).ConfigureAwait(false);
        StatusText = "Copied as fetch";
    }
    private async Task DiffSessionsAsync()
    {
        if (!TryBuildSessionDiff(out var diff))
        {
            StatusText = "Select exactly two sessions to diff";
            return;
        }

        SessionDiffText = diff.Text;
        await CopyTextToClipboardAsync(diff.Text).ConfigureAwait(false);
        ShowSessionDetails = true;
        SelectedOuterPaneIndex = 0;
        SelectedInspectTabIndex = 3; // Diff tab
        StatusText = diff.HasDifferences ? "Session Diff: differences found (copied)" : "Session Diff: identical (copied)";
    }
    /// <summary>Compares exactly two selected sessions (E2E / probe).</summary>
    public bool TryBuildSessionDiff(out SessionDiffResult diff)
    {
        diff = new SessionDiffResult(false, "");
        var selection = ResolveFilterSelection();
        if (selection.Count != 2)
        {
            return false;
        }

        diff = SessionDiff.Compare(selection[0], selection[1]);
        return true;
    }
    /// <summary>Builds curl for the single selected session (E2E / probe).</summary>
    public bool TryBuildCopyAsCurl(out string curl)
    {
        curl = "";
        var session = ResolveSingleCopySession();
        if (session is null || !SessionRequestCodegen.CanGenerate(session))
        {
            return false;
        }

        curl = SessionRequestCodegen.ToCurl(session);
        return true;
    }
    /// <summary>Builds fetch for the single selected session (E2E / probe).</summary>
    public bool TryBuildCopyAsFetch(out string fetch)
    {
        fetch = "";
        var session = ResolveSingleCopySession();
        if (session is null || !SessionRequestCodegen.CanGenerate(session))
        {
            return false;
        }

        fetch = SessionRequestCodegen.ToFetch(session);
        return true;
    }
    private static async Task CopyTextToClipboardAsync(string text)
    {
        var window = TryGetMainWindow();
        if (window?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
        }
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
    /// <summary>True when at least one session is selected.</summary>
    public bool HasSelectedSessions => ResolveFilterSelection().Count > 0;
    /// <summary>True when exactly one session is selected (Replay / Composer).</summary>
    public bool HasSingleSelectedSession => ResolveFilterSelection().Count == 1;
    /// <summary>True when at least one selected session has a URL to copy.</summary>
    public bool CanCopyUrl => ResolveCopyUrls().Count > 0;
    /// <summary>True when exactly two sessions are selected for Session Diff.</summary>
    public bool CanDiffSessions => ResolveFilterSelection().Count == 2;
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

        return SelectedSession is null
            ? Array.Empty<SessionSnapshot>()
            : new List<SessionSnapshot> { SelectedSession };
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanExcludeHost)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFilterByProcess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedSessions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSingleSelectedSession)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopyUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopyAsCurl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDiffSessions)));
        RaiseSessionCommandCanExecuteChanged();
    }
    private void RaiseSessionCommandCanExecuteChanged()
    {
        _clearSessionsCommand.RaiseCanExecuteChanged();
        _removeSelectedSessionsCommand.RaiseCanExecuteChanged();
        _exportSelectedHarCommand.RaiseCanExecuteChanged();
        _exportSelectedArchiveCommand.RaiseCanExecuteChanged();
        _copyAsCurlCommand.RaiseCanExecuteChanged();
        _copyAsFetchCommand.RaiseCanExecuteChanged();
        _diffSessionsCommand.RaiseCanExecuteChanged();
    }
    private List<string> ResolveCopyUrls() =>
        ResolveFilterSelection()
            .Where(snap => !string.IsNullOrEmpty(snap.Url))
            .Select(snap => snap.Url)
            .ToList();
    private Task AddAutoResponderRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(AutoResponderLocalFilePath)
            && AutoResponderBody.Length > InspectorBodyLimits.MaxInlineToolBodyChars)
        {
            SetGuardStatus("Inline AutoResponder body is too large — use Map Local for larger bodies");
            return Task.CompletedTask;
        }

        AutoResponder.Rules.Add(new AutoResponderRule
        {
            MatchUrl = AutoResponderMatch,
            StatusCode = AutoResponderStatus,
            Body = AutoResponderBody,
            ContentType = AutoResponderContentType,
            LocalFilePath = AutoResponderLocalFilePath,
            GraphQlOperationName = AutoResponderGraphQlOperation,
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

        if (string.IsNullOrWhiteSpace(AutoResponderLocalFilePath)
            && AutoResponderBody.Length > InspectorBodyLimits.MaxInlineToolBodyChars)
        {
            SetGuardStatus("Inline AutoResponder body is too large — use Map Local for larger bodies");
            return Task.CompletedTask;
        }

        var rule = AutoResponder.SelectedRule;
        rule.MatchUrl = AutoResponderMatch;
        rule.StatusCode = AutoResponderStatus;
        rule.Body = AutoResponderBody;
        rule.ContentType = AutoResponderContentType;
        rule.LocalFilePath = AutoResponderLocalFilePath;
        rule.GraphQlOperationName = AutoResponderGraphQlOperation;
        PersistAutoResponder();
        StatusText = "AutoResponder rule updated";
        return Task.CompletedTask;
    }
    private async Task BrowseAutoResponderLocalFileAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync(
            "Map Local — choose response file",
            "All files",
            "*.*").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        AutoResponderLocalFilePath = path;
        StatusText = $"Map Local file: {path}";
    }
    private Task AddMapRemoteRuleAsync()
    {
        MapRemote.Rules.Add(new MapRemoteRule
        {
            MatchUrl = MapRemoteMatch,
            TargetUrl = MapRemoteTarget,
            GraphQlOperationName = MapRemoteGraphQlOperation,
            Enabled = true,
        });
        PersistMapRemote();
        StatusText = $"Map Remote rule added ({MapRemote.Rules.Count} total)";
        return Task.CompletedTask;
    }
    private Task DeleteMapRemoteRuleAsync()
    {
        if (MapRemote.SelectedRule is null)
        {
            StatusText = "Select a Map Remote rule to delete";
            return Task.CompletedTask;
        }

        MapRemote.Rules.Remove(MapRemote.SelectedRule);
        MapRemote.SelectedRule = null;
        PersistMapRemote();
        StatusText = "Map Remote rule deleted";
        return Task.CompletedTask;
    }
    private Task UpdateMapRemoteRuleAsync()
    {
        if (MapRemote.SelectedRule is null)
        {
            StatusText = "Select a Map Remote rule to update";
            return Task.CompletedTask;
        }

        var rule = MapRemote.SelectedRule;
        rule.MatchUrl = MapRemoteMatch;
        rule.TargetUrl = MapRemoteTarget;
        rule.GraphQlOperationName = MapRemoteGraphQlOperation;
        PersistMapRemote();
        StatusText = "Map Remote rule updated";
        return Task.CompletedTask;
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

    /// <summary>
    /// Re-evaluate filter membership when status/content-type/etc. arrive after the row was added.
    /// Avoids a full <see cref="ApplyFilter"/> rebuild on every SSE tee chunk.
    /// </summary>
    private void OnSessionUpdatedForFilter(SessionSnapshot snapshot)
    {
        var matches = SessionSearch.Matches(snapshot, SearchQuery);
        var index = Sessions.IndexOf(snapshot);
        if (matches)
        {
            if (index < 0)
            {
                Sessions.Add(snapshot);
                RefreshSessionCountText();
            }
        }
        else if (index >= 0)
        {
            if (ReferenceEquals(SelectedSession, snapshot))
            {
                SelectedSession = null;
            }

            Sessions.RemoveAt(index);
            RefreshSessionCountText();
        }
    }

    private void OnSessionsRemoved(IReadOnlyList<SessionSnapshot> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var ids = removed.Select(s => s.Id).ToHashSet();
        // Clear selection before removing rows — otherwise Avalonia DataGrid selects a
        // neighbor and SelectedSession reopens the details pane.
        _selectedSessions.RemoveAll(s => ids.Contains(s.Id));
        if (SelectedSession is not null && ids.Contains(SelectedSession.Id))
        {
            SelectedSession = null;
            ShowSessionDetails = false;
        }

        _suppressOpenSessionDetails = true;
        try
        {
            for (var i = Sessions.Count - 1; i >= 0; i--)
            {
                if (ids.Contains(Sessions[i].Id))
                {
                    Sessions.RemoveAt(i);
                }
            }
        }
        finally
        {
            _suppressOpenSessionDetails = false;
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
            await _store.EnsureBodiesLoadedAsync(snap, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            await MarshalToUiAsync(() =>
            {
                if (ReferenceEquals(_selected, snap))
                {
                    RefreshSelectedInspectors();
                }
            }, StatusCancelToken).ConfigureAwait(false);
        }
        catch
        {
            await MarshalToUiAsync(() =>
            {
                if (ReferenceEquals(_selected, snap))
                {
                    RefreshSelectedInspectors();
                }
            }, StatusCancelToken).ConfigureAwait(false);
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
        var detailsWereOpen = ShowSessionDetails;
        Sessions.Clear();
        foreach (var s in SessionSearch.Filter(_all, SearchQuery))
        {
            Sessions.Add(s);
        }

        // Restore single selection used by the detail pane when the row still matches the filter.
        // Do not force the pane open — the user may have closed it, and Sessions.Clear() can
        // briefly null SelectedSession via the DataGrid binding.
        if (previouslySelected is not null && Sessions.Contains(previouslySelected))
        {
            if (!ReferenceEquals(_selected, previouslySelected))
            {
                _suppressOpenSessionDetails = true;
                try
                {
                    SelectedSession = previouslySelected;
                }
                finally
                {
                    _suppressOpenSessionDetails = false;
                }
            }

            ShowSessionDetails = detailsWereOpen;
        }
        else if (previouslySelected is not null)
        {
            SelectedSession = null;
        }
    }
    private async Task ExportHarAsync()
    {
        if (_all.Count == 0)
        {
            SetGuardStatus("No sessions to export");
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            SetTransientStatus("Export HAR cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        try
        {
            var sessions = _all.ToList();
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + StatusText update
            // raced with headless WaitUntil pumps on macOS (file written, StatusText stayed Ready).
            SetStatus("Exporting HAR…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions, _statusRevertCts?.Token ?? CancellationToken.None);
            await SessionArchive.ExportHarAsync(sessions, path, _statusRevertCts?.Token ?? CancellationToken.None);
            SetOutcomeStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Export HAR failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private async Task ExportSelectedHarAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            SetGuardStatus("Select a session to export");
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected HAR", "titanium-inspector.har", "HAR", "*.har");
        if (path is null)
        {
            SetTransientStatus("Export HAR cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        try
        {
            SetStatus("Exporting HAR…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions, _statusRevertCts?.Token ?? CancellationToken.None);
            await SessionArchive.ExportHarAsync(sessions, path, _statusRevertCts?.Token ?? CancellationToken.None);
            SetOutcomeStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Export HAR failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private async Task ImportHarAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import HAR", "HAR", "*.har", ZipFileFilter);
        if (path is null)
        {
            SetGuardStatus("No .har or archive to import");
            return;
        }

        SetStatus("Importing…", StatusSeverity.Busy);
        List<SessionSnapshot> imported;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            imported = await SessionArchive.ImportNativeArchiveAsync(path, _statusRevertCts?.Token ?? CancellationToken.None);
        }
        else
        {
            imported = await SessionArchive.ImportHarAsync(path, _statusRevertCts?.Token ?? CancellationToken.None);
        }

        foreach (var snap in imported)
        {
            _store.Add(snap);
        }

        ApplyFilter();
        RefreshSessionCountText();
        SetOutcomeStatus($"Appended {imported.Count} sessions from {Path.GetFileName(path)}", StatusSeverity.Success, toastImportant: true);
    }
    private async Task ExportArchiveAsync()
    {
        if (_all.Count == 0)
        {
            SetGuardStatus("No sessions to export");
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export all archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetTransientStatus("Export archive cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        try
        {
            var sessions = _all.ToList();
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + StatusText update
            // raced with headless WaitUntil pumps on macOS (file written, StatusText stayed Ready).
            SetStatus("Exporting archive…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions, _statusRevertCts?.Token ?? CancellationToken.None);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path, _statusRevertCts?.Token ?? CancellationToken.None);
            SetOutcomeStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Export archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private async Task ExportSelectedArchiveAsync()
    {
        var sessions = ResolveExportSelection();
        if (sessions.Count == 0)
        {
            SetGuardStatus("Select a session to export");
            return;
        }

        var path = await _pathPicker.PickSavePathAsync("Export selected archive", "titanium-inspector.zip", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetTransientStatus("Export archive cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        try
        {
            SetStatus("Exporting archive…", StatusSeverity.Busy);
            await _store.EnsureBodiesLoadedAsync(sessions, _statusRevertCts?.Token ?? CancellationToken.None);
            await SessionArchive.ExportNativeArchiveAsync(sessions, path, _statusRevertCts?.Token ?? CancellationToken.None);
            SetOutcomeStatus($"Exported {sessions.Count} sessions to {path}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Export archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private async Task ImportArchiveAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Import archive", "ZIP", ZipFileFilter);
        if (path is null)
        {
            SetGuardStatus("No titanium-inspector archive to import");
            return;
        }

        SetStatus("Importing archive…", StatusSeverity.Busy);
        try
        {
            // Stay on the UI sync context (RelayCommand). ConfigureAwait(false) + off-thread
            // StatusText throws Avalonia "Call from invalid thread" on Windows CI, and
            // nested MarshalToUiAsync StatusText updates flaked on macOS headless.
            var imported = await SessionArchive.ImportNativeArchiveAsync(path, _statusRevertCts?.Token ?? CancellationToken.None);
            foreach (var snap in imported)
            {
                _store.Add(snap);
            }

            ApplyFilter();
            RefreshSessionCountText();
            SetOutcomeStatus($"Appended {imported.Count} sessions from {Path.GetFileName(path)}", StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Import archive failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private IReadOnlyList<SessionSnapshot> ResolveExportSelection()
    {
        if (_selectedSessions.Count > 0)
        {
            return _selectedSessions.ToList();
        }

        return SelectedSession is null
            ? Array.Empty<SessionSnapshot>()
            : new List<SessionSnapshot> { SelectedSession };
    }
}
