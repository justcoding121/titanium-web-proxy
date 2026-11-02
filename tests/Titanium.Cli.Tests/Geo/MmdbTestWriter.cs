using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Titanium.Cli.Tests.Geo;

/// <summary>Minimal MaxMind DB writer for unit tests (country.iso_code maps only).</summary>
internal static class MmdbTestWriter
{
    private const int RecordSizeBits = 24;
    private const int RecordBytes = RecordSizeBits / 8;

    public static void Write(Stream output, IReadOnlyList<(IPAddress Network, int PrefixLength, string CountryIsoCode)> networks)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (networks.Count == 0)
        {
            throw new ArgumentException("At least one network is required.", nameof(networks));
        }

        var dataSection = new MemoryStream();
        var dataOffsets = new List<int>(networks.Count);
        foreach (var (_, _, iso) in networks)
        {
            dataOffsets.Add((int)dataSection.Length);
            WriteCountryRecord(dataSection, iso);
        }

        var nodeCount = BuildTree(networks, dataOffsets, out var treeBytes);
        output.Write(treeBytes);
        output.Write(new byte[16]);
        output.Write(dataSection.ToArray());
        WriteMetadata(output, nodeCount);
    }

    private static int BuildTree(
        IReadOnlyList<(IPAddress Network, int PrefixLength, string CountryIsoCode)> networks,
        List<int> dataOffsets,
        out byte[] treeBytes)
    {
        var nodes = new List<(uint Left, uint Right)> { (0, 0) };
        for (var i = 0; i < networks.Count; i++)
        {
            InsertNetwork(nodes, networks[i].Network, networks[i].PrefixLength, (uint)dataOffsets[i]);
        }

        var nodeCount = nodes.Count;
        using var tree = new MemoryStream(nodes.Count * RecordBytes * 2);
        foreach (var (left, right) in nodes)
        {
            WriteRecord(tree, left);
            WriteRecord(tree, right);
        }

        treeBytes = tree.ToArray();
        return nodeCount;
    }

    private static void InsertNetwork(List<(uint Left, uint Right)> nodes, IPAddress network, int prefixLength, uint dataOffset)
    {
        var bytes = network.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new NotSupportedException("Only IPv4 prefixes are supported in this test writer.");
        }

        var nodeIndex = 0;
        for (var bit = 0; bit < prefixLength; bit++)
        {
            var goRight = GetBit(bytes, bit);
            var (left, right) = nodes[nodeIndex];
            if (bit == prefixLength - 1)
            {
                var pointer = EncodeDataPointer(dataOffset, nodes.Count);
                if (goRight)
                {
                    right = pointer;
                }
                else
                {
                    left = pointer;
                }

                nodes[nodeIndex] = (left, right);
                return;
            }

            ref var slot = ref goRight ? ref right : ref left;
            if (slot == 0 || slot >= EncodeDataPointer(0, nodes.Count))
            {
                slot = (uint)nodes.Count;
                nodes.Add((0, 0));
            }

            nodes[nodeIndex] = (left, right);
            nodeIndex = (int)slot;
        }
    }

    private static bool GetBit(byte[] bytes, int bitIndex)
    {
        var byteIndex = bitIndex / 8;
        var bitInByte = 7 - (bitIndex % 8);
        return (bytes[byteIndex] & (1 << bitInByte)) != 0;
    }

    private static uint EncodeDataPointer(uint dataOffset, int nodeCount) => (uint)(nodeCount + 16 + dataOffset);

    private static void WriteRecord(MemoryStream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        buf.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf[^RecordBytes..]);
    }

    private static void WriteCountryRecord(MemoryStream stream, string isoCode)
    {
        var iso = isoCode.ToUpperInvariant();
        WriteMapHeader(stream, 1);
        WriteUtf8String(stream, "country");
        WriteMapHeader(stream, 1);
        WriteUtf8String(stream, "iso_code");
        WriteUtf8String(stream, iso);
    }

    private static void WriteMapHeader(MemoryStream stream, int pairs)
    {
        if (pairs is < 0 or > 28)
        {
            throw new NotSupportedException("This test writer supports maps with at most 28 pairs.");
        }

        stream.WriteByte((byte)((7 << 5) | pairs));
    }

    private static void WriteUtf8String(MemoryStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 28)
        {
            throw new NotSupportedException("This test writer supports short UTF-8 strings only.");
        }

        stream.WriteByte((byte)((2 << 5) | bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUint32(MemoryStream stream, uint value)
    {
        stream.WriteByte((byte)((6 << 5) | 4));
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteMetadata(Stream output, int nodeCount)
    {
        var meta = new MemoryStream();
        WriteMapHeader(meta, 7);
        WriteUtf8String(meta, "node_count");
        WriteUint32(meta, (uint)nodeCount);
        WriteUtf8String(meta, "record_size");
        WriteUint32(meta, (uint)RecordSizeBits);
        WriteUtf8String(meta, "ip_version");
        WriteUint32(meta, 4);
        WriteUtf8String(meta, "database_type");
        WriteUtf8String(meta, "Titanium-Test");
        WriteUtf8String(meta, "binary_format_major_version");
        WriteUint32(meta, 2);
        WriteUtf8String(meta, "binary_format_minor_version");
        WriteUint32(meta, 0);
        WriteUtf8String(meta, "build_epoch");
        WriteUint32(meta, 1_700_000_000);
        WriteUtf8String(meta, "description");
        WriteMapHeader(meta, 1);
        WriteUtf8String(meta, "en");
        WriteUtf8String(meta, "Titanium test database");
        output.Write(meta.ToArray());
        output.WriteByte(0xAB);
        output.WriteByte(0xCD);
        output.WriteByte(0xEF);
        output.Write(Encoding.ASCII.GetBytes("MaxMind.com"));
    }
}
