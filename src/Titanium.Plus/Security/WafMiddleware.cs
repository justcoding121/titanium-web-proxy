using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Titanium.Plus;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

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
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

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

        AddPathDenyFromCsv(rules, options);
        AddMethodDenyFromCsv(rules, options);
        AddHeaderDeny(rules, options);
        AddRulesFromFile(rules, options);
        return rules;
    }

    private static void AddPathDenyFromCsv(WafRules rules, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("waf.denyPaths", out var paths) || string.IsNullOrWhiteSpace(paths))
        {
            return;
        }

        foreach (var part in paths.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            rules.PathDeny.Add(CompileRegex(part));
        }
    }

    private static void AddMethodDenyFromCsv(WafRules rules, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("waf.denyMethods", out var methods) || string.IsNullOrWhiteSpace(methods))
        {
            return;
        }

        foreach (var part in methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            rules.MethodDeny.Add(part);
        }
    }

    private static void AddHeaderDeny(WafRules rules, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("waf.denyHeader", out var headerRule) || string.IsNullOrWhiteSpace(headerRule))
        {
            return;
        }

        // format: HeaderName=regex
        var eq = headerRule.IndexOf('=');
        if (eq <= 0)
        {
            return;
        }

        var name = headerRule[..eq].Trim();
        var pattern = headerRule[(eq + 1)..].Trim();
        rules.HeaderDeny.Add((name, CompileRegex(pattern)));
    }

    private static void AddRulesFromFile(WafRules rules, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("waf.rulesFile", out var file) || string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("denyPaths", out var denyPaths))
            {
                return;
            }

            foreach (var el in denyPaths.EnumerateArray())
            {
                var p = el.GetString();
                if (!string.IsNullOrEmpty(p))
                {
                    rules.PathDeny.Add(CompileRegex(p));
                }
            }
        }
        catch
        {
            // ignore bad rules file; operator can fix
        }
    }

    private static Regex CompileRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            matchTimeout: RegexMatchTimeout);
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
        if (TryGetSessionRequest(context, out var sessionRequest))
        {
            if (_rules.MethodDeny.Contains(sessionRequest!.Method))
            {
                Deny(context);
                return;
            }

            var path = sessionRequest.RequestUri?.AbsolutePath ?? sessionRequest.RequestUriString ?? "";
            if (_rules.PathDeny.Any(regex => regex.IsMatch(path)))
            {
                Deny(context);
                return;
            }

            if (HeaderDenied(sessionRequest))
            {
                Deny(context);
                return;
            }

            if (sessionRequest.ContentLength > _rules.MaxBodyBytes)
            {
                Deny(context);
                return;
            }
        }
        else if (context.Request is { } view)
        {
            if (_rules.MethodDeny.Contains(view.Method))
            {
                Deny(context);
                return;
            }

            if (_rules.PathDeny.Any(regex => regex.IsMatch(view.Path)))
            {
                Deny(context);
                return;
            }

            if (HeaderDenied(view))
            {
                Deny(context);
                return;
            }

            if (view.ContentLength > _rules.MaxBodyBytes)
            {
                Deny(context);
                return;
            }
        }
        else
        {
            await next(context, cancellationToken);
            return;
        }

        await next(context, cancellationToken);
    }

    private static bool TryGetSessionRequest(ProxyMiddlewareContext context, out Request? request)
    {
        if (context.Session is SessionEventArgsBase args)
        {
            request = args.HttpClient.Request;
            return true;
        }

        request = null;
        return false;
    }

    private bool HeaderDenied(Request request) =>
        _rules.HeaderDeny.Any(rule =>
        {
            var values = request.Headers.GetHeaders(rule.Header);
            return values is not null && values.Any(h => rule.Value.IsMatch(h.Value));
        });

    private bool HeaderDenied(MiddlewareRequestView request) =>
        _rules.HeaderDeny.Any(rule =>
        {
            var values = request.GetHeaderValues?.Invoke(rule.Header);
            return values is not null && values.Any(v => rule.Value.IsMatch(v));
        });

    private void Deny(ProxyMiddlewareContext context)
    {
        _logger?.LogInformation("Plus WAF: request denied");
        CidrAccessMiddleware.Deny(context, HttpStatusCode.Forbidden, "forbidden");
    }
}
