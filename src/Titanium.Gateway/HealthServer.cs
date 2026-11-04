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

    public HealthServer(int port, Func<bool> ready, ILogger log)
    {
        _ready = ready;
        _log = log;
        // Controller health binds cleartext; kube probes expect http:// on the health port.
        _listener.Prefixes.Add($"http://+:{port}/"); // NOSONAR S5332 -- health endpoint is intentionally cleartext.
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _log.LogWarning(ex, "Health endpoint not started: {Message}", ex.Message);
            return;
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
}
