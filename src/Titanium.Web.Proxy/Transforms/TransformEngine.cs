using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Transforms;

/// <summary>Applies known transform kinds to request path/headers/query and staged response headers.</summary>
public sealed class TransformEngine : ITransformEngine
{
    public void ApplyRequestTransforms(IReadOnlyList<TransformConfig>? transforms, TransformRequestContext context)
    {
        if (transforms is null || transforms.Count == 0)
        {
            return;
        }

        foreach (var t in transforms)
        {
            switch (t.Kind)
            {
                case "PathRemovePrefix" when t.Parameters is not null &&
                                             t.Parameters.TryGetValue("prefix", out var prefix) &&
                                             context.Path.StartsWith(prefix, StringComparison.Ordinal):
                    context.Path = context.Path[prefix.Length..];
                    if (context.Path.Length == 0 || context.Path[0] != '/')
                    {
                        context.Path = string.Concat("/", context.Path); // NOSONAR S1075 -- origin-form path delimiter, not a URI.
                    }

                    break;
                case "PathPrefix" when t.Parameters is not null &&
                                       t.Parameters.TryGetValue("prefix", out var addPrefix):
                    if (string.IsNullOrEmpty(addPrefix))
                    {
                        break;
                    }

                    if (!addPrefix.StartsWith('/'))
                    {
                        addPrefix = "/" + addPrefix;
                    }

                    var rest = context.Path.StartsWith('/') ? context.Path : "/" + context.Path;
                    context.Path = addPrefix.TrimEnd('/') + rest;
                    break;
                case "RequestHeaderSet" when t.Parameters is not null &&
                                             t.Parameters.TryGetValue("name", out var name) &&
                                             t.Parameters.TryGetValue("value", out var value):
                    context.Headers[name] = value;
                    break;
                case "RequestHeaderRemove" when t.Parameters is not null &&
                                                t.Parameters.TryGetValue("name", out var removeName):
                    context.Headers.Remove(removeName);
                    context.HeadersToRemove.Add(removeName);
                    break;
                case "QueryValueSet" when t.Parameters is not null &&
                                          t.Parameters.TryGetValue("name", out var qName) &&
                                          t.Parameters.TryGetValue("value", out var qValue):
                    context.Path = SetQueryValue(context.Path, qName, qValue);
                    break;
                case "ResponseHeaderSet" when t.Parameters is not null &&
                                              t.Parameters.TryGetValue("name", out var rhName) &&
                                              t.Parameters.TryGetValue("value", out var rhValue):
                    context.ResponseHeadersToSet[rhName] = rhValue;
                    break;
                case "ResponseHeaderRemove" when t.Parameters is not null &&
                                                 t.Parameters.TryGetValue("name", out var rrName):
                    context.ResponseHeadersToRemove.Add(rrName);
                    break;
            }
        }
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
