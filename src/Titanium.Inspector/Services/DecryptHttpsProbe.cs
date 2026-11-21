using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Inspector.Services;

/// <summary>
///     Live MITM check: fetch HTTPS through the running explicit proxy and require a
///     decrypted (non-tunnel) session to appear — or any HTTP response whose leaf chains
///     to the MITM root (that alone proves the proxy decrypted).
/// </summary>
internal static class DecryptHttpsProbe
{
    public const string DefaultHost = "example.com";
    public static readonly Uri DefaultUri = new("https://example.com/");

    public sealed record Result(bool Ok, string Message, bool Inconclusive = false);

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

        var root = interception.RootCertificate;
        if (root is null)
            return new Result(false, "Root CA is not loaded");

        var probeHostSkipped = MitmBypass.ShouldDisableSslDecrypt(
            DefaultHost,
            interception.DecryptSkipHosts,
            interception.DecryptOnlyHosts);

        var captured = new ProbeCapture();
        void OnSession(object? _, SessionSnapshot snap) => captured.Remember(snap);

        interception.SessionCaptured += OnSession;
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                // Validate the forged leaf against this process's MITM root (not the OS store).
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    LeafChainsToMitmRoot(certificate, root),
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            Exception? requestError = null;
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(DefaultUri, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                requestError = ex;
            }
            catch (HttpRequestException ex)
            {
                requestError = ex;
            }
            catch (AuthenticationException ex)
            {
                requestError = ex;
            }

            while (ReadCaptured(captured) is null && sw.Elapsed < timeout)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            if (ReadCaptured(captured) is { } session)
                return new Result(true, "Decrypted session captured (" + Truncate(session.Url, 80) + ")");

            // Any HTTP response proves the proxy presented a MITM leaf the callback accepted
            // (including a proxy-generated 502). Missing SessionCaptured (paused capture) is OK.
            if (response is not null)
            {
                response.Dispose();
                return new Result(true, "Probe reached the proxy with a MITM leaf");
            }

            if (IsTlsAuthFailure(requestError))
            {
                if (probeHostSkipped)
                {
                    return new Result(false,
                        "Could not verify; example.com is on the decrypt-skip list",
                        Inconclusive: true);
                }

                return new Result(false,
                    "Probe request failed: " + Truncate(requestError!.Message, 120));
            }

            if (requestError is not null)
            {
                return new Result(false,
                    "Could not verify; " + Truncate(DescribeTransportFailure(requestError), 120),
                    Inconclusive: true);
            }

            return new Result(false,
                "Could not verify; no decrypted HTTPS session within " + (int)timeout.TotalSeconds + "s",
                Inconclusive: true);
        }
        finally
        {
            interception.SessionCaptured -= OnSession;
        }
    }

    /// <summary>
    ///     Builds a custom chain with the Inspector MITM root so SslPolicyErrors.UntrustedRoot
    ///     from the OS store does not fail the probe.
    /// </summary>
    internal static bool LeafChainsToMitmRoot(X509Certificate2? leaf, X509Certificate2 mitmRoot)
    {
        if (leaf is null)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags =
            X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.Add(mitmRoot);
        if (!chain.Build(leaf))
            return false;

        if (chain.ChainElements.Cast<X509ChainElement>().Any(element =>
                string.Equals(element.Certificate.Thumbprint, mitmRoot.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            return true;

        return string.Equals(leaf.Thumbprint, mitmRoot.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTlsAuthFailure(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AuthenticationException)
                return true;
            ex = ex.InnerException;
        }

        return false;
    }

    private static string DescribeTransportFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "example.com unreachable (timed out)";
        return "example.com unreachable (" + ex.GetType().Name + ")";
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
