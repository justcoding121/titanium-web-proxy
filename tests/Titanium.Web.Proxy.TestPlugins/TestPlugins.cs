using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.TestPlugins;

/// <summary>Appends a line to the file named by <c>marker</c> so a test can observe calls and disposal from outside the plugin's load context.</summary>
internal static class Marker
{
    public static void Write(IReadOnlyDictionary<string, string> settings, string line)
    {
        if (settings.TryGetValue("marker", out var path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}

/// <summary>Stamps request headers, remembers nothing between requests except its own call count, and reports disposal.</summary>
[ProxyPlugin(1)]
public sealed class RecordingPlugin : IProxyMiddleware, IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _settings;
    private int _calls;

    public RecordingPlugin(IReadOnlyDictionary<string, string> settings) => _settings = settings;

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        Marker.Write(_settings, "invoke");
        var calls = Interlocked.Increment(ref _calls);
        var session = (SessionEventArgs)context.Session;
        var headers = session.HttpClient.Request.Headers;
        headers.AddHeader("X-Plugin-Ran", _settings.GetValueOrDefault("name", "recording"));
        headers.AddHeader("X-Plugin-Calls", calls.ToString());

        // Per-request state lives in Items. A new request must never see an earlier request's entry.
        if (context.Items.ContainsKey("seen"))
        {
            headers.AddHeader("X-Plugin-Leak", "yes");
        }

        context.Items["seen"] = true;
        return next(context, cancellationToken);
    }

    public void Dispose() => Marker.Write(_settings, "disposed");
}

/// <summary>Throws on every call, either before or after it calls <c>next</c>.</summary>
[ProxyPlugin(1)]
public sealed class ThrowingPlugin : IProxyMiddleware
{
    private readonly bool _afterNext;

    public ThrowingPlugin(IReadOnlyDictionary<string, string> settings) =>
        _afterNext = settings.GetValueOrDefault("afterNext") == "true";

    public async ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        if (_afterNext)
        {
            await next(context, cancellationToken);
        }

        throw new InvalidOperationException("plugin failure");
    }
}

/// <summary>Takes <c>delayMs</c> to answer, with or without watching the cancellation token.</summary>
[ProxyPlugin(1)]
public sealed class SlowPlugin : IProxyMiddleware
{
    private readonly int _delayMs;
    private readonly bool _honorCancellation;

    public SlowPlugin(IReadOnlyDictionary<string, string> settings)
    {
        _delayMs = int.Parse(settings.GetValueOrDefault("delayMs", "5000"));
        _honorCancellation = settings.GetValueOrDefault("honorCancellation") == "true";
    }

    public async ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        await Task.Delay(_delayMs, _honorCancellation ? cancellationToken : CancellationToken.None);

        // A plugin that finishes after the host gave up must not be able to change the request.
        ((SessionEventArgs)context.Session).HttpClient.Request.Headers.AddHeader("X-Late-Plugin", "ran");
        context.IsHandled = true;
        await next(context, cancellationToken);
    }
}

/// <summary>Answers 418 with a header and body through the context fields.</summary>
[ProxyPlugin(1)]
public sealed class DenyPlugin : IProxyMiddleware
{
    public DenyPlugin()
    {
    }

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        context.HandledStatusCode = 418;
        context.HandledBody = "teapot";
        context.HandledHeaders = [new("X-Denied-By", "deny-plugin")];
        context.IsHandled = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Neither calls <c>next</c> nor handles the request.</summary>
[ProxyPlugin(1)]
public sealed class SilentPlugin : IProxyMiddleware
{
    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>Declares a contract version no host supports yet.</summary>
[ProxyPlugin(99)]
public sealed class FutureContractPlugin : IProxyMiddleware
{
    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
        next(context, cancellationToken);
}

/// <summary>Has no usable constructor.</summary>
public sealed class NoConstructorPlugin : IProxyMiddleware
{
    public NoConstructorPlugin(int required)
    {
        _ = required;
    }

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
        next(context, cancellationToken);
}

/// <summary>Throws from its constructor.</summary>
public sealed class FaultyConstructorPlugin : IProxyMiddleware
{
    public FaultyConstructorPlugin() => throw new InvalidOperationException("constructor failure");

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
        next(context, cancellationToken);
}

/// <summary>Not a plugin: it does not implement the contract.</summary>
public sealed class NotAPlugin
{
}
