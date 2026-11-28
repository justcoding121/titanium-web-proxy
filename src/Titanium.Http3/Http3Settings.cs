using System;
using System.Collections.Generic;

namespace Titanium.Http3;

/// <summary>
/// HTTP/3 SETTINGS parameter identifiers (RFC 9114 §7.2.4 and the QPACK settings in RFC 9204).
/// </summary>
public static class Http3SettingsId
{
    /// <summary>Deprecated in HTTP/3 (was SETTINGS_HEADER_TABLE_SIZE in HTTP/2). Must be 0 if present.</summary>
    public const ulong HeaderTableSize = 0x1;

    /// <summary>Deprecated in HTTP/3 (was SETTINGS_ENABLE_PUSH in HTTP/2). Must not be present.</summary>
    public const ulong EnablePush = 0x2;

    /// <summary>Deprecated in HTTP/3. Must not be present.</summary>
    public const ulong MaxConcurrentStreams = 0x3;

    /// <summary>Deprecated in HTTP/3. Must not be present.</summary>
    public const ulong InitialWindowSize = 0x4;

    /// <summary>Deprecated in HTTP/3. Must not be present.</summary>
    public const ulong MaxFrameSize = 0x5;

    /// <summary>SETTINGS_MAX_FIELD_SECTION_SIZE. Advisory limit on an uncompressed field section.</summary>
    public const ulong MaxFieldSectionSize = 0x6;

    /// <summary>QPACK maximum dynamic-table capacity. Default 0 means no dynamic table.</summary>
    public const ulong QpackMaxTableCapacity = 0x1;

    /// <summary>QPACK maximum number of blocked streams. Default 0.</summary>
    public const ulong QpackBlockedStreams = 0x7;

    /// <summary>SETTINGS_ENABLE_CONNECT_PROTOCOL.</summary>
    public const ulong EnableConnectProtocol = 0x8;
}

/// <summary>A parsed SETTINGS frame body: a list of identifier and value pairs.</summary>
public sealed class Http3Settings
{
    private readonly List<(ulong Id, ulong Value)> parameters = new();

    /// <summary>The raw pairs from the wire. Unknown identifiers are kept and ignored by the known-field setters.</summary>
    public IReadOnlyList<(ulong Id, ulong Value)> Parameters => parameters;

    /// <summary>SETTINGS_MAX_FIELD_SECTION_SIZE (0x6). 0 means no limit was sent.</summary>
    public ulong MaxFieldSectionSize { get; private set; }

    /// <summary>SETTINGS_QPACK_MAX_TABLE_CAPACITY (0x1). 0 means no dynamic table.</summary>
    public uint QpackMaxTableCapacity { get; private set; }

    /// <summary>SETTINGS_QPACK_BLOCKED_STREAMS (0x7). 0 means no blocked streams.</summary>
    public uint QpackBlockedStreams { get; private set; }

    /// <summary>SETTINGS_ENABLE_CONNECT_PROTOCOL (0x8). False when absent or zero.</summary>
    public bool EnableConnectProtocol { get; private set; }

    /// <summary>Records one settings pair and updates the known field when the identifier matches.</summary>
    /// <param name="id">The settings identifier.</param>
    /// <param name="value">The settings value.</param>
    public void Add(ulong id, ulong value)
    {
        parameters.Add((id, value));
        if (id == Http3SettingsId.MaxFieldSectionSize)
        {
            MaxFieldSectionSize = value;
        }
        else if (id == Http3SettingsId.QpackMaxTableCapacity)
        {
            QpackMaxTableCapacity = (uint)Math.Min(value, uint.MaxValue);
        }
        else if (id == Http3SettingsId.QpackBlockedStreams)
        {
            QpackBlockedStreams = (uint)Math.Min(value, uint.MaxValue);
        }
        else if (id == Http3SettingsId.EnableConnectProtocol)
        {
            EnableConnectProtocol = value == 1;
        }
    }

    /// <summary>Adds SETTINGS_QPACK_MAX_TABLE_CAPACITY.</summary>
    /// <param name="capacity">The dynamic-table capacity.</param>
    public void SetQpackMaxTableCapacity(uint capacity) => Add(Http3SettingsId.QpackMaxTableCapacity, capacity);

    /// <summary>Adds SETTINGS_QPACK_BLOCKED_STREAMS.</summary>
    /// <param name="count">The blocked-stream limit.</param>
    public void SetQpackBlockedStreams(uint count) => Add(Http3SettingsId.QpackBlockedStreams, count);

    /// <summary>Adds SETTINGS_MAX_FIELD_SECTION_SIZE.</summary>
    /// <param name="size">The field-section limit.</param>
    public void SetMaxFieldSectionSize(ulong size) => Add(Http3SettingsId.MaxFieldSectionSize, size);

    /// <summary>Adds SETTINGS_ENABLE_CONNECT_PROTOCOL when <paramref name="enabled"/> is true.</summary>
    /// <param name="enabled">Whether the CONNECT protocol is enabled.</param>
    public void SetEnableConnectProtocol(bool enabled)
    {
        if (enabled)
        {
            Add(Http3SettingsId.EnableConnectProtocol, 1);
        }
    }

    /// <summary>Parses a SETTINGS frame payload. A truncated pair stops the parse and is ignored.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The parsed settings.</returns>
    public static Http3Settings Parse(ReadOnlySpan<byte> payload)
    {
        var settings = new Http3Settings();
        var remaining = payload;
        while (!remaining.IsEmpty)
        {
            if (!Http3VarInt.TryRead(remaining, out var id, out var idLen))
            {
                break;
            }

            remaining = remaining[idLen..];
            if (!Http3VarInt.TryRead(remaining, out var value, out var valueLen))
            {
                break;
            }

            remaining = remaining[valueLen..];
            settings.Add(id, value);
        }

        return settings;
    }

    /// <summary>Serializes this object into a SETTINGS frame payload.</summary>
    /// <returns>The payload bytes.</returns>
    public byte[] Serialize()
    {
        var buf = new byte[parameters.Count * 16];
        var offset = 0;
        foreach (var (id, value) in parameters)
        {
            offset += Http3VarInt.Write(buf.AsSpan(offset), id);
            offset += Http3VarInt.Write(buf.AsSpan(offset), value);
        }

        return buf[..offset];
    }
}
