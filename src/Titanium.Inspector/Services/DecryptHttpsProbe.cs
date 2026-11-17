using System.Diagnostics;
using System.Net;

namespace Titanium.Inspector.Services;

/// <summary>
///     Live MITM check: fetch HTTPS through the running explicit proxy and require a
///     decrypted (non-tunnel) session to appear.
/// </summary>
internal static class DecryptHttpsProbe
{
    public const string DefaultHost = "example.com";
    public static readonly Uri DefaultUri = new("https://example.com/");

    public sealed record Result(bool Ok, string Message);

    /// <summary>
    ///     Turns on decrypt long enough to issue one HTTPS GET via the proxy.
    ///     Caller must already have <see cref="InterceptionService.DecryptHttps"/> true
    ///     and the proxy running.
    /// </summary>
    public static async Task<Result> VerifyAsync(
        InterceptionService interception,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!interception.IsRunning || interception.BoundPort <= 0)
            return new Result(false, "Proxy is not listening");

        if (!interception.DecryptHttps)
            return new Result(false, "Decrypt HTTPS is off");

        SessionSnapshot? captured = null;
        void OnSession(object? _, SessionSnapshot snap)
        {
            if (snap.IsTunnel)
                return;
            if (snap.Url.Contains(DefaultHost, StringComparison.OrdinalIgnoreCase)
                || (snap.Host?.Contains(DefaultHost, StringComparison.OrdinalIgnoreCase) == true))
            {
                captured = snap;
            }
        }

        interception.SessionCaptured += OnSession;
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                // Probe only: we assert Inspector decrypted the session, not OS trust of the leaf.
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            try
            {
                _ = await client.GetAsync(DefaultUri, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // fall through to captured check / timeout message
            }
            catch (HttpRequestException ex)
            {
                // Still succeed if a decrypted session was recorded (origin/network flake after MITM).
                if (captured is not null)
                    return new Result(true, "Decrypted session captured (" + Truncate(captured.Url, 80) + ")");

                return new Result(false, "Probe request failed: " + Truncate(ex.Message, 120));
            }

            while (captured is null && sw.Elapsed < timeout)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            if (captured is not null)
                return new Result(true, "Decrypted session captured (" + Truncate(captured.Url, 80) + ")");

            return new Result(false, "No decrypted HTTPS session within " + (int)timeout.TotalSeconds + "s");
        }
        finally
        {
            interception.SessionCaptured -= OnSession;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
