using System.Text.Json;

namespace Titanium.Plus.Manager;

/// <summary>
/// Parses the JSON body of <c>GET /v1/agent/config</c> (manager → agent). Malformed bytes must not
/// throw out of <see cref="TryParse"/>; the agent loop treats a false return as a soft failure.
/// </summary>
internal static class AgentConfigPayload
{
    internal sealed record Parsed(string ConfigHash, string Yaml, string RevisionId);

    /// <summary>SharpFuzz-style entry point.</summary>
    public static void Fuzz(byte[] data)
    {
        var violations = Check(data);
        if (violations.Count > 0)
        {
            throw new AgentConfigFuzzInvariantException(string.Join("; ", violations));
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

            var hash = ReadString(root, "configHash", "config_hash", "hash") ?? "";
            var yaml = ReadString(root, "yaml", "configYaml", "config_yaml");
            if (yaml is null && root.TryGetProperty("config", out var configEl))
            {
                yaml = configEl.ValueKind == JsonValueKind.String
                    ? TryGetString(configEl)
                    : configEl.GetRawText();
            }

            yaml ??= "";
            var revisionId = ReadString(root, "revisionId", "revision_id") ?? hash;
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(yaml))
            {
                error = "manager config response missing hash or yaml";
                return false;
            }

            parsed = new Parsed(hash, yaml, revisionId);
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

internal sealed class AgentConfigFuzzInvariantException(string message) : Exception(message);
