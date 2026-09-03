namespace Titanium.Web.Proxy.Network;

/// <summary>Outcome kind for OS / browser SSL trust helpers (Keychain, NSS, package install).</summary>
public enum CertificateOsTrustKind
{
    Succeeded = 0,
    CertutilMissing = 1,
    NssFailed = 2,
    MacKeychainFailed = 3,
    MacNeedsManualTrustConfirm = 4,
    HomebrewMissing = 5,
    Unsupported = 6,
    Cancelled = 7,
    Failed = 8,
}

/// <summary>Structured result from Unix OS trust or related helper operations.</summary>
public sealed class CertificateOsTrustResult
{
    public CertificateOsTrustResult(
        CertificateOsTrustKind kind,
        string message,
        string? packageHint = null,
        bool brewAvailable = false)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        PackageHint = packageHint;
        BrewAvailable = brewAvailable;
    }

    public CertificateOsTrustKind Kind { get; }
    public string Message { get; }
    public string? PackageHint { get; }
    public bool BrewAvailable { get; }
    public bool Succeeded => Kind == CertificateOsTrustKind.Succeeded;

    public static CertificateOsTrustResult Ok(string message = "Trusted") =>
        new(CertificateOsTrustKind.Succeeded, message);

    public static CertificateOsTrustResult Fail(
        CertificateOsTrustKind kind,
        string message,
        string? packageHint = null,
        bool brewAvailable = false) =>
        new(kind, message, packageHint, brewAvailable);
}
