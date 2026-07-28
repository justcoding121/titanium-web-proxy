using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 SETTINGS parameter identifiers (RFC 9114 §7.2.4 + §7.2.4.1, plus QPACK extension RFC 9204).
/// </summary>
internal static class Http3SettingsId
{
    /// <summary>Deprecated in HTTP/3 (was SETTINGS_HEADER_TABLE_SIZE in HTTP/2). MUST be 0 if present.</summary>
    public const ulong HeaderTableSize = 0x1;

    /// <summary>Deprecated in HTTP/3 (was SETTINGS_ENABLE_PUSH in HTTP/2). MUST NOT be present.</summary>
    public const ulong EnablePush = 0x2;

    /// <summary>Deprecated in HTTP/3. MUST NOT be present.</summary>
    public const ulong MaxConcurrentStreams = 0x3;

    /// <summary>Deprecated in HTTP/3. MUST NOT be present.</summary>
    public const ulong InitialWindowSize = 0x4;

    /// <summary>Deprecated in HTTP/3. MUST NOT be present.</summary>
    public const ulong MaxFrameSize = 0x5;

    /// <summary>
    ///     SETTINGS_MAX_FIELD_SECTION_SIZE: advisory limit on the maximum size of an uncompressed field section.
    ///     Mirrors HTTP/2 SETTINGS_MAX_HEADER_LIST_SIZE.
    /// </summary>
    public const ulong MaxFieldSectionSize = 0x6;

    /// <summary>QPACK: maximum number of entries in the dynamic table. Default 0 (no dynamic table).</summary>
    public const ulong QpackMaxTableCapacity = 0x1;  // reuses 0x1 in QPACK namespace (draft-ietf-quic-qpack)

    /// <summary>QPACK: maximum number of blocked streams. Default 0.</summary>
    public const ulong QpackBlockedStreams = 0x7;
}

/// <summary>
///     A parsed SETTINGS frame body — a list of (id, value) pairs.
/// </summary>
internal sealed class Http3Settings
{
    private readonly List<(ulong Id, ulong Value)> _parameters = new();

    /// <summary>
    ///     The raw (id, value) pairs as parsed from the wire. Unknown IDs must be ignored (RFC 9114 §7.2.4).
    /// </summary>
    public IReadOnlyList<(ulong Id, ulong Value)> Parameters => _parameters;

    /// <summary>SETTINGS_MAX_FIELD_SECTION_SIZE (0x6). 0 means no limit was sent.</summary>
    public ulong MaxFieldSectionSize { get; private set; }

    public void Add(ulong id, ulong value)
    {
        _parameters.Add((id, value));
        if (id == Http3SettingsId.MaxFieldSectionSize)
            MaxFieldSectionSize = value;
    }

    /// <summary>
    ///     Parses a SETTINGS frame payload.
    /// </summary>
    public static Http3Settings Parse(ReadOnlySpan<byte> payload)
    {
        var settings = new Http3Settings();
        var remaining = payload;
        while (!remaining.IsEmpty)
        {
            if (!Http3VarInt.TryRead(remaining, out var id, out var idLen)) break;
            remaining = remaining[idLen..];
            if (!Http3VarInt.TryRead(remaining, out var value, out var valueLen)) break;
            remaining = remaining[valueLen..];
            settings.Add(id, value);
        }
        return settings;
    }

    /// <summary>
    ///     Serializes this settings object into a SETTINGS frame payload byte array.
    /// </summary>
    public byte[] Serialize()
    {
        // Each pair: up to 8 bytes for id + 8 bytes for value.
        var buf = new byte[_parameters.Count * 16];
        var offset = 0;
        foreach (var (id, value) in _parameters)
        {
            offset += Http3VarInt.Write(buf.AsSpan(offset), id);
            offset += Http3VarInt.Write(buf.AsSpan(offset), value);
        }
        return buf[..offset];
    }
}
