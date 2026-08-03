using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

using static Common;

/// <summary>
///     Owns exactly one native SSPI handle (a credentials handle or a security-context handle) and
///     guarantees it is released via the matching secur32 API - <c>FreeCredentialsHandle</c> or
///     <c>DeleteSecurityContext</c> - exactly once.
///     <para>
///         Before this type existed, <see cref="State" /> held a bare <see cref="SecurityHandle" />
///         struct and nothing ever called either release function: every completed or abandoned
///         NTLM/Negotiate/Kerberos exchange leaked one credentials handle and one security-context
///         handle in the OS's SSPI provider for the lifetime of the process.
///     </para>
///     <para>
///         Deriving from <see cref="CriticalFinalizerObject" /> (rather than relying on an ordinary
///         finalizer) ensures the release still runs even if the owning <see cref="State" /> is
///         abandoned mid-negotiation (e.g. the peer disconnects before completing a multi-round
///         handshake) and only ever reclaimed by the GC — matching the guarantee <see cref="SafeHandle" />
///         gives single-<see cref="IntPtr" /> handles. SSPI's <c>PCredHandle</c>/<c>PCtxtHandle</c> are
///         pointers to a two-<see cref="IntPtr" /> struct rather than a single handle value, so they
///         cannot be represented directly by <see cref="SafeHandle" /> itself.
///     </para>
/// </summary>
internal sealed partial class SafeSspiHandle : CriticalFinalizerObject, IDisposable
{
    internal enum HandleKind
    {
        Credential,
        Context
    }

    private readonly HandleKind kind;
    private bool released;

    /// <summary>The raw two-<see cref="IntPtr" /> handle, passed by <c>ref</c>/<c>out</c> directly into the secur32 P/Invoke calls.</summary>
    internal SecurityHandle RawHandle;

    internal SafeSspiHandle(HandleKind kind)
    {
        this.kind = kind;
        RawHandle = new SecurityHandle(0);
    }

    internal bool IsZero => RawHandle.LowPart == IntPtr.Zero && RawHandle.HighPart == IntPtr.Zero;

    /// <summary>
    ///     Releases the native handle exactly once. Safe to call repeatedly and safe to call when the
    ///     handle was never successfully acquired (<see cref="IsZero" />).
    /// </summary>
    internal void Free()
    {
        if (released || IsZero) return;
        released = true;

        try
        {
            if (RunTime.IsWindows)
            {
                if (kind == HandleKind.Credential) FreeCredentialsHandle(ref RawHandle);
                else DeleteSecurityContext(ref RawHandle);
            }
        }
        finally
        {
            RawHandle.Reset();
        }
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~SafeSspiHandle()
    {
        Free();
    }

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int FreeCredentialsHandle(ref SecurityHandle phCredential);

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int DeleteSecurityContext(ref SecurityHandle phContext);
}
