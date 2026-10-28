using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>Loads native <see cref="TwpConfig"/> from YAML or JSON files/strings.</summary>
public static class TwpConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // YamlDotNet cannot fill IReadOnlyList<T> / IReadOnlyDictionary<K,V> on its own; the cluster and route models use
    // them for read-only shapes (init-only), so map each to its concrete collection. JSON already handles them.
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeMapping<IReadOnlyList<DestinationConfig>, List<DestinationConfig>>()
        .WithTypeMapping<IReadOnlyList<WeightedCluster>, List<WeightedCluster>>()
        .WithTypeMapping<IReadOnlyList<TransformConfig>, List<TransformConfig>>()
        .WithTypeMapping<IReadOnlyList<ErrorPageConfig>, List<ErrorPageConfig>>()
        .WithTypeMapping<IReadOnlyList<QueryParameterMatch>, List<QueryParameterMatch>>()
        .WithTypeMapping<IReadOnlyList<string>, List<string>>()
        .WithTypeMapping<IReadOnlyList<int>, List<int>>()
        .WithTypeMapping<IReadOnlyDictionary<string, string>, Dictionary<string, string>>()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Detects format from extension (.yaml/.yml → YAML, else JSON).</summary>
    public static TwpConfig LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = File.ReadAllText(path);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return LoadYaml(text);
        }

        return LoadJson(text);
    }

    public static TwpConfig LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<TwpConfig>(json, JsonOptions)
               ?? throw new InvalidOperationException("Configuration JSON deserialized to null.");
    }

    public static TwpConfig LoadYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return YamlDeserializer.Deserialize<TwpConfig>(yaml)
               ?? throw new InvalidOperationException("Configuration YAML deserialized to null.");
    }

    public static string ToJson(TwpConfig config) =>
        JsonSerializer.Serialize(config, JsonOptions);
}
