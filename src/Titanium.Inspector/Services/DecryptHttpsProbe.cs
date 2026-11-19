using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

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

        var captured = new ProbeCapture();
        void OnSession(object? _, SessionSnapshot snap) => captured.Remember(snap);

        interception.SessionCaptured += OnSession;
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                // Probe only: we assert Inspector decrypted the session, not OS trust of the leaf.
                ServerCertificateCustomValidationCallback = AcceptMitmProbeLeaf,
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            Exception? requestError = null;
            try
            {
                _ = await client.GetAsync(DefaultUri, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                requestError = ex;
            }
            catch (HttpRequestException ex)
            {
                requestError = ex;
            }

            while (ReadCaptured(captured) is null && sw.Elapsed < timeout)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            if (ReadCaptured(captured) is { } session)
                return new Result(true, "Decrypted session captured (" + Truncate(session.Url, 80) + ")");

            if (requestError is HttpRequestException httpEx)
                return new Result(false, "Probe request failed: " + Truncate(httpEx.Message, 120));

            return new Result(false, "No decrypted HTTPS session within " + (int)timeout.TotalSeconds + "s");
        }
        finally
        {
            interception.SessionCaptured -= OnSession;
        }
    }

    /// <summary>
    ///     Accepts the probe leaf on purpose. Success is a decrypted session in Inspector,
    ///     not operating-system trust of the generated certificate.
    /// </summary>
    private static bool AcceptMitmProbeLeaf(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        _ = request;
        _ = certificate;
        _ = chain;
        _ = sslPolicyErrors;
        return true; // NOSONAR S4830
    }

    // The callback writes the slot; read it here so the poll is not a local the flow analysis treats as stuck null.
    private static SessionSnapshot? ReadCaptured(ProbeCapture captured) => captured.Session;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed class ProbeCapture
    {
        public SessionSnapshot? Session { get; private set; }

        public void Remember(SessionSnapshot snap)
        {
            if (snap.IsTunnel)
                return;
            if (snap.Url.Contains(DefaultHost, StringComparison.OrdinalIgnoreCase)
                || snap.Host?.Contains(DefaultHost, StringComparison.OrdinalIgnoreCase) == true)
            {
                Session = snap;
            }
        }
    }
}
