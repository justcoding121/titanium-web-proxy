using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Transforms;

/// <summary>Applies known transform kinds to request path/headers/query and staged response headers.</summary>
public sealed class TransformEngine : ITransformEngine
{
    private const string ValueParameter = "value";
    private const char Slash = '/';
    /// <summary>Extra kinds. Null (the default) does not allocate or change built-in transforms.</summary>
    public IReadOnlyList<IRouteTransformFactory>? Factories { get; init; }

    public void ApplyRequestTransforms(IReadOnlyList<TransformConfig>? transforms, TransformRequestContext context) // NOSONAR S3776 -- Transform kinds share the request context; splitting would hide apply order.
    {
        if (transforms is null || transforms.Count == 0)
        {
            return;
        }

        foreach (var t in transforms)
        {
            switch (t.Kind)
            {
                case TransformKinds.PathRemovePrefix when t.Parameters is not null &&
                                             t.Parameters.TryGetValue("prefix", out var prefix) &&
                                             context.Path.StartsWith(prefix, StringComparison.Ordinal):
                    context.Path = context.Path[prefix.Length..];
                    if (context.Path.Length == 0 || context.Path[0] != '/')
                    {
                        context.Path = string.Concat("/", context.Path); // NOSONAR S1075 -- origin-form path delimiter, not a URI.
                    }

                    break;
                case TransformKinds.PathPrefix when t.Parameters is not null &&
                                       t.Parameters.TryGetValue("prefix", out var addPrefix):
                    if (string.IsNullOrEmpty(addPrefix))
                    {
                        break;
                    }

                    if (!addPrefix.StartsWith(Slash))
                    {
                        addPrefix = Slash + addPrefix;
                    }

                    var rest = context.Path.StartsWith(Slash) ? context.Path : Slash + context.Path;
                    context.Path = addPrefix.TrimEnd('/') + rest;
                    break;
                case TransformKinds.RequestHeaderSet when t.Parameters is not null &&
                                             t.Parameters.TryGetValue("name", out var name) &&
                                             t.Parameters.TryGetValue(ValueParameter, out var value):
                    context.Headers[name] = value;
                    break;
                case TransformKinds.RequestHeaderRemove when t.Parameters is not null &&
                                                t.Parameters.TryGetValue("name", out var removeName):
                    context.Headers.Remove(removeName);
                    context.HeadersToRemove.Add(removeName);
                    break;
                case TransformKinds.QueryValueSet when t.Parameters is not null &&
                                          t.Parameters.TryGetValue("name", out var qName) &&
                                          t.Parameters.TryGetValue(ValueParameter, out var qValue):
                    context.Path = SetQueryValue(context.Path, qName, qValue);
                    break;
                case TransformKinds.RequestHostUseDestination:
                    context.UseDestinationHost = true;
                    break;
                case TransformKinds.ResponseHeaderSet when t.Parameters is not null &&
                                              t.Parameters.TryGetValue("name", out var rhName) &&
                                              t.Parameters.TryGetValue(ValueParameter, out var rhValue):
                    context.ResponseHeadersToSet[rhName] = rhValue;
                    // A later Set replaces whatever an earlier Append staged for the same name.
                    context.ResponseHeadersToAppend?.Remove(rhName);
                    break;
                case TransformKinds.ResponseHeaderRemove when t.Parameters is not null &&
                                                 t.Parameters.TryGetValue("name", out var rrName):
                    context.ResponseHeadersToRemove.Add(rrName);
                    // Config order wins: a Set/Append staged before this Remove must not resurrect the header.
                    context.ResponseHeadersToSet.Remove(rrName);
                    context.ResponseHeadersToAppend?.Remove(rrName);
                    break;
                case TransformKinds.PathSet when Param(t, "path") is { } setPath:
                    context.Path = setPath.StartsWith(Slash) ? setPath : Slash + setPath;
                    break;
                case TransformKinds.PathPattern when Param(t, "pattern") is { } pattern:
                    context.Path = pattern.Replace("{**remainder}", context.Path, StringComparison.Ordinal);
                    break;
                case TransformKinds.PathRegexReplace when Param(t, "pattern") is { } regex && Param(t, "replacement") is { } replacement:
                    context.Path = System.Text.RegularExpressions.Regex.Replace(
                        context.Path, regex, replacement, System.Text.RegularExpressions.RegexOptions.NonBacktracking);
                    break;
                case TransformKinds.HttpMethodChange when Param(t, "to") is { } toMethod:
                    var from = Param(t, "from");
                    if (from is null || from == "*" || string.Equals(from, context.Method, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Method = toMethod;
                    }

                    break;
                case TransformKinds.QueryValueAppend when Param(t, "name") is { } appendName && Param(t, ValueParameter) is { } appendValue:
                    context.Path = AppendQuery(context.Path, appendName, appendValue);
                    break;
                case TransformKinds.QueryRemove when Param(t, "name") is { } removeQuery:
                    context.Path = RemoveQuery(context.Path, removeQuery);
                    break;
                case TransformKinds.QueryRouteParameter when Param(t, "name") is { } routeQuery && Param(t, "routeValue") is { } routeKey &&
                    context.RouteValues.TryGetValue(routeKey, out var routeValue):
                    context.Path = SetQueryValue(context.Path, routeQuery, routeValue);
                    break;
                case TransformKinds.RequestHeaderAppend when Param(t, "name") is { } appendHeader && Param(t, ValueParameter) is { } appendHeaderValue:
                    context.Headers[appendHeader] = ExistingRequestValue(context, appendHeader) is { Length: > 0 } current
                        ? current + ", " + appendHeaderValue
                        : appendHeaderValue;
                    break;
                case TransformKinds.RequestHeadersAllowed when Param(t, "headers") is { } allowed:
                    KeepOnly(context, allowed);
                    break;
                case TransformKinds.ClientCertHeader when Param(t, "name") is { } certHeader:
                    // The header carries identity. A copy the client sent itself is never forwarded,
                    // whether or not a certificate was presented.
                    context.HeadersToRemove.Add(certHeader);
                    if (!string.IsNullOrEmpty(context.ClientCertificateSubject))
                    {
                        context.Headers[certHeader] = context.ClientCertificateSubject;
                    }
                    else
                    {
                        context.Headers.Remove(certHeader);
                    }

                    break;
                case TransformKinds.RequestHeaderOriginalHost:
                    if (!string.IsNullOrEmpty(context.OriginalHost))
                    {
                        context.Headers["X-Original-Host"] = context.OriginalHost;
                    }

                    break;
                case TransformKinds.ResponseHeaderAppend when Param(t, "name") is { } responseName && Param(t, ValueParameter) is { } responseValue:
                    if (context.ResponseHeadersToSet.TryGetValue(responseName, out var stagedResponse) && stagedResponse.Length > 0)
                    {
                        // Joins an earlier Set/Append of the same name; the staged entry keeps its own mode.
                        context.ResponseHeadersToSet[responseName] = stagedResponse + ", " + responseValue;
                    }
                    else
                    {
                        context.ResponseHeadersToSet[responseName] = responseValue;
                        (context.ResponseHeadersToAppend ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(responseName);
                    }

                    break;
                case TransformKinds.ResponseHeadersAllowed when Param(t, "headers") is { } responseAllowed:
                    context.ResponseHeadersToRemove.Add("*" + responseAllowed);
                    break;
                case TransformKinds.RequestTrailerSet when Param(t, "name") is { } requestTrailer && Param(t, ValueParameter) is { } requestTrailerValue:
                    context.RequestTrailers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    context.RequestTrailers[requestTrailer] = requestTrailerValue;
                    break;
                case TransformKinds.ResponseTrailerSet when Param(t, "name") is { } responseTrailer && Param(t, ValueParameter) is { } responseTrailerValue:
                    context.ResponseTrailers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    context.ResponseTrailers[responseTrailer] = responseTrailerValue;
                    break;
                default:
                    ApplyFactory(t, context);
                    break;
            }
        }
    }

    private void ApplyFactory(TransformConfig transform, TransformRequestContext context)
    {
        if (Factories is not { Count: > 0 } factories || string.IsNullOrEmpty(transform.Kind))
        {
            return;
        }

        for (var i = 0; i < factories.Count; i++)
        {
            if (factories[i].CanHandle(transform.Kind))
            {
                factories[i].Apply(transform, context);
                return;
            }
        }
    }

    /// <summary>
    ///     Value an append must extend: what an earlier transform staged, else what the client sent. When the
    ///     client's lines are merged they are also queued for removal, so the merged value replaces all of them.
    /// </summary>
    private static string? ExistingRequestValue(TransformRequestContext context, string name)
    {
        if (context.Headers.TryGetValue(name, out var staged))
        {
            return staged;
        }

        if (context.HeadersToRemove.Contains(name) || context.ExistingRequestHeader is not { } lookup)
        {
            return null;
        }

        var existing = lookup(name);
        if (!string.IsNullOrEmpty(existing))
        {
            context.HeadersToRemove.Add(name);
        }

        return existing;
    }

    private static string? Param(TransformConfig transform, string name) =>
        transform.Parameters is not null && transform.Parameters.TryGetValue(name, out var value) ? value : null;

    private static void KeepOnly(TransformRequestContext context, string allowedCsv)
    {
        var allowed = new HashSet<string>(allowedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        foreach (var name in context.Headers.Keys.Where(name => !allowed.Contains(name)))
        {
            context.HeadersToRemove.Add(name);
        }
    }

    private static string AppendQuery(string pathAndQuery, string name, string value)
    {
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return pathAndQuery + separator + name + "=" + Uri.EscapeDataString(value);
    }

    private static string RemoveQuery(string pathAndQuery, string name)
    {
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return pathAndQuery;
        }

        var path = pathAndQuery[..q];
        var parts = pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var eq = part.IndexOf('=');
                var key = eq >= 0 ? part[..eq] : part;
                return !string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
            });
        var query = string.Join("&", parts);
        return query.Length == 0 ? path : path + "?" + query;
    }

    private static string SetQueryValue(string pathAndQuery, string name, string value)
    {
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = q >= 0 ? pathAndQuery[..q] : pathAndQuery;
        var query = q >= 0 ? pathAndQuery[(q + 1)..] : "";
        var parts = string.IsNullOrEmpty(query)
            ? new List<string>()
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        var replaced = false;
        for (var i = 0; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            var key = eq >= 0 ? parts[i][..eq] : parts[i];
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = name + "=" + Uri.EscapeDataString(value);
                replaced = true;
            }
        }

        if (!replaced)
        {
            parts.Add(name + "=" + Uri.EscapeDataString(value));
        }

        return path + "?" + string.Join("&", parts);
    }
}
