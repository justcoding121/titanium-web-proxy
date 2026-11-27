using System;

namespace Titanium.Tls;

internal enum ExtensionOrderPolicy
{
    Fixed,
    PermuteExceptFixed,
    Groups,
}

internal enum PaddingPolicy
{
    None,
    PadToLength,
    PadWithinBand,
}

internal enum ExtensionKind
{
    Empty,
    Constant,
    ServerName,
    Alpn,
    NamedGroups,
    Versions,
    SignatureAlgorithms,
    KeyShare,
    EncryptedClientHelloGrease,
    GreaseEmpty,
    GreaseOneByte,
    Padding,
}

internal readonly struct AdvertisedCode
{
    internal AdvertisedCode(ushort value, bool implemented = false, bool grease = false)
    {
        Value = value;
        Implemented = implemented;
        Grease = grease;
    }

    internal ushort Value { get; }

    internal bool Implemented { get; }

    internal bool Grease { get; }
}

internal readonly struct KeyShareSlot
{
    internal KeyShareSlot(ushort group, int length, bool grease = false, bool omitWhenReduced = false)
    {
        Group = group;
        Length = length;
        Grease = grease;
        OmitWhenReduced = omitWhenReduced;
    }

    internal ushort Group { get; }

    internal int Length { get; }

    internal bool Grease { get; }

    internal bool OmitWhenReduced { get; }
}

internal sealed class ExtensionSpec
{
    internal required ushort Type { get; init; }

    internal ExtensionKind Kind { get; init; }

    internal bool Fixed { get; init; }

    internal int PermuteGroup { get; init; } = -1;

    internal bool Implemented { get; init; }

    internal byte[]? ConstantBody { get; init; }
}

internal sealed class ClientHelloTemplate
{
    internal required string Name { get; set; }

    internal required string Version { get; set; }

    internal TlsFingerprintFidelity Fidelity { get; set; }

    internal ushort RecordVersion { get; set; } = 0x0301;

    internal ushort LegacyVersion { get; set; } = 0x0303;

    internal int SessionIdLength { get; set; } = 32;

    internal bool SplitAcrossRecords { get; set; }

    internal ExtensionOrderPolicy Order { get; set; }

    internal PaddingPolicy Padding { get; set; }

    internal int PaddingTarget { get; set; }

    internal int PaddingBandMin { get; set; }

    internal int PaddingBandMax { get; set; }

    internal bool ExtensionGreaseValuesDiffer { get; set; }

    internal bool KeyShareGreaseMatchesGroup { get; set; }

    internal required AdvertisedCode[] Ciphers { get; set; }

    internal required AdvertisedCode[] Groups { get; set; }

    internal required AdvertisedCode[] Versions { get; set; }

    internal required AdvertisedCode[] SignatureAlgorithms { get; set; }

    internal required KeyShareSlot[] KeyShares { get; set; }

    internal required string[] ApplicationProtocols { get; set; }

    internal int[]? EchPayloadLengths { get; set; }

    internal required ExtensionSpec[] Extensions { get; set; }
}

internal static class Grease
{
    internal static readonly ushort[] Values =
    [
        0x0A0A, 0x1A1A, 0x2A2A, 0x3A3A, 0x4A4A, 0x5A5A, 0x6A6A, 0x7A7A,
        0x8A8A, 0x9A9A, 0xAAAA, 0xBABA, 0xCACA, 0xDADA, 0xEAEA, 0xFAFA,
    ];

    internal static bool IsGrease(ushort value)
    {
        return (value & 0x0F0F) == 0x0A0A;
    }
}

internal readonly struct GreaseDraw
{
    internal ushort Cipher { get; init; }

    internal ushort ExtensionFirst { get; init; }

    internal ushort ExtensionLast { get; init; }

    internal ushort Group { get; init; }

    internal ushort Version { get; init; }

    internal ushort Signature { get; init; }
}

internal static class EngineCapabilities
{
    internal static bool Cipher(ushort value)
    {
        return value is 0x1301 or 0x1302 or 0x1303;
    }

    internal static bool Group(ushort value)
    {
        return value is 0x0017 or 0x0018 or 0x001D or 0x11EC;
    }

    internal static bool Version(ushort value)
    {
        return value == 0x0304;
    }

    internal static bool Extension(ushort value)
    {
        return value is 0 or 10 or 13 or 16 or 21 or 27 or 41 or 43 or 45 or 51;
    }
}

internal static class TemplateGuard
{
    internal static void Check(ClientHelloTemplate template)
    {
        foreach (AdvertisedCode cipher in template.Ciphers)
        {
            Reject(cipher.Implemented && (cipher.Grease || !EngineCapabilities.Cipher(cipher.Value)), "cipher", cipher.Value);
        }

        foreach (AdvertisedCode group in template.Groups)
        {
            Reject(group.Implemented && (group.Grease || !EngineCapabilities.Group(group.Value)), "group", group.Value);
        }

        foreach (AdvertisedCode version in template.Versions)
        {
            Reject(version.Implemented && (version.Grease || !EngineCapabilities.Version(version.Value)), "version", version.Value);
        }

        foreach (ExtensionSpec extension in template.Extensions)
        {
            if (extension.Kind is ExtensionKind.GreaseEmpty or ExtensionKind.GreaseOneByte)
            {
                continue;
            }

            Reject(extension.Implemented && !EngineCapabilities.Extension(extension.Type), "extension", extension.Type);
        }
    }

    private static void Reject(bool failed, string kind, ushort value)
    {
        if (failed)
        {
            throw new TlsProtocolViolation($"The template marks {kind} 0x{value:x4} implemented, and the engine cannot select it.");
        }
    }
}
