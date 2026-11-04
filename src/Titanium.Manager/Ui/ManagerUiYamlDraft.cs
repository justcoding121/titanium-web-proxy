using System.Globalization;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Titanium.Manager.Ui;

internal sealed record DraftRoute(string Id, string? Path, string? ClusterId);

internal sealed record DraftCluster(string Id, int DestinationCount);

internal sealed record DraftSummary(
    IReadOnlyList<DraftRoute> Routes,
    IReadOnlyList<DraftCluster> Clusters);

/// <summary>
///     Parses and patches fleet draft YAML for the structured editor helpers (routes/clusters stubs).
///     Round-trips through a YAML object graph so unrelated keys are preserved when possible.
/// </summary>
internal static class ManagerUiYamlDraft
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public const string EmptyDraft = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    public static DraftSummary Summarize(string yaml)
    {
        var root = ParseRoot(yaml);
        var routes = ReadList(root, "routes")
            .Select(AsMap)
            .Where(m => m is not null)
            .Select(m => new DraftRoute(
                ReadString(m!, "id") ?? "",
                ReadPath(m!),
                ReadString(m!, "clusterId") ?? ReadString(m!, "cluster_id")))
            .Where(r => r.Id.Length > 0)
            .ToList();

        var clusters = ReadList(root, "clusters")
            .Select(AsMap)
            .Where(m => m is not null)
            .Select(m =>
            {
                var destinations = ReadList(m!, "destinations");
                return new DraftCluster(ReadString(m!, "id") ?? "", destinations.Count);
            })
            .Where(c => c.Id.Length > 0)
            .ToList();

        return new DraftSummary(routes, clusters);
    }

    public static string UpsertRoute(string yaml, string id, string? path, string? clusterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var root = ParseRoot(yaml);
        var routes = EnsureList(root, "routes");
        Dictionary<object, object>? existing = null;
        foreach (var item in routes)
        {
            var map = AsMap(item);
            if (map is not null &&
                string.Equals(ReadString(map, "id"), id, StringComparison.Ordinal))
            {
                existing = map;
                break;
            }
        }

        if (existing is null)
        {
            existing = new Dictionary<object, object>
            {
                ["id"] = id,
            };
            routes.Add(existing);
        }

        if (!string.IsNullOrWhiteSpace(clusterId))
        {
            existing["clusterId"] = clusterId.Trim();
        }

        if (path is not null)
        {
            var match = AsMap(existing.TryGetValue("match", out var m) ? m : null)
                        ?? new Dictionary<object, object>();
            match["path"] = path;
            if (!match.ContainsKey("pathKind") && !match.ContainsKey("path_kind"))
            {
                match["pathKind"] = "Prefix";
            }

            existing["match"] = match;
        }

        return Serialize(root);
    }

    public static string RemoveRoute(string yaml, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var root = ParseRoot(yaml);
        var routes = EnsureList(root, "routes");
        routes.RemoveAll(item =>
        {
            var map = AsMap(item);
            return map is not null && string.Equals(ReadString(map, "id"), id, StringComparison.Ordinal);
        });
        return Serialize(root);
    }

    public static string UpsertCluster(string yaml, string id, string? destinationAddress = "127.0.0.1", int destinationPort = 8080)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var root = ParseRoot(yaml);
        var clusters = EnsureList(root, "clusters");
        foreach (var item in clusters)
        {
            var map = AsMap(item);
            if (map is not null && string.Equals(ReadString(map, "id"), id, StringComparison.Ordinal))
            {
                return Serialize(root);
            }
        }

        var destId = id + "-1";
        clusters.Add(new Dictionary<object, object>
        {
            ["id"] = id.Trim(),
            ["destinations"] = new List<object>
            {
                new Dictionary<object, object>
                {
                    ["id"] = destId,
                    ["address"] = string.IsNullOrWhiteSpace(destinationAddress) ? "127.0.0.1" : destinationAddress.Trim(),
                    ["port"] = destinationPort,
                },
            },
        });
        return Serialize(root);
    }

    public static string RemoveCluster(string yaml, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var root = ParseRoot(yaml);
        var clusters = EnsureList(root, "clusters");
        clusters.RemoveAll(item =>
        {
            var map = AsMap(item);
            return map is not null && string.Equals(ReadString(map, "id"), id, StringComparison.Ordinal);
        });
        return Serialize(root);
    }

    private static Dictionary<object, object> ParseRoot(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            yaml = EmptyDraft;
        }

        object? raw;
        try
        {
            raw = Deserializer.Deserialize<object>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("YAML parse error: " + ex.Message, ex);
        }

        if (raw is Dictionary<object, object> map)
        {
            return map;
        }

        if (raw is Dictionary<string, object> stringMap)
        {
            return CopyStringMap(stringMap);
        }

        return new Dictionary<object, object>();
    }

    private static string Serialize(Dictionary<object, object> root)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb, CultureInfo.InvariantCulture);
        Serializer.Serialize(writer, root);
        return sb.ToString();
    }

    private static List<object> EnsureList(Dictionary<object, object> root, string key)
    {
        if (root.TryGetValue(key, out var existing) && existing is List<object> list)
        {
            return list;
        }

        if (existing is IList<object> ilist)
        {
            var copy = ilist.ToList();
            root[key] = copy;
            return copy;
        }

        var created = new List<object>();
        root[key] = created;
        return created;
    }

    private static List<object> ReadList(Dictionary<object, object> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is List<object> list)
        {
            return list;
        }

        if (value is IEnumerable<object> enumerable)
        {
            return enumerable.ToList();
        }

        return [];
    }

    private static Dictionary<object, object>? AsMap(object? value) => value switch
    {
        Dictionary<object, object> map => map,
        Dictionary<string, object> stringMap => CopyStringMap(stringMap),
        _ => null,
    };

    private static Dictionary<object, object> CopyStringMap(Dictionary<string, object> stringMap)
    {
        var map = new Dictionary<object, object>();
        foreach (var kv in stringMap)
        {
            map[kv.Key] = kv.Value;
        }

        return map;
    }

    private static string? ReadString(Dictionary<object, object> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? ReadPath(Dictionary<object, object> route)
    {
        var match = AsMap(route.TryGetValue("match", out var m) ? m : null);
        if (match is null)
        {
            return null;
        }

        return ReadString(match, "path") ?? ReadString(match, "pathPrefix");
    }
}
