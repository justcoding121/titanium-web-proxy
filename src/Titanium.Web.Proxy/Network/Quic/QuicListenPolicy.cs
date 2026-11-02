using System;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Stops a developer test host from opening a QUIC listener.
///     MsQuic reports a listen on <c>::1</c> as wildcard UDP, and Windows Firewall prompts once per executable path.
///     A real proxy process leaves <see cref="BeforeListen"/> unset and listens as configured.
///     CI sets <c>TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1</c> and still listens.
/// </summary>
internal static class QuicListenPolicy
{
    /// <summary>
    ///     Test hosts set this to mark the current test inconclusive. Invoked only when a listen would prompt.
    /// </summary>
    internal static Action? BeforeListen { get; set; }

    /// <summary>
    ///     True when this process is a Windows test host that must not open a QUIC listener.
    /// </summary>
    internal static bool RefusesListen =>
        OperatingSystem.IsWindows()
        && IsOne("TITANIUM_SKIP_ROOT_STORE_UI")
        && !IsOne("TITANIUM_ALLOW_WILDCARD_BIND_TESTS")
        && BeforeListen is not null;

    /// <summary>
    ///     Returns when the listen is allowed. A test host's <see cref="BeforeListen"/> runs first so the test
    ///     can stop as inconclusive. If that callback returns, the listen is still refused.
    /// </summary>
    internal static void EnsureAllowed()
    {
        if (!RefusesListen)
            return;

        BeforeListen?.Invoke();
        throw new InvalidOperationException(
            "Refusing a QUIC listen on a developer test host. MsQuic opens wildcard UDP sockets. " +
            "Set TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1 where a firewall prompt will not appear.");
    }

    private static bool IsOne(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
