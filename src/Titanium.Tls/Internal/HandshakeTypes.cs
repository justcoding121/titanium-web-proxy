using System;

namespace Titanium.Tls;

internal enum HandshakeType : byte
{
    ClientHello = 1,
    ServerHello = 2,
    NewSessionTicket = 4,
    EndOfEarlyData = 5,
    EncryptedExtensions = 8,
    Certificate = 11,
    CertificateRequest = 13,
    CertificateVerify = 15,
    Finished = 20,
    KeyUpdate = 24,
    CompressedCertificate = 25,
    MessageHash = 254,
}

internal enum HandshakeAlert
{
    UnexpectedMessage = 10,
    IllegalParameter = 47,
    DecodeError = 50,
    UnsupportedExtension = 110,
}

internal enum HandshakeMessageKind
{
    ClientHello,
    ServerHello,
    HelloRetryRequest,
    EncryptedExtensions,
    Certificate,
    CertificateRequest,
    NewSessionTicket,
}

/// <summary>Extension code points this stack recognizes. Unlisted values are retained and ignored.</summary>
internal static class ExtensionType
{
    internal const ushort ServerName = 0;
    internal const ushort MaxFragmentLength = 1;
    internal const ushort StatusRequest = 5;
    internal const ushort SupportedGroups = 10;
    internal const ushort EcPointFormats = 11;
    internal const ushort SignatureAlgorithms = 13;
    internal const ushort UseSrtp = 14;
    internal const ushort Heartbeat = 15;
    internal const ushort ApplicationLayerProtocolNegotiation = 16;
    internal const ushort SignedCertificateTimestamp = 18;
    internal const ushort ClientCertificateType = 19;
    internal const ushort ServerCertificateType = 20;
    internal const ushort Padding = 21;
    internal const ushort ExtendedMasterSecret = 23;
    internal const ushort CompressCertificate = 27;
    internal const ushort SessionTicket = 35;
    internal const ushort PreSharedKey = 41;
    internal const ushort EarlyData = 42;
    internal const ushort SupportedVersions = 43;
    internal const ushort Cookie = 44;
    internal const ushort PskKeyExchangeModes = 45;
    internal const ushort CertificateAuthorities = 47;
    internal const ushort OidFilters = 48;
    internal const ushort PostHandshakeAuth = 49;
    internal const ushort SignatureAlgorithmsCert = 50;
    internal const ushort KeyShare = 51;
    internal const ushort QuicTransportParameters = 57;
    internal const ushort ApplicationSettingsLegacy = 17513;
    internal const ushort ApplicationSettings = 17613;
    internal const ushort EncryptedClientHello = 0xFE0D;
    internal const ushort RenegotiationInfo = 0xFF01;

    // BinarySearch requires this list to stay in ascending numeric order.
    private static readonly ushort[] Known =
    [
        ServerName,
        MaxFragmentLength,
        StatusRequest,
        SupportedGroups,
        EcPointFormats,
        SignatureAlgorithms,
        UseSrtp,
        Heartbeat,
        ApplicationLayerProtocolNegotiation,
        SignedCertificateTimestamp,
        ClientCertificateType,
        ServerCertificateType,
        Padding,
        ExtendedMasterSecret,
        CompressCertificate,
        SessionTicket,
        PreSharedKey,
        EarlyData,
        SupportedVersions,
        Cookie,
        PskKeyExchangeModes,
        CertificateAuthorities,
        OidFilters,
        PostHandshakeAuth,
        SignatureAlgorithmsCert,
        KeyShare,
        QuicTransportParameters,
        ApplicationSettingsLegacy,
        ApplicationSettings,
        EncryptedClientHello,
        RenegotiationInfo,
    ];

    internal static bool IsKnown(ushort type)
    {
        return Array.BinarySearch(Known, type) >= 0;
    }
}
