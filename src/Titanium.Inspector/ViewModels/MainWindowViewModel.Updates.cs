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
    private static async Task OpenAboutAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
        {
            return;
        }

        await AboutWindow.ShowAsync(owner);
    }
    /// <summary>Startup or Help → Check for updates. When <paramref name="promptIfAvailable"/>, offer install dialog.</summary>
    public async Task CheckUpdatesAsync(bool promptIfAvailable = true)
    {
        var channel = _updates.ChannelDisplayName;
        SetStatus($"Checking for updates ({channel})…", StatusSeverity.Busy);
        var result = await _updates.CheckAsync(_statusRevertCts?.Token ?? CancellationToken.None);
        if (!result.UpdateAvailable || string.IsNullOrEmpty(result.AssetUrl))
        {
            var upToDate = result.Message.Contains("up to date", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("No newer Beta", StringComparison.OrdinalIgnoreCase);
            if (upToDate)
            {
                SetTransientStatus(
                    result.Message,
                    StatusSeverity.Neutral,
                    toastImportant: true,
                    revertMs: 4000,
                    toastSeverity: StatusSeverity.Success);
            }
            else
            {
                SetOutcomeStatus(result.Message, StatusSeverity.Warning, toastImportant: true);
            }

            return;
        }

        SetOutcomeStatus(result.Message, StatusSeverity.Success, toastImportant: true);
        if (!promptIfAvailable)
        {
            return;
        }

        var owner = TryGetMainWindow();
        var version = result.RemoteVersion ?? "";
        if (!await AwaitCancellableAsync(_dialogs.ConfirmInstallUpdateAsync(owner, version, result.ChannelDisplay, result.OfferKind)))
        {
            SetOutcomeStatus(result.Message, StatusSeverity.Success);
            return;
        }

        SetStatus("Downloading update…", StatusSeverity.Busy);
        var (ok, message) = await _updates.DownloadAndStartApplyAsync(result, _statusRevertCts?.Token ?? CancellationToken.None);
        SetOutcomeStatus(message, ok ? StatusSeverity.Success : StatusSeverity.Error, toastImportant: true);
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
    private async Task ReplaySelectedAsync()
    {
        if (SelectedSession is null)
        {
            SetGuardStatus("Select a session to replay");
            return;
        }

        SetStatus("Replaying…", StatusSeverity.Busy);
        await _store.EnsureBodiesLoadedAsync(SelectedSession, _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        var result = await ReplayService.ReplayAsync(
            SelectedSession,
            ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors,
            cancellationToken: _statusRevertCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        await MarshalToUiAsync(() =>
        {
            SetOutcomeStatus(
                result.Ok
                    ? $"Replay → HTTP {result.StatusCode}: {Truncate(result.Message, 120)}"
                    : "Replay failed: " + result.Message,
                result.Ok ? StatusSeverity.Success : StatusSeverity.Error,
                toastImportant: !result.Ok);
        }, StatusCancelToken).ConfigureAwait(false);
    }
    private async Task SendComposerAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerUrl))
        {
            SetGuardStatus("Composer URL is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposerBodyFilePath)
            && !string.IsNullOrEmpty(ComposerBody)
            && ComposerBody.Length > InspectorBodyLimits.MaxBodyBytes)
        {
            SetOutcomeStatus(
                $"Composer body is {SessionDisplayFormat.FormatByteSize(ComposerBody.Length)} — consider Load body from file",
                StatusSeverity.Warning);
        }

        SetStatus("Composer sending…", StatusSeverity.Busy);
        var template = new SessionSnapshot
        {
            Method = string.IsNullOrWhiteSpace(ComposerMethod) ? "GET" : ComposerMethod,
            Url = ComposerUrl,
            RequestHeadersText = ComposerHeaders,
            RequestBodyText = HasComposerBodyFile ? null : ComposerBody,
            ContentType = GuessContentType(ComposerHeaders),
        };

        ReplayResult result;
        try
        {
            result = await ReplayService.ReplayAsync(
                template,
                editedUrl: ComposerUrl,
                editedMethod: ComposerMethod,
                editedBody: HasComposerBodyFile ? null : ComposerBody,
                editedHeaders: ComposerHeaders,
                bodyFilePath: ComposerBodyFilePath,
                ignoreServerCertificateErrors: _interception.IgnoreServerCertificateErrors,
                cancellationToken: _statusRevertCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Composer failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
            return;
        }

        if (!result.Ok)
        {
            SetOutcomeStatus("Composer failed: " + result.Message, StatusSeverity.Error, toastImportant: true);
            return;
        }

        var requestPreview = HasComposerBodyFile
            ? $"(file: {Path.GetFileName(ComposerBodyFilePath)})"
            : InspectorBodyLimits.TruncateText(ComposerBody ?? "");
        var snap = new SessionSnapshot
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Method = template.Method,
            Url = ComposerUrl,
            Host = TryHost(ComposerUrl),
            StartedUtc = DateTimeOffset.UtcNow,
            RequestHeadersText = ComposerHeaders,
            RequestBodyText = requestPreview,
            StatusCode = result.StatusCode,
            ResponseHeadersText = result.ResponseHeaders,
            ResponseBodyText = result.ResponseBody,
            ResponseBodyBytes = result.ResponseBodyBytes,
            ResponseBodyOriginalSize = result.ResponseBodyOriginalSize,
            ResponseBodyCapture = result.ResponseBodyCapture,
            ContentType = template.ContentType,
            BodySize = result.ResponseBodyOriginalSize ?? result.ResponseBody?.Length,
            Protocol = "Composer",
        };

        _store.Add(snap);
        ApplyFilter();
        RefreshSessionCountText();
        SelectedSession = snap;
        SetOutcomeStatus($"Composer → HTTP {result.StatusCode} (session #{snap.Id})", StatusSeverity.Success);
    }
    private static string? GuessContentType(string headers)
    {
        var map = SessionInspectors.ParseHeaderBlock(headers);
        return map.TryGetValue("Content-Type", out var ct) ? ct : null;
    }
}
