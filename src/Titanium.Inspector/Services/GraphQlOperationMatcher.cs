using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Extracts GraphQL operationName from a JSON request body (tools matching only).</summary>
public static class GraphQlOperationMatcher
{
    /// <summary>
    /// Tries to read <c>operationName</c> from a GraphQL JSON body.
    /// Returns false when body is not GraphQL JSON or operationName is absent/null.
    /// </summary>
    public static bool TryGetOperationName(string? body, out string? operationName)
    {
        operationName = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("operationName", out var op) ||
                op.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                // Fallback: first word after query/mutation/subscription in "query" field.
                if (doc.RootElement.TryGetProperty("query", out var queryEl) &&
                    queryEl.ValueKind == JsonValueKind.String &&
                    TryParseOperationFromQuery(queryEl.GetString(), out operationName))
                {
                    return true;
                }

                return false;
            }

            if (op.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            operationName = op.GetString();
            return !string.IsNullOrWhiteSpace(operationName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="requiredOperation"/> is empty or equals the body's operationName.</summary>
    public static bool MatchesOperation(string? body, string? requiredOperation)
    {
        if (string.IsNullOrWhiteSpace(requiredOperation))
        {
            return true;
        }

        if (!TryGetOperationName(body, out var name) || name is null)
        {
            return false;
        }

        return string.Equals(name, requiredOperation.Trim(), StringComparison.Ordinal);
    }

    private static bool TryParseOperationFromQuery(string? query, out string? name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i] is "query" or "mutation" or "subscription")
            {
                var candidate = tokens[i + 1].Trim().TrimEnd('(', '{');
                if (candidate.Length > 0 && candidate is not "{" and not "(")
                {
                    name = candidate;
                    return true;
                }
            }
        }

        return false;
    }
}
