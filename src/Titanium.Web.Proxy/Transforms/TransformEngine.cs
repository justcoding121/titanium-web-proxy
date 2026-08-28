using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Transforms;

/// <summary>Applies known transform kinds (path prefix strip/set header).</summary>
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
                case "RequestHeaderSet" when t.Parameters is not null &&
                                             t.Parameters.TryGetValue("name", out var name) &&
                                             t.Parameters.TryGetValue("value", out var value):
                    context.Headers[name] = value;
                    break;
            }
        }
    }
}
