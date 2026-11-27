using System;
using System.Buffers;

namespace Titanium.Tls;

/// <summary>The profiles this assembly ships.</summary>
public static class TlsFingerprintProfiles
{
    /// <summary>The captured browser profile.</summary>
    public static ChromeFingerprintProfile Chrome { get; } = new();
}

/// <summary>
/// ClientHello shape measured from Chrome 158.0.8127.89 on Windows.
/// The middle extensions move. The first and last extensions are GREASE, and those two values differ.
/// The GREASE value in supported_groups is the same value as the GREASE key share.
/// </summary>
public sealed class ChromeFingerprintProfile : TlsFingerprintProfile
{
    private readonly IEntropySource _entropy;
    private readonly TlsFingerprintFidelity? _forced;

    /// <summary>Uses the process entropy source and the current post-quantum capability.</summary>
    public ChromeFingerprintProfile()
        : this(new SystemEntropy(), null)
    {
    }

    internal ChromeFingerprintProfile(IEntropySource entropy, TlsFingerprintFidelity? fidelity)
    {
        _entropy = entropy;
        _forced = fidelity;
    }

    /// <inheritdoc />
    public override string Name => "Chrome";

    /// <inheritdoc />
    public override string Version => "158.0.8127.89";

    /// <inheritdoc />
    public override TlsFingerprintFidelity Fidelity => _forced ?? TlsFingerprint.Current;

    /// <inheritdoc />
    public override void BuildClientHello(ClientHelloContext context, IBufferWriter<byte> destination)
    {
        ClientHelloBuilder.Write(Create(Fidelity), context, _entropy, destination);
    }

    private static ClientHelloTemplate Create(TlsFingerprintFidelity fidelity)
    {
        return new ClientHelloTemplate
        {
            Name = "Chrome",
            Version = "158.0.8127.89",
            Fidelity = fidelity,
            RecordVersion = 0x0301,
            LegacyVersion = 0x0303,
            SessionIdLength = 32,
            Order = ExtensionOrderPolicy.PermuteExceptFixed,
            ExtensionGreaseValuesDiffer = true,
            KeyShareGreaseMatchesGroup = true,
            EchPayloadLengths = EchPayloadLengths,
            ApplicationProtocols = ["h2", "http/1.1"],
            Ciphers =
            [
                new AdvertisedCode(0, grease: true),
                new AdvertisedCode(0x1301, implemented: true),
                new AdvertisedCode(0x1302, implemented: true),
                new AdvertisedCode(0x1303, implemented: true),
                new AdvertisedCode(0xC02B),
                new AdvertisedCode(0xC02F),
                new AdvertisedCode(0xC02C),
                new AdvertisedCode(0xC030),
                new AdvertisedCode(0xCCA9),
                new AdvertisedCode(0xCCA8),
                new AdvertisedCode(0xC013),
                new AdvertisedCode(0xC014),
                new AdvertisedCode(0x009C),
                new AdvertisedCode(0x009D),
                new AdvertisedCode(0x002F),
                new AdvertisedCode(0x0035),
            ],
            Groups =
            [
                new AdvertisedCode(0, grease: true),
                new AdvertisedCode(0x11EC, implemented: true),
                new AdvertisedCode(0x001D, implemented: true),
                new AdvertisedCode(0x0017, implemented: true),
                new AdvertisedCode(0x0018, implemented: true),
            ],
            Versions =
            [
                new AdvertisedCode(0, grease: true),
                new AdvertisedCode(0x0304, implemented: true),
                new AdvertisedCode(0x0303),
            ],
            SignatureAlgorithms =
            [
                new AdvertisedCode(0, grease: true),
                new AdvertisedCode(0x0904),
                new AdvertisedCode(0x0905),
                new AdvertisedCode(0x0906),
                new AdvertisedCode(0x0403),
                new AdvertisedCode(0x0804),
                new AdvertisedCode(0x0401),
                new AdvertisedCode(0x0503),
                new AdvertisedCode(0x0805),
                new AdvertisedCode(0x0501),
                new AdvertisedCode(0x0806),
                new AdvertisedCode(0x0601),
            ],
            KeyShares =
            [
                new KeyShareSlot(0, 1, grease: true),
                new KeyShareSlot(0x11EC, 1216, omitWhenReduced: true),
                new KeyShareSlot(0x001D, 32),
            ],
            Extensions =
            [
                new ExtensionSpec { Type = 0, Kind = ExtensionKind.GreaseEmpty, Fixed = true },
                new ExtensionSpec { Type = ExtensionType.ExtendedMasterSecret, Kind = ExtensionKind.Empty },
                new ExtensionSpec { Type = ExtensionType.ApplicationSettings, Kind = ExtensionKind.Constant, ConstantBody = [0x00, 0x03, 0x02, 0x68, 0x32] },
                new ExtensionSpec { Type = ExtensionType.ApplicationLayerProtocolNegotiation, Kind = ExtensionKind.Alpn, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.SupportedVersions, Kind = ExtensionKind.Versions, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.KeyShare, Kind = ExtensionKind.KeyShare, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.SessionTicket, Kind = ExtensionKind.Empty },
                new ExtensionSpec { Type = ExtensionType.SignedCertificateTimestamp, Kind = ExtensionKind.Empty },
                new ExtensionSpec { Type = ExtensionType.SignatureAlgorithms, Kind = ExtensionKind.SignatureAlgorithms, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.RenegotiationInfo, Kind = ExtensionKind.Constant, ConstantBody = [0x00] },
                new ExtensionSpec { Type = ExtensionType.ServerName, Kind = ExtensionKind.ServerName, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.PskKeyExchangeModes, Kind = ExtensionKind.Constant, ConstantBody = [0x01, 0x01], Implemented = true },
                new ExtensionSpec { Type = ExtensionType.CompressCertificate, Kind = ExtensionKind.Constant, ConstantBody = [0x02, 0x00, 0x02], Implemented = true },
                new ExtensionSpec { Type = ExtensionType.StatusRequest, Kind = ExtensionKind.Constant, ConstantBody = [0x01, 0x00, 0x00, 0x00, 0x00] },
                new ExtensionSpec { Type = ExtensionType.SupportedGroups, Kind = ExtensionKind.NamedGroups, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.EncryptedClientHello, Kind = ExtensionKind.EncryptedClientHelloGrease },
                new ExtensionSpec { Type = 0xCA34, Kind = ExtensionKind.Constant, ConstantBody = UnknownExtension },
                new ExtensionSpec { Type = ExtensionType.EcPointFormats, Kind = ExtensionKind.Constant, ConstantBody = [0x01, 0x00] },
                new ExtensionSpec { Type = 0, Kind = ExtensionKind.GreaseOneByte, Fixed = true },
            ],
        };
    }

    private static readonly int[] EchPayloadLengths =
    [
        208, 208, 240, 144, 208, 208, 240, 240, 240, 240, 144, 208, 176, 144, 176, 144, 208, 176, 176, 176,
        208, 240, 144, 208, 176, 208, 176, 208, 208, 176, 144, 240, 176, 144, 240, 208, 176, 176, 208, 144,
        208, 176, 208, 240, 208, 208, 176, 240, 208, 144,
    ];

    private static readonly byte[] UnknownExtension = Convert.FromHexString(
        "00B80582DF1302010582DF1302060582DF13020D0582DF13020E0582DF13020F0582DF1302120582DF1302130582DF130214" +
        "08839A648C9B2D010708839A648C9B2D010808839A648C9B2D010908839A648C9B2D010A08839A648C9B2D010B08839A648C9B2D010C" +
        "08839A648C9B2D010D08839A648C9B2D011208839A648C9B2D011304D679090104D679090404D679090504D679090604D679090704D6790908" +
        "04D679090A04D679090B04D679090C04D679090D04D679090F");
}
