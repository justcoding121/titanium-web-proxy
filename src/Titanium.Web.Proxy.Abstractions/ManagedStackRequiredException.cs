using System;

namespace Titanium.Web.Proxy.Abstractions;

/// <summary>
///     A feature was requested that the platform TLS and QUIC stacks cannot perform.
/// </summary>
public sealed class ManagedStackRequiredException : NotSupportedException
{
    /// <summary>
    ///     Why the feature needs the managed stack.
    /// </summary>
    public const string Reason =
        "The platform TLS and QUIC APIs do not let a program choose the exact ClientHello, extension order or QUIC Initial layout, and do not expose raw UDP datagrams to a QUIC connection, so these features can only be built on a TLS and QUIC implementation we own.";

    /// <summary>Creates an error that names the feature, the flag, and <see cref="Reason"/>.</summary>
    /// <param name="feature">The feature that was requested.</param>
    /// <param name="flag">The proxy flag that has to be on.</param>
    public ManagedStackRequiredException(string feature, string flag)
        : base(feature + " needs " + flag + " or TITANIUM_MANAGED_STACK. " + Reason)
    {
        Feature = feature;
        Flag = flag;
    }

    /// <summary>The feature that was requested.</summary>
    public string Feature { get; }

    /// <summary>The flag that has to be on.</summary>
    public string Flag { get; }
}
