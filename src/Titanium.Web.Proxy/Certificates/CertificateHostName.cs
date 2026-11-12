using System;
using System.Globalization;
using System.Net;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Normalizes host names for leaf certificates. DNS SANs and CN values must be A-labels
///     (punycode); putting Unicode or mojibake into a DnsName SAN breaks browser name matching.
/// </summary>
internal static class CertificateHostName
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    /// <summary>
    ///     Returns an IP literal unchanged, otherwise the ASCII (punycode) form of a DNS name.
    /// </summary>
    public static string Normalize(string hostName)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return hostName;
        }

        if (IPAddress.TryParse(hostName, out _))
        {
            return hostName;
        }

        try
        {
            return Idn.GetAscii(hostName);
        }
        catch (ArgumentException)
        {
            // Malformed IDN: keep the caller-supplied bytes so MakeCertificate can still fail loudly.
            return hostName;
        }
    }
}
