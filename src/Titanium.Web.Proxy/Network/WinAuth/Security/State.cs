using System;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

/// <summary>
///     Status of authenticated session
/// </summary>
internal sealed class State : IDisposable
{
    /// <summary>
    ///     States during Windows Authentication
    /// </summary>
    public enum WinAuthState
    {
        Unauthorized,
        InitialToken,
        FinalToken,
        Authorized
    }

    /// <summary>
    ///     Current state of the authentication process
    /// </summary>
    internal WinAuthState AuthState;

    /// <summary>
    ///     Context will be used to validate HTLM hashes
    /// </summary>
    internal readonly SafeSspiHandle Context;

    /// <summary>
    ///     Credentials used to validate NTLM hashes
    /// </summary>
    internal readonly SafeSspiHandle Credentials;

    /// <summary>
    ///     Timestamp needed to calculate validity of the authenticated session
    /// </summary>
    internal DateTime LastSeen;

    internal State()
    {
        Credentials = new SafeSspiHandle(SafeSspiHandle.HandleKind.Credential);
        Context = new SafeSspiHandle(SafeSspiHandle.HandleKind.Context);

        LastSeen = DateTime.UtcNow;
        AuthState = WinAuthState.Unauthorized;
    }

    internal void ResetHandles()
    {
        Credentials.Free();
        Context.Free();
        AuthState = WinAuthState.Unauthorized;
    }

    internal void UpdatePresence()
    {
        LastSeen = DateTime.UtcNow;
    }

    /// <summary>
    ///     Releases the native SSPI credentials and security-context handles owned by this state.
    ///     Safe to call once the negotiation has reached a terminal state (<see cref="WinAuthState.Authorized" />
    ///     or a failed round); do not call while a multi-round negotiation is still in progress, since
    ///     a later round needs both handles to still be valid.
    /// </summary>
    public void Dispose()
    {
        Credentials.Dispose();
        Context.Dispose();
    }
}
