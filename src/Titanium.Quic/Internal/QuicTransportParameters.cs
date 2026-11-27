using System;
using System.Buffers.Binary;

namespace Titanium.Quic;

/// <summary>One endpoint's transport parameters (RFC 9000 section 18).</summary>
internal sealed class QuicTransportParameters
{
    internal byte[]? OriginalDestinationConnectionId;

    internal byte[]? InitialSourceConnectionId;

    internal byte[]? RetrySourceConnectionId;

    internal byte[]? StatelessResetToken;

    internal ulong MaxIdleTimeout;

    internal ulong MaxUdpPayloadSize = 65527;

    internal ulong InitialMaxData;

    internal ulong InitialMaxStreamDataBidiLocal;

    internal ulong InitialMaxStreamDataBidiRemote;

    internal ulong InitialMaxStreamDataUni;

    internal ulong InitialMaxStreamsBidi;

    internal ulong InitialMaxStreamsUni;

    internal ulong AckDelayExponent = 3;

    internal ulong MaxAckDelay = 25;

    internal bool DisableActiveMigration;

    internal bool HasPreferredAddress;

    internal QuicPreferredAddress PreferredAddress = new();

    internal ulong ActiveConnectionIdLimit = 8;

    internal ulong MaxDatagramFrameSize;

    internal bool GreaseQuicBit;
}

internal sealed class QuicPreferredAddress
{
    internal byte[] IPv4 = new byte[4];

    internal ushort IPv4Port;

    internal byte[] IPv6 = new byte[16];

    internal ushort IPv6Port;

    internal byte[] ConnectionId = [];

    internal byte[] StatelessResetToken = new byte[16];
}

/// <summary>Reads and writes a transport-parameter list. Unknown parameters, including grease identifiers, are ignored.</summary>
internal static class QuicTransportParametersCodec
{
    internal const int MaxParameters = 64;

    internal const ulong TransportParameterError = 0x08;

    internal const ulong MaxStreamsLimit = 1UL << 60;

    internal static bool TryRead(ReadOnlySpan<byte> encoded, bool fromServer, bool retryReceived, out QuicTransportParameters parameters, out ulong error)
    {
        parameters = new QuicTransportParameters();
        error = 0;
        Span<ulong> seen = stackalloc ulong[MaxParameters];
        int seenCount = 0;
        while (!encoded.IsEmpty)
        {
            if (!QuicVarInt.TryDecode(encoded, out ulong id, out int idBytes)
                || !QuicVarInt.TryDecode(encoded[idBytes..], out ulong length, out int lengthBytes)
                || length > (ulong)(encoded.Length - idBytes - lengthBytes))
            {
                return Fail(out error);
            }

            int header = idBytes + lengthBytes;
            ReadOnlySpan<byte> value = encoded.Slice(header, (int)length);
            encoded = encoded[(header + (int)length)..];
            if (seenCount == MaxParameters || Contains(seen[..seenCount], id))
            {
                return Fail(out error);
            }

            seen[seenCount++] = id;
            if (!Apply(id, value, parameters))
            {
                return Fail(out error);
            }
        }

        bool serverOnly = parameters.OriginalDestinationConnectionId is not null
            || parameters.RetrySourceConnectionId is not null
            || parameters.StatelessResetToken is not null
            || parameters.HasPreferredAddress;
        if (parameters.InitialSourceConnectionId is null
            || (!fromServer && serverOnly)
            || (fromServer && parameters.OriginalDestinationConnectionId is null)
            || (fromServer && retryReceived && parameters.RetrySourceConnectionId is null)
            || (fromServer && !retryReceived && parameters.RetrySourceConnectionId is not null))
        {
            return Fail(out error);
        }

        return true;
    }

    internal static int Write(QuicTransportParameters parameters, Span<byte> destination)
    {
        int at = 0;
        if (!Append(parameters.OriginalDestinationConnectionId, 0x00, destination, ref at)
            || !AppendInteger(0x01, parameters.MaxIdleTimeout, destination, ref at)
            || !Append(parameters.StatelessResetToken, 0x02, destination, ref at)
            || !AppendInteger(0x03, parameters.MaxUdpPayloadSize, destination, ref at)
            || !AppendInteger(0x04, parameters.InitialMaxData, destination, ref at)
            || !AppendInteger(0x05, parameters.InitialMaxStreamDataBidiLocal, destination, ref at)
            || !AppendInteger(0x06, parameters.InitialMaxStreamDataBidiRemote, destination, ref at)
            || !AppendInteger(0x07, parameters.InitialMaxStreamDataUni, destination, ref at)
            || !AppendInteger(0x08, parameters.InitialMaxStreamsBidi, destination, ref at)
            || !AppendInteger(0x09, parameters.InitialMaxStreamsUni, destination, ref at)
            || !AppendInteger(0x0a, parameters.AckDelayExponent, destination, ref at)
            || !AppendInteger(0x0b, parameters.MaxAckDelay, destination, ref at)
            || !AppendEmpty(parameters.DisableActiveMigration, 0x0c, destination, ref at)
            || !AppendPreferred(parameters, destination, ref at)
            || !AppendInteger(0x0e, parameters.ActiveConnectionIdLimit, destination, ref at)
            || !Append(parameters.InitialSourceConnectionId, 0x0f, destination, ref at)
            || !Append(parameters.RetrySourceConnectionId, 0x10, destination, ref at)
            || !AppendInteger(0x20, parameters.MaxDatagramFrameSize, destination, ref at)
            || !AppendEmpty(parameters.GreaseQuicBit, 0x2ab2, destination, ref at))
        {
            return 0;
        }

        return at;
    }

    private static bool Apply(ulong id, ReadOnlySpan<byte> value, QuicTransportParameters parameters)
    {
        switch (id)
        {
            case 0x00:
                return TakeConnectionId(value, out parameters.OriginalDestinationConnectionId);
            case 0x01:
                return TakeInteger(value, out parameters.MaxIdleTimeout);
            case 0x02:
                return TakeFixed(value, 16, out parameters.StatelessResetToken);
            case 0x03:
                return TakeInteger(value, out parameters.MaxUdpPayloadSize) && parameters.MaxUdpPayloadSize >= 1200;
            case 0x04:
                return TakeInteger(value, out parameters.InitialMaxData);
            case 0x05:
                return TakeInteger(value, out parameters.InitialMaxStreamDataBidiLocal);
            case 0x06:
                return TakeInteger(value, out parameters.InitialMaxStreamDataBidiRemote);
            case 0x07:
                return TakeInteger(value, out parameters.InitialMaxStreamDataUni);
            case 0x08:
                return TakeInteger(value, out parameters.InitialMaxStreamsBidi) && parameters.InitialMaxStreamsBidi <= MaxStreamsLimit;
            case 0x09:
                return TakeInteger(value, out parameters.InitialMaxStreamsUni) && parameters.InitialMaxStreamsUni <= MaxStreamsLimit;
            case 0x0a:
                return TakeInteger(value, out parameters.AckDelayExponent) && parameters.AckDelayExponent <= 20;
            case 0x0b:
                return TakeInteger(value, out parameters.MaxAckDelay) && parameters.MaxAckDelay < 1UL << 14;
            case 0x0c:
                return value.IsEmpty && (parameters.DisableActiveMigration = true);
            case 0x0d:
                return TakePreferred(value, parameters);
            case 0x0e:
                return TakeInteger(value, out parameters.ActiveConnectionIdLimit) && parameters.ActiveConnectionIdLimit >= 2;
            case 0x0f:
                return TakeConnectionId(value, out parameters.InitialSourceConnectionId);
            case 0x10:
                return TakeConnectionId(value, out parameters.RetrySourceConnectionId);
            case 0x20:
                return TakeInteger(value, out parameters.MaxDatagramFrameSize);
            case 0x2ab2:
                return value.IsEmpty && (parameters.GreaseQuicBit = true);
            default:
                return true;
        }
    }

    private static bool TakePreferred(ReadOnlySpan<byte> value, QuicTransportParameters parameters)
    {
        if (value.Length < 41)
        {
            return false;
        }

        var address = new QuicPreferredAddress();
        value[..4].CopyTo(address.IPv4);
        address.IPv4Port = BinaryPrimitives.ReadUInt16BigEndian(value[4..6]);
        value.Slice(6, 16).CopyTo(address.IPv6);
        address.IPv6Port = BinaryPrimitives.ReadUInt16BigEndian(value[22..24]);
        int connectionIdLength = value[24];
        if (connectionIdLength is < 1 or > QuicDatagramReader.MaxConnectionIdLength || value.Length != 25 + connectionIdLength + 16)
        {
            return false;
        }

        address.ConnectionId = value.Slice(25, connectionIdLength).ToArray();
        value.Slice(25 + connectionIdLength, 16).CopyTo(address.StatelessResetToken);
        parameters.PreferredAddress = address;
        parameters.HasPreferredAddress = true;
        return true;
    }

    private static bool TakeConnectionId(ReadOnlySpan<byte> value, out byte[]? connectionId)
    {
        connectionId = null;
        if (value.Length > QuicDatagramReader.MaxConnectionIdLength)
        {
            return false;
        }

        connectionId = value.ToArray();
        return true;
    }

    private static bool TakeFixed(ReadOnlySpan<byte> value, int length, out byte[]? bytes)
    {
        bytes = null;
        if (value.Length != length)
        {
            return false;
        }

        bytes = value.ToArray();
        return true;
    }

    private static bool TakeInteger(ReadOnlySpan<byte> value, out ulong number)
    {
        return QuicVarInt.TryDecode(value, out number, out int used) && used == value.Length;
    }

    private static bool Append(byte[]? value, ulong id, Span<byte> destination, ref int at)
    {
        if (value is null)
        {
            return true;
        }

        if (id == 0x02)
        {
            if (value.Length != 16)
            {
                return false;
            }
        }
        else if (value.Length > QuicDatagramReader.MaxConnectionIdLength)
        {
            return false;
        }

        return AppendRaw(id, value, destination, ref at);
    }

    private static bool AppendEmpty(bool present, ulong id, Span<byte> destination, ref int at)
    {
        return !present || AppendRaw(id, ReadOnlySpan<byte>.Empty, destination, ref at);
    }

    private static bool AppendInteger(ulong id, ulong value, Span<byte> destination, ref int at)
    {
        Span<byte> encoded = stackalloc byte[8];
        int length = QuicVarInt.Encode(value, encoded);
        if (length == 0)
        {
            return false;
        }

        return AppendRaw(id, encoded[..length], destination, ref at);
    }

    private static bool AppendPreferred(QuicTransportParameters parameters, Span<byte> destination, ref int at)
    {
        if (!parameters.HasPreferredAddress)
        {
            return true;
        }

        QuicPreferredAddress address = parameters.PreferredAddress;
        if (address.IPv4.Length != 4
            || address.IPv6.Length != 16
            || address.ConnectionId.Length is < 1 or > QuicDatagramReader.MaxConnectionIdLength
            || address.StatelessResetToken.Length != 16)
        {
            return false;
        }

        int length = 25 + address.ConnectionId.Length + 16;
        Span<byte> body = length <= 128 ? stackalloc byte[length] : new byte[length];
        address.IPv4.CopyTo(body);
        BinaryPrimitives.WriteUInt16BigEndian(body[4..6], address.IPv4Port);
        address.IPv6.CopyTo(body[6..]);
        BinaryPrimitives.WriteUInt16BigEndian(body[22..24], address.IPv6Port);
        body[24] = (byte)address.ConnectionId.Length;
        address.ConnectionId.CopyTo(body[25..]);
        address.StatelessResetToken.CopyTo(body[(25 + address.ConnectionId.Length)..]);
        return AppendRaw(0x0d, body[..length], destination, ref at);
    }

    private static bool AppendRaw(ulong id, ReadOnlySpan<byte> value, Span<byte> destination, ref int at)
    {
        if ((uint)at > (uint)destination.Length)
        {
            return false;
        }

        int idBytes = QuicVarInt.Encode(id, destination[at..]);
        if (idBytes == 0)
        {
            return false;
        }

        int lengthBytes = QuicVarInt.Encode((ulong)value.Length, destination[(at + idBytes)..]);
        if (lengthBytes == 0 || destination.Length < at + idBytes + lengthBytes + value.Length)
        {
            return false;
        }

        value.CopyTo(destination[(at + idBytes + lengthBytes)..]);
        at += idBytes + lengthBytes + value.Length;
        return true;
    }

    private static bool Contains(ReadOnlySpan<ulong> seen, ulong id)
    {
        foreach (ulong existing in seen)
        {
            if (existing == id)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Fail(out ulong error)
    {
        error = TransportParameterError;
        return false;
    }
}
