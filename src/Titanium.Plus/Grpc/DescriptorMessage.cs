using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Titanium.Plus.Grpc;

/// <summary>Minimal dynamic <see cref="IMessage"/> backed by a <see cref="MessageDescriptor"/> (no codegen).</summary>
internal sealed class DescriptorMessage : IMessage
{
    public Dictionary<int, object?> Values { get; } = new();
    public MessageDescriptor Descriptor { get; }

    public DescriptorMessage(MessageDescriptor descriptor) => Descriptor = descriptor;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public int CalculateSize()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return (int)ms.Length;
    }

    public void WriteTo(CodedOutputStream output)
    {
        foreach (var field in Descriptor.Fields.InFieldNumberOrder())
        {
            if (!Values.TryGetValue(field.FieldNumber, out var val) || val is null)
                continue;

            if (field.IsRepeated && val is System.Collections.IList list)
            {
                foreach (var item in list)
                    WriteField(output, field, item);
                continue;
            }

            WriteField(output, field, val);
        }
    }

    private static void WriteField(CodedOutputStream output, FieldDescriptor field, object? val)
    {
        if (val is null) return;

        switch (field.FieldType)
        {
            case FieldType.String:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString((string)val);
                break;
            case FieldType.Bytes:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes((ByteString)val);
                break;
            case FieldType.Bool:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteBool((bool)val);
                break;
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt32(Convert.ToInt32(val));
                break;
            case FieldType.UInt32:
            case FieldType.Fixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt32(Convert.ToUInt32(val));
                break;
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt64(Convert.ToInt64(val));
                break;
            case FieldType.UInt64:
            case FieldType.Fixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt64(Convert.ToUInt64(val));
                break;
            case FieldType.Float:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFloat(Convert.ToSingle(val));
                break;
            case FieldType.Double:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteDouble(Convert.ToDouble(val));
                break;
            case FieldType.Enum:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteEnum(Convert.ToInt32(val));
                break;
            case FieldType.Message:
            case FieldType.Group:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteMessage((IMessage)val);
                break;
        }
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            var field = Descriptor.FindFieldByNumber(number);
            if (field is null)
            {
                input.SkipLastField();
                continue;
            }

            var value = ReadField(input, field);
            if (field.IsRepeated)
            {
                if (!Values.TryGetValue(number, out var existing) || existing is not System.Collections.IList list)
                {
                    list = new List<object?>();
                    Values[number] = list;
                }

                list.Add(value);
            }
            else
            {
                Values[number] = value;
            }
        }
    }

    private static object? ReadField(CodedInputStream input, FieldDescriptor field) =>
        field.FieldType switch
        {
            FieldType.String => input.ReadString(),
            FieldType.Bytes => input.ReadBytes(),
            FieldType.Bool => input.ReadBool(),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => input.ReadInt32(),
            FieldType.UInt32 or FieldType.Fixed32 => input.ReadUInt32(),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => input.ReadInt64(),
            FieldType.UInt64 or FieldType.Fixed64 => input.ReadUInt64(),
            FieldType.Float => input.ReadFloat(),
            FieldType.Double => input.ReadDouble(),
            FieldType.Enum => input.ReadEnum(),
            FieldType.Message or FieldType.Group => ReadMessage(input, field.MessageType),
            _ => Skip(input)
        };

    private static DescriptorMessage ReadMessage(CodedInputStream input, MessageDescriptor descriptor)
    {
        var nested = new DescriptorMessage(descriptor);
        input.ReadMessage(nested);
        return nested;
    }

    private static object? Skip(CodedInputStream input)
    {
        input.SkipLastField();
        return null;
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }

    public void MergeFrom(byte[] data) => MergeFrom(new CodedInputStream(data));
}
