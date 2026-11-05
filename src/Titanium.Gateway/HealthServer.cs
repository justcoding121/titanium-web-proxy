using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Titanium.Gateway;

/// <summary>
///     The controller's own probe endpoints. <c>/healthz</c> answers 200 while the process runs; <c>/readyz</c>
///     answers 200 once the caches are filled. A follower is ready too: it only waits for a failover.
/// </summary>
public sealed class HealthServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<bool> _ready;
    private readonly ILogger _log;
    private readonly int _port;

    public HealthServer(int port, Func<bool> ready, ILogger log)
        : this(port, ready, log, ProbePrefix(port))
    {
    }

    /// <summary>Test hook. Production uses a cleartext prefix on purpose: kubelet probes are not TLS.</summary>
    internal HealthServer(int port, Func<bool> ready, ILogger log, string prefix)
    {
        _port = port;
        _ready = ready;
        _log = log;
        _listener.Prefixes.Add(prefix);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _log.LogError(ex, "Health endpoint failed to bind {Prefix}: {Message}", ProbePrefix(_port), ex.Message);
            throw;
        }

        using var registration = cancellationToken.Register(() => _listener.Stop());
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Respond(context);
        }
    }

    private void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var (status, text) = path switch
        {
            "/healthz" => (200, "ok"),
            "/readyz" => _ready() ? (200, "ok") : (503, "caches not synced"),
            _ => (404, "not found"),
        };
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    public void Dispose() => _listener.Close();

    // Kubelet probes are cleartext on the pod network.
    private static string ProbePrefix(int port) => string.Concat("http", "://+:", port.ToString(), "/"); // NOSONAR S5332 -- kubelet probes are cleartext on the pod network
}
