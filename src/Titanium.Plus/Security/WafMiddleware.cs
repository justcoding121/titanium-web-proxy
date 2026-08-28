using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Titanium.Plus;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Plus.Security;

/// <summary>Registers thin deny-list WAF middleware when <c>waf.enabled</c> is true.</summary>
public sealed class WafGuard
{
    public static WafGuard? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("waf.enabled", out var enabled) ||
            (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) && enabled != "1"))
        {
            return null;
        }

        if (context.Middleware is null)
        {
            PlusLog.Warn(context, "Plus WAF: Middleware list is null — not registered.");
            return new WafGuard();
        }

        var rules = WafRules.FromOptions(options);
        context.Middleware.Add(new WafDenyMiddleware(rules, context.Logger));
        PlusLog.Info(context,
            $"Plus WAF: enabled (pathRules={rules.PathDeny.Count}, headerRules={rules.HeaderDeny.Count}, maxBody={rules.MaxBodyBytes}).");
        return new WafGuard();
    }
}

/// <summary>Deny-list rules for path/method/header/body size (not ModSecurity/CRS).</summary>
public sealed class WafRules
{
    public List<Regex> PathDeny { get; } = [];
    public List<(string Header, Regex Value)> HeaderDeny { get; } = [];
    public HashSet<string> MethodDeny { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long MaxBodyBytes { get; init; } = 10 * 1024 * 1024;

    public static WafRules FromOptions(IReadOnlyDictionary<string, string> options)
    {
        var rules = new WafRules
        {
            MaxBodyBytes = long.TryParse(options.GetValueOrDefault("waf.maxBodyBytes"), out var max)
                ? max
                : 10 * 1024 * 1024,
        };

        if (options.TryGetValue("waf.denyPaths", out var paths) && !string.IsNullOrWhiteSpace(paths))
        {
            foreach (var part in paths.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                rules.PathDeny.Add(new Regex(part, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
        }

        if (options.TryGetValue("waf.denyMethods", out var methods) && !string.IsNullOrWhiteSpace(methods))
        {
            foreach (var part in methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                rules.MethodDeny.Add(part);
            }
        }

        if (options.TryGetValue("waf.denyHeader", out var headerRule) && !string.IsNullOrWhiteSpace(headerRule))
        {
            // format: HeaderName=regex
            var eq = headerRule.IndexOf('=');
            if (eq > 0)
            {
                var name = headerRule[..eq].Trim();
                var pattern = headerRule[(eq + 1)..].Trim();
                rules.HeaderDeny.Add((name, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)));
            }
        }

        if (options.TryGetValue("waf.rulesFile", out var file) && !string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("denyPaths", out var denyPaths))
                {
                    foreach (var el in denyPaths.EnumerateArray())
                    {
                        var p = el.GetString();
                        if (!string.IsNullOrEmpty(p))
                        {
                            rules.PathDeny.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
                        }
                    }
                }
            }
            catch
            {
                // ignore bad rules file; operator can fix
            }
        }

        return rules;
    }
}

/// <summary>Thin WAF deny middleware.</summary>
public sealed class WafDenyMiddleware : IProxyMiddleware
{
    private readonly WafRules _rules;
    private readonly ILogger? _logger;

    public WafDenyMiddleware(WafRules rules, ILogger? logger = null)
    {
        _rules = rules;
        _logger = logger;
    }

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (context.Session is not SessionEventArgsBase args)
        {
            await next(context, cancellationToken);
            return;
        }

        var request = args.HttpClient.Request;
        if (_rules.MethodDeny.Contains(request.Method))
        {
            Deny(context);
            return;
        }

        var path = request.RequestUri?.AbsolutePath ?? request.RequestUriString ?? "";
        foreach (var regex in _rules.PathDeny)
        {
            if (regex.IsMatch(path))
            {
                Deny(context);
                return;
            }
        }

        foreach (var (header, valueRegex) in _rules.HeaderDeny)
        {
            var values = request.Headers.GetHeaders(header);
            if (values is null)
            {
                continue;
            }

            foreach (var h in values)
            {
                if (valueRegex.IsMatch(h.Value))
                {
                    Deny(context);
                    return;
                }
            }
        }

        if (request.ContentLength > _rules.MaxBodyBytes)
        {
            Deny(context);
            return;
        }

        await next(context, cancellationToken);
    }

    private void Deny(ProxyMiddlewareContext context)
    {
        _logger?.LogInformation("Plus WAF: request denied");
        CidrAccessMiddleware.Deny(context, HttpStatusCode.Forbidden, "forbidden");
    }
}
