namespace Titanium.Inspector.Services;

/// <summary>Result of the macOS Keychain Always Trust wait dialog.</summary>
public enum MacSslTrustWaitResult
{
    Cancelled = 0,
    Trusted = 1,
    /// <summary>User gave up or confirmed save but policies were still not detected.</summary>
    NotSavedYet = 2,
}
