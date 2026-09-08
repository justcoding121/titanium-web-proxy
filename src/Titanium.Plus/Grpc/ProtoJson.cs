using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Titanium.Plus.Grpc;

/// <summary>Proto3-ish JSON bind/format for <see cref="DescriptorMessage"/> (MVP scalar/nested/repeated).</summary>
internal static class ProtoJson
{
    public static DescriptorMessage Parse(string json, MessageDescriptor descriptor, bool ignoreUnknown)
    {
        var message = new DescriptorMessage(descriptor);
        if (string.IsNullOrWhiteSpace(json))
            return message;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("JSON body must be an object.");

        BindObject(message, doc.RootElement, ignoreUnknown);
        return message;
    }

    public static string Format(DescriptorMessage message, bool preserveProtoFieldNames, bool alwaysPrintPrimitiveFields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, message, preserveProtoFieldNames, alwaysPrintPrimitiveFields);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void BindObject(DescriptorMessage message, JsonElement obj, bool ignoreUnknown)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var field = FindField(message.Descriptor, prop.Name);
            if (field is null)
            {
                if (!ignoreUnknown)
                    throw new InvalidOperationException($"Unknown JSON field '{prop.Name}'.");
                continue;
            }

            if (field.IsRepeated)
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"Field '{field.Name}' expects a JSON array.");

                var list = new List<object?>();
                foreach (var item in prop.Value.EnumerateArray())
                    list.Add(ReadValue(field, item, ignoreUnknown));
                message.Values[field.FieldNumber] = list;
            }
            else
            {
                message.Values[field.FieldNumber] = ReadValue(field, prop.Value, ignoreUnknown);
            }
        }
    }

    private static object? ReadValue(FieldDescriptor field, JsonElement el, bool ignoreUnknown) // NOSONAR S3776 -- Proto JSON scalar switch stays in one bind helper.
    {
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return field.FieldType switch
        {
            FieldType.String => el.GetString(),
            FieldType.Bool => el.GetBoolean(),
            FieldType.Bytes => ByteString.FromBase64(el.GetString() ?? string.Empty),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => el.ValueKind == JsonValueKind.String
                ? int.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetInt32(),
            FieldType.UInt32 or FieldType.Fixed32 => el.ValueKind == JsonValueKind.String
                ? uint.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetUInt32(),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => el.ValueKind == JsonValueKind.String
                ? long.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetInt64(),
            FieldType.UInt64 or FieldType.Fixed64 => el.ValueKind == JsonValueKind.String
                ? ulong.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetUInt64(),
            FieldType.Float => el.ValueKind == JsonValueKind.String
                ? float.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetSingle(),
            FieldType.Double => el.ValueKind == JsonValueKind.String
                ? double.Parse(el.GetString()!, CultureInfo.InvariantCulture)
                : el.GetDouble(),
            FieldType.Enum => ReadEnum(field, el),
            FieldType.Message or FieldType.Group => ReadMessage(field.MessageType, el, ignoreUnknown),
            _ => throw new NotSupportedException($"Field type {field.FieldType} is not supported.")
        };
    }

    private static int ReadEnum(FieldDescriptor field, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();

        var name = el.GetString() ?? string.Empty;
        var value = field.EnumType.FindValueByName(name);
        if (value is null)
            throw new InvalidOperationException($"Unknown enum value '{name}' for {field.EnumType.FullName}.");
        return value.Number;
    }

    private static DescriptorMessage ReadMessage(MessageDescriptor descriptor, JsonElement el, bool ignoreUnknown)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Expected object for message {descriptor.FullName}.");
        var nested = new DescriptorMessage(descriptor);
        BindObject(nested, el, ignoreUnknown);
        return nested;
    }

    private static void WriteObject( // NOSONAR S3776 -- Proto JSON format walks fields once with preserve-name/always-print flags.
        Utf8JsonWriter writer,
        DescriptorMessage message,
        bool preserveProtoFieldNames,
        bool alwaysPrintPrimitiveFields)
    {
        writer.WriteStartObject();
        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            var name = preserveProtoFieldNames ? field.Name : field.JsonName;
            if (!message.Values.TryGetValue(field.FieldNumber, out var val) || val is null)
            {
                if (alwaysPrintPrimitiveFields && !field.IsRepeated && field.FieldType != FieldType.Message)
                    WriteDefault(writer, name, field);
                continue;
            }

            if (field.IsRepeated && val is System.Collections.IList list)
            {
                writer.WritePropertyName(name);
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, field, item, preserveProtoFieldNames, alwaysPrintPrimitiveFields);
                writer.WriteEndArray();
                continue;
            }

            writer.WritePropertyName(name);
            WriteValue(writer, field, val, preserveProtoFieldNames, alwaysPrintPrimitiveFields);
        }

        writer.WriteEndObject();
    }

    private static void WriteDefault(Utf8JsonWriter writer, string name, FieldDescriptor field)
    {
        writer.WritePropertyName(name);
        switch (field.FieldType)
        {
            case FieldType.String:
                writer.WriteStringValue(string.Empty);
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue(false);
                break;
            case FieldType.Float:
            case FieldType.Double:
                writer.WriteNumberValue(0);
                break;
            case FieldType.Enum:
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
            case FieldType.UInt32:
            case FieldType.Fixed32:
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
            case FieldType.UInt64:
            case FieldType.Fixed64:
                writer.WriteNumberValue(0);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        FieldDescriptor field,
        object? val,
        bool preserveProtoFieldNames,
        bool alwaysPrintPrimitiveFields)
    {
        if (val is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (field.FieldType)
        {
            case FieldType.String:
                writer.WriteStringValue((string)val);
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue((bool)val);
                break;
            case FieldType.Bytes:
                writer.WriteStringValue(((ByteString)val).ToBase64());
                break;
            case FieldType.Float:
                writer.WriteNumberValue(Convert.ToSingle(val));
                break;
            case FieldType.Double:
                writer.WriteNumberValue(Convert.ToDouble(val));
                break;
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
            case FieldType.Enum:
                writer.WriteNumberValue(Convert.ToInt32(val));
                break;
            case FieldType.UInt32:
            case FieldType.Fixed32:
                writer.WriteNumberValue(Convert.ToUInt32(val));
                break;
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                writer.WriteStringValue(Convert.ToInt64(val).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.UInt64:
            case FieldType.Fixed64:
                writer.WriteStringValue(Convert.ToUInt64(val).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.Message:
            case FieldType.Group:
                WriteObject(writer, (DescriptorMessage)val, preserveProtoFieldNames, alwaysPrintPrimitiveFields);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static FieldDescriptor? FindField(MessageDescriptor descriptor, string jsonName) =>
        descriptor.FindFieldByName(jsonName) ??
        descriptor.Fields.InDeclarationOrder().FirstOrDefault(f =>
            string.Equals(f.JsonName, jsonName, StringComparison.Ordinal) ||
            string.Equals(f.Name, jsonName, StringComparison.Ordinal));
}
