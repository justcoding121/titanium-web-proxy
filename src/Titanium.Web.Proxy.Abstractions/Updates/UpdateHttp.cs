using System.Net;
using System.Net.Http;

namespace Titanium.Web.Proxy.Abstractions.Updates;

/// <summary>
/// HttpClient factory for product self-update. Honors corporate proxies, but can
/// bypass a resolved proxy (Inspector's own MITM listener, or any loopback proxy for CLI)
/// so update checks are not decrypted by the app under test.
/// </summary>
public static class UpdateHttp
{
    /// <summary>
    /// Create an update client. When <paramref name="bypassResolvedProxy"/> returns true for the
    /// proxy URI chosen by the system/default proxy for a destination, that request goes direct.
    /// </summary>
    public static HttpClient CreateClient(
        TimeSpan timeout,
        Func<Uri?, bool>? bypassResolvedProxy = null)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new SelectiveBypassWebProxy(HttpClient.DefaultProxy, bypassResolvedProxy),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Like <see cref="CreateClient"/> with loopback-aware selective bypass for
    /// <paramref name="url"/>. Keeps the default proxy for remote destinations
    /// (corporate HTTPS_PROXY) and returns the destination from
    /// <see cref="SelectiveBypassWebProxy.GetProxy"/> for loopback feeds so
    /// product-smoke update checks are not MITM'd by a local proxy listener.
    /// Musl alpine smoke failures that looked like "HttpClient still honors
    /// HTTPS_PROXY" were caused by the harness staging PREV and candidate zips
    /// under one basename (fixed); this API keeps SelectiveBypass because it is
    /// the path covered by unit tests and the green musl update proof.
    /// </summary>
    public static HttpClient CreateClientForUrl(
        TimeSpan timeout,
        string? url,
        Func<Uri?, bool>? bypassResolvedProxy = null)
    {
        // url is retained for call-site clarity and future diagnostics; bypass is
        // driven by destination IsLoopback inside SelectiveBypassWebProxy.
        _ = url;
        return CreateClient(timeout, bypassResolvedProxy ?? IsLoopbackProxy);
    }

    /// <summary>True when the resolved proxy URI points at a loopback host.</summary>
    public static bool IsLoopbackProxy(Uri? proxyUri)
    {
        if (proxyUri is null)
        {
            return false;
        }

        if (proxyUri.IsLoopback)
        {
            return true;
        }

        var host = proxyUri.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || host is "127.0.0.1" or "::1" or "[::1]";
    }

    /// <summary>
    /// True when <paramref name="proxyUri"/> is this process's Inspector listener
    /// (<paramref name="boundPort"/> on loopback while capture/system-proxy is active).
    /// </summary>
    public static bool IsOwnLoopbackProxy(Uri? proxyUri, int boundPort, bool active)
    {
        if (!active || boundPort <= 0 || proxyUri is null)
        {
            return false;
        }

        if (!IsLoopbackProxy(proxyUri))
        {
            return false;
        }

        return proxyUri.Port == boundPort;
    }

    /// <summary>Human-readable update failure including inner exceptions and SSL hints.</summary>
    public static string FormatFailure(Exception ex, bool updateTrafficMayBeCaptured)
    {
        var parts = new List<string> { ex.Message };
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            parts.Add($"{inner.GetType().Name}: {inner.Message}");
        }

        var text = string.Join(" | ", parts);
        if (updateTrafficMayBeCaptured)
        {
            text += " (update traffic may be going through this app's proxy — decrypt can break the download)";
        }

        return text;
    }

    private sealed class SelectiveBypassWebProxy : IWebProxy
    {
        private readonly IWebProxy _inner;
        private readonly Func<Uri?, bool>? _bypassResolved;

        public SelectiveBypassWebProxy(IWebProxy? inner, Func<Uri?, bool>? bypassResolved)
        {
            _inner = inner ?? new WebProxy();
            _bypassResolved = bypassResolved;
        }

        public ICredentials? Credentials
        {
            get => _inner.Credentials;
            set => _inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination)
        {
            // Returning the destination URI means "go direct" for stacks that
            // consult GetProxy before IsBypassed (observed on musl HttpClient).
            if (destination.IsLoopback || IsLoopbackProxy(destination))
            {
                return destination;
            }

            return _inner.GetProxy(destination);
        }

        public bool IsBypassed(Uri host)
        {
            // Loopback feeds must never traverse a proxy — the CLI/Inspector smoke
            // recorder is itself on loopback and would black-hole the request.
            if (host.IsLoopback || IsLoopbackProxy(host))
            {
                return true;
            }

            if (_inner.IsBypassed(host))
            {
                return true;
            }

            if (_bypassResolved is null)
            {
                return false;
            }

            Uri? resolved;
            try
            {
                resolved = _inner.GetProxy(host);
            }
            catch
            {
                return false;
            }

            // DefaultProxy returns the destination itself when there is no proxy.
            if (resolved is null || resolved == host)
            {
                return true;
            }

            return _bypassResolved(resolved);
        }
    }
}
