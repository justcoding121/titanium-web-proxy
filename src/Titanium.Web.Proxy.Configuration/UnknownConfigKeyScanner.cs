using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>
///     Finds keys the native config model does not declare. Config load warns; <c>titanium test --strict</c> fails.
/// </summary>
public static class UnknownConfigKeyScanner
{
    public static IReadOnlyList<string> Scan(string text, bool yaml)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        JsonElement root;
        if (yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var graph = deserializer.Deserialize<object>(text);
            if (graph is null) return [];
            root = JsonDocument.Parse(ToJson(graph)).RootElement;
        }
        else
        {
            root = JsonDocument.Parse(text).RootElement;
        }

        if (root.ValueKind != JsonValueKind.Object) return [];
        var found = new List<string>();
        Walk(root, typeof(TwpConfig), "", found);
        return found;
    }

    private static void Walk(JsonElement element, Type type, string path, List<string> found)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var props = Properties(type);
        foreach (var property in element.EnumerateObject())
            Visit(property, path, props, found);
    }

    private static void Visit(JsonProperty property, string path, Dictionary<string, Type> props, List<string> found)
    {
        var childPath = path.Length == 0 ? property.Name : path + "." + property.Name;
        if (!props.TryGetValue(property.Name, out var clr))
        {
            found.Add(UnknownKeyMessage(property.Name, childPath, path, props.Keys));
            return;
        }

        VisitKnown(property.Value, clr, childPath, found);
    }

    private static string UnknownKeyMessage(string name, string childPath, string path, IEnumerable<string> keys)
    {
        var suggestion = Suggest(name, keys);
        if (suggestion is null)
            return $"Unknown key '{childPath}'.";

        var hinted = path.Length == 0 ? suggestion : path + "." + suggestion;
        return $"Unknown key '{childPath}'. Did you mean '{hinted}'?";
    }

    private static void VisitKnown(JsonElement value, Type clr, string childPath, List<string> found)
    {
        var next = Nullable.GetUnderlyingType(clr) ?? clr;
        if (value.ValueKind == JsonValueKind.Object && !IsDictionary(next))
        {
            Walk(value, next, childPath, found);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return;

        var item = ItemType(next);
        if (item == null || IsDictionary(item))
            return;

        foreach (var entry in value.EnumerateArray())
            Walk(entry, item, childPath, found);
    }

    private static Dictionary<string, Type> Properties(Type type)
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.GetProperties())
        {
            if (property.GetMethod is null || property.GetMethod.GetParameters().Length > 0) continue;
            map[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.PropertyType;
        }

        return map;
    }

    private static bool IsDictionary(Type type) =>
        type.IsGenericType && (
            type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    private static Type? ItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (!type.IsGenericType) return null;
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(IList<>) || def == typeof(List<>) || def == typeof(IReadOnlyList<>) ||
            def == typeof(IEnumerable<>) || def == typeof(ICollection<>))
            return type.GetGenericArguments()[0];
        return null;
    }

    private static string? Suggest(string key, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var candidate in candidates)
        {
            var distance = Distance(key, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int Distance(string left, string right)
    {
        var a = left.AsSpan();
        var b = right.AsSpan();
        var prev = new int[b.Length + 1];
        var next = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            next[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, next) = (next, prev);
        }

        return prev[b.Length];
    }

    private static string ToJson(object? value) => JsonSerializer.Serialize(Normalize(value));

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => map.ToDictionary(
            pair => pair.Key?.ToString() ?? "",
            pair => Normalize(pair.Value),
            StringComparer.Ordinal),
        IList<object> list => list.Select(Normalize).ToList(),
        _ => value
    };
}
