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
            var schema = SchemaFor(property.PropertyType, stack);
            ApplyKnownConstraints(schema, property);
            props[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = schema;
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
            var obj = new JsonObject
            {
                ["type"] = ObjectType,
                ["additionalProperties"] = false,
            };
            if (type == typeof(Models.MasqueCredentialConfig))
            {
                obj["required"] = new JsonArray { "username", "passwordEnv" };
            }

            obj["properties"] = PropertiesOf(type, stack);
            return obj;
        }
        finally
        {
            stack.Remove(type);
        }
    }

    /// <summary>
    ///     Bounds and enums the validator enforces. Reflection only sees the CLR type, so these
    ///     are applied by declaring type and property name before the schema file is compared.
    /// </summary>
    private static void ApplyKnownConstraints(JsonObject schema, System.Reflection.PropertyInfo property)
    {
        switch (property.DeclaringType?.Name + "." + property.Name)
        {
            case "ClusterHttpClientConfig.MaxConnectionsPerServer":
                schema["description"] =
                    "Hard cap on open upstream connections per destination, including idle pooled sockets. Waiters time out after the connect timeout with 503 and Retry-After.";
                break;
            case "ListenerConfig.ClientCertificateMode":
                schema["enum"] = new JsonArray { "none", "allow", "require" };
                break;
            case "MasqueListenerConfig.MaxBytesPerSecond":
                schema["minimum"] = 1;
                schema["maximum"] = 1_073_741_824;
                break;
            case "MasqueListenerConfig.MaxDatagramsPerSecond":
                schema["minimum"] = 1;
                schema["maximum"] = 1_000_000;
                break;
            case "MasqueListenerConfig.MaxTunnels":
            case "MasqueListenerConfig.MaxTunnelsPerClient":
                schema["minimum"] = 1;
                break;
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
