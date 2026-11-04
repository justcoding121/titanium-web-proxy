using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Examples.RoutePlugin;

/// <summary>
/// Sample per-route plugin. It refuses a request that lacks one header and stamps an extra request header on the
/// ones it lets through. Wire it to a route in <c>twp.yaml</c>:
/// <code>
/// plugins:
///   - name: header-gate
///     assembly: ./plugins/Titanium.Web.Proxy.Examples.RoutePlugin.dll
///     settings:
///       requiredHeader: X-Api-Client
///       stampHeader: X-Route-Plugin
///     failMode: closed
///     timeoutMs: 200
/// routes:
///   - id: api
///     clusterId: backend
///     match: { path: /api, pathKind: Prefix }
///     middleware: [header-gate]
/// </code>
/// </summary>
[ProxyPlugin(ProxyPluginContract.CurrentVersion)]
public sealed class HeaderGatePlugin : IProxyMiddleware
{
    private readonly string _requiredHeader;
    private readonly string _stampHeader;

    /// <summary>The host passes the <c>settings</c> map of the plugin entry.</summary>
    public HeaderGatePlugin(IReadOnlyDictionary<string, string> settings)
    {
        _requiredHeader = settings.GetValueOrDefault("requiredHeader", "X-Api-Client");
        _stampHeader = settings.GetValueOrDefault("stampHeader", "X-Route-Plugin");
    }

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        // On the session path Session is the SessionEventArgs of the request.
        if (context.Session is not SessionEventArgs session)
        {
            return next(context, cancellationToken);
        }

        var headers = session.HttpClient.Request.Headers;
        if (!headers.HeaderExists(_requiredHeader))
        {
            // Setting these fields stops the route and makes the host answer with them.
            context.HandledStatusCode = 403;
            context.HandledBody = $"missing {_requiredHeader}";
            context.IsHandled = true;
            return ValueTask.CompletedTask;
        }

        headers.AddHeader(_stampHeader, "header-gate");
        return next(context, cancellationToken);
    }
}
