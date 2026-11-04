using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Minimal OTLP/HTTP stub that counts trace export POSTs.</summary>
internal sealed class OtlpStubCollector : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private int _traceExports;

    public OtlpStubCollector()
    {
        Port = CliProcessHarness.GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}";

    public int TraceExportCount => Volatile.Read(ref _traceExports);

    public async Task WaitForExportsAsync(int minimum, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TraceExportCount >= minimum)
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"Expected at least {minimum} OTLP trace export(s); got {TraceExportCount}.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            /* ignore */
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_stop.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _traceExports);
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }
}
