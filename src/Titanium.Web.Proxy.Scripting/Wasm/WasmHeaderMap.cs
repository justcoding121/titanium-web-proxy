using System.Buffers.Binary;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// A mutable header (or trailer, or metadata) map in proxy-wasm form: lower-case names, order kept, repeats allowed.
/// Remembers what it started as, so the host can turn the guest's edits into set and remove lists.
/// </summary>
internal sealed class WasmHeaderMap
{
    private readonly KeyValuePair<string, string>[] _original;

    public WasmHeaderMap(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        Pairs = pairs.Select(p => new KeyValuePair<string, string>(p.Key.ToLowerInvariant(), p.Value)).ToList();
        _original = Pairs.ToArray();
    }

    public List<KeyValuePair<string, string>> Pairs { get; private set; }

    public int Count => Pairs.Count;

    public string? Get(string name) =>
        Pairs.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public void Add(string name, string value) => Pairs.Add(new(name.ToLowerInvariant(), value));

    public void Remove(string name) => Pairs.RemoveAll(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

    public void Replace(string name, string value)
    {
        var index = Pairs.FindIndex(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
        Remove(name);
        var entry = new KeyValuePair<string, string>(name.ToLowerInvariant(), value);
        if (index < 0 || index > Pairs.Count)
        {
            Pairs.Add(entry);
        }
        else
        {
            Pairs.Insert(index, entry);
        }
    }

    public void SetAll(List<KeyValuePair<string, string>> pairs) =>
        Pairs = pairs.Select(p => new KeyValuePair<string, string>(p.Key.ToLowerInvariant(), p.Value)).ToList();

    /// <summary>
    /// Headers the guest added or changed (joined with <c>", "</c> when repeated) and headers it removed. Pseudo-headers
    /// are left out; <c>:authority</c> becomes <c>host</c>.
    /// </summary>
    public (Dictionary<string, string>? Set, List<string>? Remove) Diff()
    {
        var before = Group(_original);
        var after = Group(Pairs);
        Dictionary<string, string>? set = null;
        List<string>? remove = null;

        foreach (var (name, value) in after)
        {
            if (!before.TryGetValue(name, out var old) || !string.Equals(old, value, StringComparison.Ordinal))
            {
                (set ??= new(StringComparer.OrdinalIgnoreCase))[name] = value;
            }
        }

        foreach (var name in before.Keys.Where(key => !after.ContainsKey(key)))
        {
            (remove ??= []).Add(name);
        }

        return (set, remove);
    }

    public byte[] Serialize() => Serialize(Pairs);

    /// <summary>proxy-wasm map form: count, then (key length, value length) per pair, then each key and value NUL-terminated.</summary>
    public static byte[] Serialize(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var size = 4;
        foreach (var pair in pairs)
        {
            size += 8 + Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value) + 2;
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)pairs.Count);
        var lengths = 4;
        var data = 4 + pairs.Count * 8;
        foreach (var pair in pairs)
        {
            var key = Encoding.UTF8.GetBytes(pair.Key, span[data..]);
            span[data + key] = 0;
            data += key + 1;
            var value = Encoding.UTF8.GetBytes(pair.Value, span[data..]);
            span[data + value] = 0;
            data += value + 1;
            BinaryPrimitives.WriteUInt32LittleEndian(span[lengths..], (uint)key);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(lengths + 4)..], (uint)value);
            lengths += 8;
        }

        return buffer;
    }

    /// <summary>Parses proxy-wasm map form. Returns null when the bytes are malformed.</summary>
    public static List<KeyValuePair<string, string>>? Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        if (bytes.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > (bytes.Length - 4) / 8)
        {
            return null;
        }

        var result = new List<KeyValuePair<string, string>>((int)count);
        var data = 4 + (int)count * 8;
        for (var i = 0; i < count; i++)
        {
            var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 + i * 8)..]);
            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(8 + i * 8)..]);
            if ((long)data + keyLength + 1 + valueLength + 1 > bytes.Length)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(bytes.Slice(data, (int)keyLength));
            data += (int)keyLength + 1;
            var value = Encoding.UTF8.GetString(bytes.Slice(data, (int)valueLength));
            data += (int)valueLength + 1;
            result.Add(new(key, value));
        }

        return result;
    }

    private static Dictionary<string, string> Group(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var name = pair.Key;
            if (name.StartsWith(':'))
            {
                if (!string.Equals(name, ":authority", StringComparison.Ordinal))
                {
                    continue;
                }

                name = "host";
            }

            result[name] = result.TryGetValue(name, out var existing) ? existing + ", " + pair.Value : pair.Value;
        }

        return result;
    }
}
