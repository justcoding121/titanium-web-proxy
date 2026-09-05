using Avalonia.Controls;
using Avalonia.Threading;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class MacSslTrustWaitDialog : Window
{
    private MacSslTrustWaitResult _result = MacSslTrustWaitResult.Cancelled;
    private CancellationTokenSource? _pollCts;
    private bool _closing;
    private Func<bool>? _verifySslTrust;

    /// <summary>Give-up deadline so the dialog never spins forever.</summary>
    internal static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromMinutes(5);

    public MacSslTrustWaitDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => CloseWith(MacSslTrustWaitResult.Cancelled);
        ConfirmSavedButton.Click += (_, _) => OnConfirmSavedClicked();
        Activated += (_, _) => TryVerifyOnFocus();
        Closing += (_, e) =>
        {
            if (_closing)
                return;
            // Treat chrome close as cancel unless we already set a result.
            if (_result == MacSslTrustWaitResult.Trusted)
                return;
            if (_result == MacSslTrustWaitResult.NotSavedYet)
                return;
            _result = MacSslTrustWaitResult.Cancelled;
            StopPoll();
        };
    }

    /// <summary>
    /// Opens Keychain guidance, polls <paramref name="verifySslTrust"/> until trusted, user confirms, or give-up.
    /// </summary>
    public static async Task<MacSslTrustWaitResult> ShowAsync(
        Window? owner,
        Func<bool> verifySslTrust,
        Action openKeychain,
        Func<bool>? isInLoginKeychain = null,
        TimeSpan? pollInterval = null,
        string? message = null,
        TimeSpan? giveUpAfter = null)
    {
        if (owner is null)
            return MacSslTrustWaitResult.Cancelled;

        try
        {
            if (verifySslTrust())
                return MacSslTrustWaitResult.Trusted;
        }
        catch
        {
            // continue to wait UI
        }

        var interval = pollInterval is { TotalMilliseconds: > 0 }
            ? pollInterval.Value
            : TimeSpan.FromMilliseconds(1500);
        var deadline = giveUpAfter is { TotalMilliseconds: > 0 }
            ? giveUpAfter.Value
            : DefaultGiveUpAfter;

        var dialog = new MacSslTrustWaitDialog
        {
            _verifySslTrust = verifySslTrust,
        };
        dialog.MessageText.Text = message ?? OsTrustUxCopy.MacSslTrustWaitBody;
        dialog.ConfirmSavedButton.Content = OsTrustUxCopy.MacSslTrustWaitConfirmSaved;
        dialog.OpenKeychainButton.Click += (_, _) =>
        {
            try
            {
                openKeychain();
            }
            catch
            {
                // best-effort
            }
        };

        dialog._pollCts = new CancellationTokenSource();
        var cts = dialog._pollCts;
        _ = PollUntilTrustedAsync(dialog, verifySslTrust, isInLoginKeychain, interval, deadline, cts.Token);

        // Show our wait UI first so Keychain Access does not cover it; open Keychain after.
        var showTask = dialog.ShowDialog(owner);
        dialog.Opened += (_, _) =>
        {
            try
            {
                dialog.Activate();
                openKeychain();
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        dialog.Activate();
                    }
                    catch
                    {
                        // ignore
                    }
                }, DispatcherPriority.Background);
            }
            catch
            {
                // best-effort
            }
        };

        await showTask;
        dialog.StopPoll();
        return dialog._result;
    }

    private void OnConfirmSavedClicked()
    {
        try
        {
            if (_verifySslTrust?.Invoke() == true)
            {
                CloseWith(MacSslTrustWaitResult.Trusted);
                return;
            }
        }
        catch
        {
            // fall through
        }

        StatusText.Text = OsTrustUxCopy.MacSslTrustWaitStatusInKeychain;
        CloseWith(MacSslTrustWaitResult.NotSavedYet);
    }

    private void TryVerifyOnFocus()
    {
        if (_closing || _verifySslTrust is null)
            return;

        try
        {
            if (_verifySslTrust())
                CloseWith(MacSslTrustWaitResult.Trusted);
        }
        catch
        {
            // keep waiting
        }
    }

    private void CloseWith(MacSslTrustWaitResult result)
    {
        if (_closing)
            return;
        _closing = true;
        _result = result;
        StopPoll();
        try
        {
            Close();
        }
        catch
        {
            // ignore
        }
    }

    private static async Task PollUntilTrustedAsync(
        MacSslTrustWaitDialog dialog,
        Func<bool> verifySslTrust,
        Func<bool>? isInLoginKeychain,
        TimeSpan interval,
        TimeSpan giveUpAfter,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                var trusted = false;
                try
                {
                    trusted = verifySslTrust();
                }
                catch
                {
                    // keep polling
                }

                if (trusted)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => dialog.CloseWith(MacSslTrustWaitResult.Trusted));
                    return;
                }

                if (DateTime.UtcNow - started >= giveUpAfter)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.CloseWith(MacSslTrustWaitResult.NotSavedYet));
                    return;
                }

                if (isInLoginKeychain is not null)
                {
                    var inKeychain = false;
                    try
                    {
                        inKeychain = isInLoginKeychain();
                    }
                    catch
                    {
                        // ignore
                    }

                    var status = inKeychain
                        ? OsTrustUxCopy.MacSslTrustWaitStatusInKeychain
                        : OsTrustUxCopy.MacSslTrustWaitStatusWaiting;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested && !dialog._closing)
                            dialog.StatusText.Text = status;
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private void StopPoll()
    {
        try
        {
            _pollCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _pollCts?.Dispose();
        _pollCts = null;
    }
}
