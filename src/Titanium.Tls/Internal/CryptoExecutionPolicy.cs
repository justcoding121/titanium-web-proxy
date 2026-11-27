using System;

namespace Titanium.Tls;

internal enum CryptoOffloadReason
{
    None = 0,
    OnlineRevocation = 1,
    ExternalPrivateKey = 2,
}

/// <summary>
/// Decides which calls stay on the caller and which must leave it.
/// The thread hop itself waits for certificate validation. Running it here would schedule work the handshake does not do yet.
/// </summary>
internal static class CryptoExecutionPolicy
{
    internal static int HandshakeConcurrencyLimit(int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);
        return processorCount * 4;
    }

    internal static CryptoOffloadReason Revocation(bool checksOnlineRevocation)
    {
        return checksOnlineRevocation ? CryptoOffloadReason.OnlineRevocation : CryptoOffloadReason.None;
    }

    internal static CryptoOffloadReason Signing(bool softwareKey)
    {
        return softwareKey ? CryptoOffloadReason.None : CryptoOffloadReason.ExternalPrivateKey;
    }

    internal static void Run(CryptoOffloadReason reason, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = reason;
        action();
    }
}
