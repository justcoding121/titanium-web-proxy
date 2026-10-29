using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// Writes the tagged format <c>twp_codec.c</c> decodes: <c>n f t</c>, <c>i</c> int64, <c>d</c> double, <c>s</c> u32 length +
/// bytes, <c>T</c> u32 pair count + pairs. Tables are written with a placeholder count patched by <see cref="EndTable"/>.
/// </summary>
internal sealed class LuaWriter
{
    private const int MaxDepth = 30;
    private byte[] _buffer;
    private int _length;

    public LuaWriter(int capacity = 256) => _buffer = new byte[capacity];

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => Written.ToArray();

    public void Nil() => Byte((byte)'n');

    public void Bool(bool value) => Byte(value ? (byte)'t' : (byte)'f');

    public void Int(long value)
    {
        Ensure(9);
        _buffer[_length++] = (byte)'i';
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void Double(double value)
    {
        Ensure(9);
        _buffer[_length++] = (byte)'d';
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void String(string? value)
    {
        if (value is null)
        {
            Nil();
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        Ensure(5 + count);
        _buffer[_length++] = (byte)'s';
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), (uint)count);
        _length += 4;
        _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(5 + value.Length);
        _buffer[_length++] = (byte)'s';
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), (uint)value.Length);
        _length += 4;
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Copies an already-encoded value (for example <c>ctx.vars</c> from an earlier phase).</summary>
    public void Raw(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            Nil();
            return;
        }

        Ensure(encoded.Length);
        encoded.CopyTo(_buffer.AsSpan(_length));
        _length += encoded.Length;
    }

    /// <summary>Starts a table; returns the mark <see cref="EndTable"/> needs.</summary>
    public int BeginTable()
    {
        Ensure(5);
        _buffer[_length++] = (byte)'T';
        var mark = _length;
        _length += 4;
        return mark;
    }

    public void EndTable(int mark, int pairs) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(mark), (uint)pairs);

    public void Field(string key, string? value)
    {
        String(key);
        String(value);
    }

    public void Field(string key, long value)
    {
        String(key);
        Int(value);
    }

    public void Field(string key, bool value)
    {
        String(key);
        Bool(value);
    }

    public void Field(string key, ReadOnlySpan<byte> value)
    {
        String(key);
        Bytes(value);
    }

    /// <summary>Writes a decoded value back (round trip of <see cref="LuaReader.Read()"/>) or a plain CLR value.</summary>
    public void Value(object? value, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("value nested too deeply for a script");
        }

        switch (value)
        {
            case null:
                Nil();
                break;
            case bool b:
                Bool(b);
                break;
            case long l:
                Int(l);
                break;
            case int i:
                Int(i);
                break;
            case double d:
                Double(d);
                break;
            case string s:
                String(s);
                break;
            case byte[] bytes:
                Bytes(bytes);
                break;
            case LuaTable table:
            {
                var mark = BeginTable();
                var pairs = 0;
                for (var i = 0; i < table.Array.Count; i++)
                {
                    if (table.Array[i] is null)
                    {
                        continue;
                    }

                    Int(i + 1);
                    Value(table.Array[i], depth + 1);
                    pairs++;
                }

                foreach (var pair in table.Fields)
                {
                    String(pair.Key);
                    Value(pair.Value, depth + 1);
                    pairs++;
                }

                EndTable(mark, pairs);
                break;
            }
            default:
                String(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private void Byte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        var size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>A decoded Lua table: the <c>1..n</c> part in <see cref="Array"/>, string keys in <see cref="Fields"/>.</summary>
internal sealed class LuaTable
{
    public List<object?> Array { get; } = [];

    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

    public object? this[string key] => Fields.TryGetValue(key, out var value) ? value : null;

    public object? this[int index] => index >= 1 && index <= Array.Count ? Array[index - 1] : null;

    public string? Str(string key) => LuaReader.AsString(this[key]);

    public long? Int(string key) => LuaReader.AsInt(this[key]);

    public LuaTable? Table(string key) => this[key] as LuaTable;

    public byte[]? Bytes(string key) => this[key] as byte[];
}

/// <summary>Reads the tagged format. Strings come back as <c>byte[]</c> (Lua strings are bytes); see <see cref="AsString"/>.</summary>
internal ref struct LuaReader
{
    private const int MaxDepth = 32;
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public LuaReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public int Position => _position;

    public object? Read() => Read(0);

    /// <summary>Skips one value and returns its encoded bytes.</summary>
    public ReadOnlySpan<byte> ReadRaw()
    {
        var start = _position;
        Skip(0);
        return _data[start.._position];
    }

    /// <summary>Starts reading a table: returns its pair count, or -1 when the value is not a table (and skips it).</summary>
    public int ReadTableHeader()
    {
        Need(1);
        if (_data[_position] != 'T')
        {
            Skip(0);
            return -1;
        }

        Need(5);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position + 1));
        _position += 5;
        return count;
    }

    private object? Read(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("script value nested too deeply");
        }

        Need(1);
        var tag = _data[_position++];
        switch (tag)
        {
            case (byte)'n':
                return null;
            case (byte)'f':
                return false;
            case (byte)'t':
                return true;
            case (byte)'i':
                Need(8);
                var l = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position));
                _position += 8;
                return l;
            case (byte)'d':
                Need(8);
                var d = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_position));
                _position += 8;
                return d;
            case (byte)'s':
            {
                Need(4);
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
                _position += 4;
                Need(n);
                var bytes = _data.Slice(_position, n).ToArray();
                _position += n;
                return bytes;
            }
            case (byte)'T':
            {
                Need(4);
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
                _position += 4;
                var table = new LuaTable();
                var indexed = new SortedDictionary<long, object?>();
                for (var i = 0; i < n; i++)
                {
                    var key = Read(depth + 1);
                    var value = Read(depth + 1);
                    switch (key)
                    {
                        case byte[] s:
                            table.Fields[Encoding.UTF8.GetString(s)] = value;
                            break;
                        case long k when k >= 1 && k <= 65_536:
                            indexed[k] = value;
                            break;
                        case double k when k >= 1 && k <= 65_536 && k == Math.Floor(k):
                            indexed[(long)k] = value;
                            break;
                    }
                }

                foreach (var pair in indexed)
                {
                    while (table.Array.Count < pair.Key - 1)
                    {
                        table.Array.Add(null);
                    }

                    table.Array.Add(pair.Value);
                }

                return table;
            }
            default:
                throw new FormatException($"unknown script value tag {tag}");
        }
    }

    private void Skip(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("script value nested too deeply");
        }

        Need(1);
        var tag = _data[_position++];
        switch (tag)
        {
            case (byte)'n' or (byte)'f' or (byte)'t':
                return;
            case (byte)'i' or (byte)'d':
                Need(8);
                _position += 8;
                return;
            case (byte)'s':
            {
                Need(4);
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
                _position += 4;
                Need(n);
                _position += n;
                return;
            }
            case (byte)'T':
            {
                Need(4);
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
                _position += 4;
                for (var i = 0; i < n; i++)
                {
                    Skip(depth + 1);
                    Skip(depth + 1);
                }

                return;
            }
            default:
                throw new FormatException($"unknown script value tag {tag}");
        }
    }

    private readonly void Need(int count)
    {
        if (count < 0 || _position > _data.Length - count)
        {
            throw new FormatException("truncated script value");
        }
    }

    public static string? AsString(object? value) => value switch
    {
        null => null,
        byte[] b => Encoding.UTF8.GetString(b),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => null,
    };

    public static long? AsInt(object? value) => value switch
    {
        long l => l,
        double d when d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue => (long)d,
        byte[] b when long.TryParse(Encoding.UTF8.GetString(b), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
