using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Titanium.Gateway.Kube;

/// <summary>JSON settings shared by the readers and writers of Kubernetes objects.</summary>
public static class GatewayJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new Rfc3339Converter());
        return options;
    }

    /// <summary>Kubernetes <c>metav1.Time</c>: RFC 3339 in UTC with whole seconds.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Kubernetes <c>metav1.MicroTime</c>, used by Lease.</summary>
    public static string FormatMicroTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private sealed class Rfc3339Converter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatTime(value));
    }
}

/// <summary>Small readers over <see cref="JsonElement"/> that return null instead of throwing on a missing path.</summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Get(this JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Null ? null : current;
    }

    public static string? GetString(this JsonElement element, params string[] path)
    {
        var value = element.Get(path);
        return value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    public static int? GetInt(this JsonElement element, params string[] path)
    {
        var value = element.Get(path);
        return value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;
    }

    public static bool? GetBool(this JsonElement element, params string[] path)
    {
        var value = element.Get(path);
        return value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
    }

    public static IEnumerable<JsonElement> GetArray(this JsonElement element, params string[] path)
    {
        var value = element.Get(path);
        return value is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : [];
    }
}
