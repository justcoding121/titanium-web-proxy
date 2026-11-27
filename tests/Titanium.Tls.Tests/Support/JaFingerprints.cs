using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Titanium.Tls.Tests.Support;

internal static class Ja3
{
    internal static string Fingerprint(ReadOnlySpan<byte> record)
    {
        return Hash(Describe(record));
    }

    internal static string Hash(string fields)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(fields))).ToLowerInvariant();
    }

    internal static string Describe(ReadOnlySpan<byte> record)
    {
        ClientHelloView hello = JaHello.Read(record);
        var ciphers = new List<ushort>();
        for (int i = 0; i + 1 < hello.CipherSuites.Length; i += 2)
        {
            ushort cipher = JaHello.ReadU16(hello.CipherSuites, i);
            if (!Grease.IsGrease(cipher))
            {
                ciphers.Add(cipher);
            }
        }

        var extensions = new List<ushort>();
        var groups = new List<ushort>();
        var points = new List<byte>();
        JaHello.Walk(hello.Extensions, (type, body) =>
        {
            if (Grease.IsGrease(type))
            {
                return;
            }

            extensions.Add(type);
            if (type == ExtensionType.SupportedGroups)
            {
                groups.AddRange(JaHello.ReadList16(body));
            }
            else if (type == ExtensionType.EcPointFormats && body.Length > 0)
            {
                int count = body[0];
                for (int i = 0; i < count && 1 + i < body.Length; i++)
                {
                    points.Add(body[1 + i]);
                }
            }
        });
        return hello.LegacyVersion + "," + Join(ciphers) + "," + Join(extensions) + "," + Join(groups) + "," + Join(points);
    }

    private static string Join<T>(List<T> values)
        where T : struct
    {
        var text = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text.Append('-');
            }

            text.Append(values[i]);
        }

        return text.ToString();
    }
}

internal static class Ja4
{
    internal static string Fingerprint(ReadOnlySpan<byte> record)
    {
        ClientHelloView hello = JaHello.Read(record);
        var ciphers = new List<ushort>();
        for (int i = 0; i + 1 < hello.CipherSuites.Length; i += 2)
        {
            ushort cipher = JaHello.ReadU16(hello.CipherSuites, i);
            if (!Grease.IsGrease(cipher))
            {
                ciphers.Add(cipher);
            }
        }

        var extensions = new List<ushort>();
        var signatures = new List<ushort>();
        bool sni = false;
        string alpn = "00";
        int version = hello.LegacyVersion;
        JaHello.Walk(hello.Extensions, (type, body) =>
        {
            if (Grease.IsGrease(type))
            {
                return;
            }

            extensions.Add(type);
            if (type == ExtensionType.ServerName)
            {
                sni = true;
            }
            else if (type == ExtensionType.ApplicationLayerProtocolNegotiation)
            {
                alpn = Alpn(body);
            }
            else if (type == ExtensionType.SupportedVersions)
            {
                version = HighestVersion(body, version);
            }
            else if (type == ExtensionType.SignatureAlgorithms)
            {
                signatures.AddRange(JaHello.ReadList16(body));
            }
        });
        string versionText = version switch
        {
            0x0304 => "13",
            0x0303 => "12",
            0x0302 => "11",
            0x0301 => "10",
            0x0300 => "s3",
            0x0002 => "s2",
            _ => "00",
        };
        return "t" + versionText + (sni ? "d" : "i") + Math.Min(ciphers.Count, 99).ToString("00") + Math.Min(extensions.Count, 99).ToString("00") + alpn
            + "_" + HashCiphers(ciphers) + "_" + HashExtensions(extensions, signatures);
    }

    internal static string CipherHash(string sortedCiphers)
    {
        return sortedCiphers.Length == 0 ? "000000000000" : Truncate(sortedCiphers);
    }

    internal static string ExtensionHash(string sortedExtensionsAndSignatures)
    {
        return sortedExtensionsAndSignatures.Length == 0 ? "000000000000" : Truncate(sortedExtensionsAndSignatures);
    }

    private static string HashCiphers(List<ushort> ciphers)
    {
        ciphers.Sort();
        return CipherHash(HexList(ciphers));
    }

    private static string HashExtensions(List<ushort> extensions, List<ushort> signatures)
    {
        extensions.RemoveAll(type => type is ExtensionType.ServerName or ExtensionType.ApplicationLayerProtocolNegotiation);
        extensions.Sort();
        if (extensions.Count == 0 && signatures.Count == 0)
        {
            return "000000000000";
        }

        string text = HexList(extensions);
        if (signatures.Count > 0)
        {
            text = text.Length == 0 ? HexList(signatures) : text + "_" + HexList(signatures);
        }

        return Truncate(text);
    }

    private static string Truncate(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(text));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string HexList(List<ushort> values)
    {
        var text = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(values[i].ToString("x4"));
        }

        return text.ToString();
    }

    private static int HighestVersion(ReadOnlySpan<byte> body, int fallback)
    {
        if (body.IsEmpty)
        {
            return fallback;
        }

        int count = body[0];
        int best = 0;
        for (int i = 0; i < count && 1 + (i * 2) + 1 < body.Length; i++)
        {
            ushort version = JaHello.ReadU16(body, 1 + (i * 2));
            if (!Grease.IsGrease(version) && version > best)
            {
                best = version;
            }
        }

        return best == 0 ? fallback : best;
    }

    private static string Alpn(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
        {
            return "00";
        }

        int list = JaHello.ReadU16(body, 0);
        if (list < 1 || body.Length < 2 + list)
        {
            return "00";
        }

        int nameLength = body[2];
        if (nameLength == 0 || 3 + nameLength > body.Length)
        {
            return "00";
        }

        ReadOnlySpan<byte> name = body.Slice(3, nameLength);
        if (name.Length == 1 && IsAlphaNumeric(name[0]))
        {
            return string.Concat((char)name[0], (char)name[0]);
        }

        if (IsAlphaNumeric(name[0]) && IsAlphaNumeric(name[^1]))
        {
            return string.Concat((char)name[0], (char)name[^1]);
        }

        string hex = Convert.ToHexString(name).ToLowerInvariant();
        return string.Concat(hex[0], hex[^1]);
    }

    private static bool IsAlphaNumeric(byte value)
    {
        return value is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A);
    }
}

internal static class JaHello
{
    internal static ClientHelloView Read(ReadOnlySpan<byte> record)
    {
        if (!HandshakeFraming.TryUnwrapHandshakeRecord(record, out ReadOnlySpan<byte> payload, out _, out _) ||
            !HandshakeMessages.TryReadClientHello(payload, out ClientHelloView hello, out _))
        {
            throw new InvalidOperationException("The record is not a ClientHello.");
        }

        return hello;
    }

    internal static void Walk(ReadOnlySpan<byte> extensions, Action<ushort, ReadOnlySpan<byte>> visit)
    {
        var reader = new TlsReader(extensions);
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            visit(type, body);
        }
    }

    internal static List<ushort> ReadList16(ReadOnlySpan<byte> body)
    {
        var values = new List<ushort>();
        if (body.Length < 2)
        {
            return values;
        }

        int count = ReadU16(body, 0);
        for (int i = 0; i + 1 < count && 2 + i + 1 < body.Length; i += 2)
        {
            ushort value = ReadU16(body, 2 + i);
            if (!Grease.IsGrease(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    internal static ushort ReadU16(ReadOnlySpan<byte> bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }
}
