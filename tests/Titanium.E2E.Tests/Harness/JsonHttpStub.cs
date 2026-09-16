using System.Net;
using System.Text;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Loopback HTTP stub that returns a fixed body for every request (JWKS / Consul / K8s).</summary>
public sealed class JsonHttpStub : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _body;
    private readonly string _contentType;
    private readonly int _statusCode;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public JsonHttpStub(string body, string contentType = "application/json", int statusCode = 200)
    {
        _body = Encoding.UTF8.GetBytes(body);
        _contentType = contentType;
        _statusCode = statusCode;
        (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignore
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    ctx.Response.StatusCode = _statusCode;
                    ctx.Response.ContentType = _contentType;
                    ctx.Response.ContentLength64 = _body.Length;
                    ctx.Response.OutputStream.Write(_body);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }, ct);
        }
    }
}

/// <summary>Origin that always returns 500 (for active-health / circuit tests).</summary>
public sealed class AlwaysFailOrigin : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _hits;

    public int Port { get; }
    public int Hits => Volatile.Read(ref _hits);

    public AlwaysFailOrigin()
    {
        (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignore
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    Interlocked.Increment(ref _hits);
                    var body = Encoding.UTF8.GetBytes("fail");
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }, ct);
        }
    }
}
