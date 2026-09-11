using System;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A host that the proxy learned to tunnel without decrypt after repeated origin TLS
///     handshake failures under MITM (e.g. bot / TLS-fingerprint rejection).
/// </summary>
public sealed class DecryptFailureBypassEntry
{
    public DecryptFailureBypassEntry(string host, int strikes, DateTime learnedAtUtc, DateTime expiresAtUtc,
        bool bypassActive)
    {
        Host = host;
        Strikes = strikes;
        LearnedAtUtc = learnedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        BypassActive = bypassActive;
    }

    /// <summary>Normalized hostname (no port).</summary>
    public string Host { get; }

    /// <summary>Origin TLS failure strikes accumulated toward the bypass threshold.</summary>
    public int Strikes { get; }

    /// <summary>When the first strike (or forced bypass) was recorded.</summary>
    public DateTime LearnedAtUtc { get; }

    /// <summary>When this entry expires if not refreshed.</summary>
    public DateTime ExpiresAtUtc { get; }

    /// <summary>True when subsequent CONNECTs skip decrypt for this host.</summary>
    public bool BypassActive { get; }
}
