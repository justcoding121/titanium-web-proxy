using System;

namespace Titanium.Tls;

/// <summary>
/// RFC 8446 section 4.2 allow-lists. A known extension on the wrong message is
/// illegal_parameter. An unknown extension is kept. A duplicate of either is illegal_parameter.
/// </summary>
internal static class ExtensionRules
{
    internal static bool TryValidate(HandshakeMessageKind message, ReadOnlySpan<byte> extensions, out HandshakeAlert alert)
    {
        alert = default;
        Span<ushort> seen = stackalloc ushort[48];
        int count = 0;
        bool sawPreSharedKey = false;
        TlsReader reader = new(extensions);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadVector16(out _))
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }

            if (count == seen.Length)
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (seen[i] == type)
                {
                    alert = HandshakeAlert.IllegalParameter;
                    return false;
                }
            }

            seen[count++] = type;
            if (message == HandshakeMessageKind.ClientHello && sawPreSharedKey)
            {
                alert = HandshakeAlert.IllegalParameter;
                return false;
            }

            if (type == ExtensionType.PreSharedKey)
            {
                sawPreSharedKey = true;
            }

            if (ExtensionType.IsKnown(type) && !IsAllowed(message, type))
            {
                alert = HandshakeAlert.IllegalParameter;
                return false;
            }
        }

        return true;
    }

    internal static bool IsAllowed(HandshakeMessageKind message, ushort type)
    {
        return message switch
        {
            HandshakeMessageKind.ClientHello => type is
                ExtensionType.ServerName or
                ExtensionType.MaxFragmentLength or
                ExtensionType.StatusRequest or
                ExtensionType.SupportedGroups or
                ExtensionType.EcPointFormats or
                ExtensionType.SignatureAlgorithms or
                ExtensionType.UseSrtp or
                ExtensionType.Heartbeat or
                ExtensionType.ApplicationLayerProtocolNegotiation or
                ExtensionType.SignedCertificateTimestamp or
                ExtensionType.ClientCertificateType or
                ExtensionType.ServerCertificateType or
                ExtensionType.Padding or
                ExtensionType.ExtendedMasterSecret or
                ExtensionType.CompressCertificate or
                ExtensionType.SessionTicket or
                ExtensionType.PreSharedKey or
                ExtensionType.EarlyData or
                ExtensionType.SupportedVersions or
                ExtensionType.Cookie or
                ExtensionType.PskKeyExchangeModes or
                ExtensionType.CertificateAuthorities or
                ExtensionType.PostHandshakeAuth or
                ExtensionType.SignatureAlgorithmsCert or
                ExtensionType.KeyShare or
                ExtensionType.QuicTransportParameters or
                ExtensionType.ApplicationSettingsLegacy or
                ExtensionType.ApplicationSettings or
                ExtensionType.EncryptedClientHello or
                ExtensionType.RenegotiationInfo,
            HandshakeMessageKind.ServerHello => type is ExtensionType.PreSharedKey or ExtensionType.SupportedVersions or ExtensionType.KeyShare,
            HandshakeMessageKind.HelloRetryRequest => type is ExtensionType.SupportedVersions or ExtensionType.Cookie or ExtensionType.KeyShare,
            HandshakeMessageKind.EncryptedExtensions => type is
                ExtensionType.ServerName or
                ExtensionType.MaxFragmentLength or
                ExtensionType.SupportedGroups or
                ExtensionType.UseSrtp or
                ExtensionType.Heartbeat or
                ExtensionType.ApplicationLayerProtocolNegotiation or
                ExtensionType.ServerCertificateType or
                ExtensionType.EarlyData or
                ExtensionType.QuicTransportParameters or
                ExtensionType.ApplicationSettingsLegacy or
                ExtensionType.ApplicationSettings,
            HandshakeMessageKind.Certificate => type is ExtensionType.StatusRequest or ExtensionType.SignedCertificateTimestamp,
            HandshakeMessageKind.CertificateRequest => type is
                ExtensionType.StatusRequest or
                ExtensionType.SignatureAlgorithms or
                ExtensionType.SignedCertificateTimestamp or
                ExtensionType.CertificateAuthorities or
                ExtensionType.OidFilters or
                ExtensionType.SignatureAlgorithmsCert or
                ExtensionType.CompressCertificate,
            HandshakeMessageKind.NewSessionTicket => type == ExtensionType.EarlyData,
            _ => false,
        };
    }
}
