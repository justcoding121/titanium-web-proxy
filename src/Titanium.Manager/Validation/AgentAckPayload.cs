using System.Text;
using System.Text.Json;

namespace Titanium.Manager.Validation;

/// <summary>
/// Parses the JSON body of <c>POST /v1/agent/ack</c>. Malformed input must not throw; callers get
/// <see cref="TryParse"/> false and an error string (for fuzz targets and the HTTP handler).
/// </summary>
internal static class AgentAckPayload
{
    internal sealed record Parsed(
        long? Fleet,
        string? NodeId,
        string? ConfigHash,
        bool Ok,
        string? Error,
        string? HealthJson,
        string? LabelsJson);

    /// <summary>SharpFuzz-style entry: throws <see cref="AgentAckFuzzInvariantException"/> only on an invariant break.</summary>
    public static void Fuzz(byte[] data)
    {
        var violations = Check(data);
        if (violations.Count > 0)
        {
            throw new AgentAckFuzzInvariantException(string.Join("; ", violations));
        }
    }

    public static IReadOnlyList<string> Check(byte[] data)
    {
        var violations = new List<string>();
        try
        {
            _ = TryParse(data, out _, out _);
        }
        catch (Exception ex)
        {
            violations.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        return violations;
    }

    public static bool TryParse(byte[] utf8, out Parsed? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (utf8 is null || utf8.Length == 0)
        {
            error = "empty body";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root must be object";
                return false;
            }

            long? fleet = null;
            if (root.TryGetProperty("fleet", out var f))
            {
                if (f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var n))
                {
                    fleet = n;
                }
                else if (f.ValueKind == JsonValueKind.String &&
                         long.TryParse(TryGetString(f), out var p))
                {
                    fleet = p;
                }
                else if (f.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    error = "fleet must be number or string";
                    return false;
                }
            }

            var nodeId = ReadString(root, "node_id", "nodeId");
            var configHash = ReadString(root, "config_hash", "configHash");
            var ok = !root.TryGetProperty("ok", out var o) || o.ValueKind != JsonValueKind.False;
            var err = ReadString(root, "error");

            string? healthJson = null;
            if (root.TryGetProperty("health", out var health))
            {
                healthJson = health.ValueKind is JsonValueKind.String
                    ? TryGetString(health)
                    : health.GetRawText();
            }

            string? labelsJson = null;
            if (root.TryGetProperty("labels", out var labels))
            {
                labelsJson = labels.ValueKind is JsonValueKind.String
                    ? TryGetString(labels)
                    : labels.GetRawText();
            }
            else if (root.TryGetProperty("labelsJson", out var labelsJsonEl) &&
                     labelsJsonEl.ValueKind == JsonValueKind.String)
            {
                labelsJson = TryGetString(labelsJsonEl);
            }

            parsed = new Parsed(fleet, nodeId, configHash, ok, err, healthJson, labelsJson);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            error = "json: " + ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                return TryGetString(el);
            }
        }

        return null;
    }

    /// <summary>JsonDocument can throw on invalid UTF-8 inside an otherwise well-formed token.</summary>
    private static string? TryGetString(JsonElement el)
    {
        try
        {
            return el.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed class AgentAckFuzzInvariantException(string message) : Exception(message);

/// <summary>Fuzz entry for revision YAML accepted by the manager store.</summary>
internal static class RevisionYamlFuzzTarget
{
    public static void Yaml(byte[] data)
    {
        var violations = Check(data);
        if (violations.Count > 0)
        {
            throw new AgentAckFuzzInvariantException(string.Join("; ", violations));
        }
    }

    public static IReadOnlyList<string> Check(byte[] data)
    {
        var violations = new List<string>();
        try
        {
            // Lossy decode is fine for the invariant: ValidateYaml must never throw.
            var text = data is null || data.Length == 0
                ? ""
                : Encoding.UTF8.GetString(data);
            _ = ConfigRevisionValidator.ValidateYaml(text);
        }
        catch (Exception ex)
        {
            violations.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        return violations;
    }
}
