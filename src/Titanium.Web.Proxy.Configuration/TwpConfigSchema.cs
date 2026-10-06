using System.Text.Json;
using System.Text.Json.Nodes;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>
///     JSON Schema for a native <c>twp.yaml</c> / <c>twp.json</c> document, reflected from the config model.
/// </summary>
public static class TwpConfigSchema
{
    private const string ObjectType = "object";
    private const string StringType = "string";

    public static string Generate()
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://titaniumproxy.com/twp.schema.json",
            ["title"] = "Titanium Web Proxy configuration",
            ["type"] = ObjectType,
            ["additionalProperties"] = false,
            ["properties"] = PropertiesOf(typeof(Models.TwpConfig), []),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static JsonObject PropertiesOf(Type type, HashSet<Type> stack)
    {
        var props = new JsonObject();
        foreach (var property in type.GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
                continue;
            props[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = SchemaFor(property.PropertyType, stack);
        }

        return props;
    }

    private static JsonObject SchemaFor(Type type, HashSet<Type> stack)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
            return new JsonObject { ["type"] = StringType };
        if (type == typeof(bool))
            return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return new JsonObject { ["type"] = "integer" };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        if (type.IsEnum)
            return new JsonObject { ["type"] = StringType };

        if (IsDictionary(type))
            return new JsonObject
            {
                ["type"] = ObjectType,
                ["additionalProperties"] = new JsonObject { ["type"] = StringType },
            };

        var item = ItemType(type);
        if (item is not null)
            return new JsonObject { ["type"] = "array", ["items"] = SchemaFor(item, stack) };

        if (!type.IsClass)
            return new JsonObject { ["type"] = StringType };

        if (!stack.Add(type))
            return new JsonObject { ["type"] = ObjectType };

        try
        {
            return new JsonObject
            {
                ["type"] = ObjectType,
                ["additionalProperties"] = false,
                ["properties"] = PropertiesOf(type, stack),
            };
        }
        finally
        {
            stack.Remove(type);
        }
    }

    private static bool IsDictionary(Type type) =>
        type.IsGenericType && (
            type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    private static Type? ItemType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (!type.IsGenericType)
            return null;
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(IList<>) || def == typeof(List<>) || def == typeof(IReadOnlyList<>) ||
            def == typeof(IEnumerable<>) || def == typeof(ICollection<>))
            return type.GetGenericArguments()[0];
        return null;
    }
}
