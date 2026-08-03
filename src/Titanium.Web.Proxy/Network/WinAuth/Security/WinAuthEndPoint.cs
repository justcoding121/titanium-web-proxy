// http://pinvoke.net/default.aspx/secur32/InitializeSecurityContext.html

using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

using static Common;

internal class WinAuthEndPoint
{
    private const string AuthStateKey = "AuthState";
    private const int SecWinntAuthIdentityUnicode = 0x2;

    /// <summary>
    ///     Acquire the intial client token to send
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static byte[]? AcquireInitialSecurityToken(string hostname, string authScheme, InternalDataStore data,
        int attributes, WinAuthCredentials? credentials = null)
    {
        if (!RunTime.IsWindows) return null;

        byte[]? token;

        // null for initial call
        var serverToken = new SecurityBufferDescription();

        var clientToken = new SecurityBufferDescription(MaximumTokenSize);
        MarshaledAuthIdentity? authIdentity = null;
        var state = new State();
        var stateStored = false;

        try
        {
            if (credentials != null)
                authIdentity = MarshaledAuthIdentity.Create(credentials);

            var lifetime = new SecurityInteger(0);

            var result = AcquireCredentialsHandle(
                credentials == null ? WindowsIdentity.GetCurrent().Name : null!,
                authScheme,
                SecurityCredentialsOutbound,
                IntPtr.Zero,
                authIdentity?.StructPtr ?? IntPtr.Zero,
                0,
                IntPtr.Zero,
                ref state.Credentials.RawHandle,
                ref lifetime);

            if (result != SuccessfulResult) return null;

            result = InitializeSecurityContext(ref state.Credentials.RawHandle,
                IntPtr.Zero,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context.RawHandle,
                out clientToken,
                out _,
                out lifetime);

            if (result != IntermediateResult && result != SuccessfulResult) return null;

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.FinalToken
                : State.WinAuthState.InitialToken;
            token = clientToken.GetBytes();
            data.Add(AuthStateKey, state);
            stateStored = true;
        }
        finally
        {
            // Only the caller that failed to store `state` still owns it; once stored under
            // AuthStateKey, AcquireFinalSecurityToken (or an abandoned negotiation's finalizer) owns
            // its disposal instead.
            if (!stateStored) state.Dispose();
            authIdentity?.Dispose();
            DisposeToken(clientToken);
            DisposeToken(serverToken);
        }

        return token;
    }

    /// <summary>
    ///     Manually marshals a <c>SEC_WINNT_AUTH_IDENTITY</c> so the plaintext password's unmanaged
    ///     buffer is one this code owns directly and can zero before freeing.
    ///     <para>
    ///         The straightforward alternative — a <see cref="Marshal.StructureToPtr{T}" /> struct with
    ///         <see langword="string" /> fields — has the CLR marshaler allocate a hidden unmanaged
    ///         buffer per string that native code never exposes a pointer to, so it can never be zeroed
    ///         before <see cref="Marshal.DestroyStructure{T}" />/<see cref="Marshal.FreeHGlobal" /> frees
    ///         it: the plaintext password would sit in freed-but-unzeroed heap memory indefinitely.
    ///     </para>
    /// </summary>
    private sealed class MarshaledAuthIdentity : IDisposable
    {
        private IntPtr userPtr;
        private IntPtr domainPtr;
        private IntPtr passwordPtr;
        private int passwordLength;

        internal IntPtr StructPtr { get; private set; }

        internal static MarshaledAuthIdentity Create(WinAuthCredentials credentials)
        {
            var result = new MarshaledAuthIdentity();
            try
            {
                var user = credentials.UserName ?? string.Empty;
                var domain = credentials.Domain ?? string.Empty;
                var password = credentials.Password ?? string.Empty;

                result.userPtr = Marshal.StringToHGlobalUni(user);
                result.domainPtr = Marshal.StringToHGlobalUni(domain);
                result.passwordPtr = Marshal.StringToHGlobalUni(password);
                result.passwordLength = password.Length;

                var identity = new SecWinntAuthIdentity
                {
                    User = result.userPtr,
                    UserLength = user.Length,
                    Domain = result.domainPtr,
                    DomainLength = domain.Length,
                    Password = result.passwordPtr,
                    PasswordLength = password.Length,
                    Flags = SecWinntAuthIdentityUnicode
                };

                result.StructPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecWinntAuthIdentity>());
                Marshal.StructureToPtr(identity, result.StructPtr, false);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // Password first and most carefully: it is the actual credential secret. User/Domain are
            // identifiers rather than secrets, but zeroing them too costs nothing and keeps the
            // "no plaintext credential material left in freed heap memory" guarantee uniform.
            ZeroAndFreeUniString(ref passwordPtr, passwordLength);
            ZeroAndFreeUniString(ref userPtr, -1); // length not tracked separately - scan for the terminator
            ZeroAndFreeUniString(ref domainPtr, -1);

            if (StructPtr != IntPtr.Zero)
            {
                // Zero the struct itself too — it only holds pointers/lengths/flags, but leaving stale
                // pointers to (now-freed) buffers around is its own minor hygiene issue.
                var size = Marshal.SizeOf<SecWinntAuthIdentity>();
                for (var i = 0; i < size; i++) Marshal.WriteByte(StructPtr, i, 0);
                Marshal.FreeHGlobal(StructPtr);
                StructPtr = IntPtr.Zero;
            }
        }

        /// <summary>
        ///     Zeroes a <see cref="Marshal.StringToHGlobalUni" />-allocated buffer before freeing it.
        ///     <paramref name="knownCharLength" /> of -1 means "unknown" - the null terminator is
        ///     located by scanning, since <see cref="Marshal.StringToHGlobalUni" /> always produces a
        ///     null-terminated buffer.
        /// </summary>
        private static void ZeroAndFreeUniString(ref IntPtr ptr, int knownCharLength)
        {
            if (ptr == IntPtr.Zero) return;

            var charLength = knownCharLength;
            if (charLength < 0)
            {
                charLength = 0;
                while (Marshal.ReadInt16(ptr, charLength * 2) != 0) charLength++;
            }

            // +1 to also clear the null terminator.
            for (var i = 0; i <= charLength; i++) Marshal.WriteInt16(ptr, i * 2, 0);

            Marshal.FreeHGlobal(ptr);
            ptr = IntPtr.Zero;
        }
    }

    /// <summary>
    ///     Acquire the final token to send
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="serverChallenge"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static byte[]? AcquireFinalSecurityToken(string hostname, byte[] serverChallenge, InternalDataStore data,
        int attributes)
    {
        if (!RunTime.IsWindows) return null;

        byte[]? token;

        // user server challenge
        var serverToken = new SecurityBufferDescription(serverChallenge);

        var clientToken = new SecurityBufferDescription(MaximumTokenSize);
        var state = data.GetAs<State>(AuthStateKey);
        var shouldDisposeState = false;

        try
        {
            state.UpdatePresence();

            var result = InitializeSecurityContext(ref state.Credentials.RawHandle,
                ref state.Context.RawHandle,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context.RawHandle,
                out clientToken,
                out _,
                out _);

            // SuccessfulResult => authentication complete.
            // IntermediateResult => another leg is required (multi-round Negotiate).
            if (result != SuccessfulResult && result != IntermediateResult)
            {
                // Negotiation failed outright: neither handle will be used again.
                shouldDisposeState = true;
                return null;
            }

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.Authorized
                : State.WinAuthState.FinalToken;
            token = clientToken.GetBytes();

            // Authorized is terminal for this flow — WinAuthHandler never calls back in for another
            // round once the peer accepts the final token — so the SSPI handles can be released now
            // rather than waiting for the owning InternalDataStore (and this State) to be collected.
            shouldDisposeState = state.AuthState == State.WinAuthState.Authorized;
        }
        finally
        {
            if (shouldDisposeState) state.Dispose();
            DisposeToken(clientToken);
            DisposeToken(serverToken);
        }

        return token;
    }

    private static void DisposeToken(SecurityBufferDescription clientToken)
    {
        if (clientToken.pBuffers != IntPtr.Zero)
        {
            if (clientToken.cBuffers == 1)
            {
                var thisSecBuffer = Marshal.PtrToStructure<SecurityBuffer>(clientToken.pBuffers);
                DisposeSecBuffer(thisSecBuffer);
            }
            else
            {
                for (var index = 0; index < clientToken.cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // What we need to do here is to grab a hold of the pvBuffer allocate by the individual
                    // SecBuffer and release it...
                    var currentOffset = index * Marshal.SizeOf(typeof(SecurityBuffer));
                    var cbBuffer = Marshal.ReadInt32(clientToken.pBuffers, currentOffset);
                    var secBufferpvBuffer = Marshal.ReadIntPtr(clientToken.pBuffers,
                        currentOffset + Marshal.SizeOf(typeof(int)) + Marshal.SizeOf(typeof(int)));
                    ZeroBuffer(secBufferpvBuffer, cbBuffer);
                    Marshal.FreeHGlobal(secBufferpvBuffer);
                }
            }

            ZeroBuffer(clientToken.pBuffers, clientToken.cBuffers * Marshal.SizeOf(typeof(SecurityBuffer)));
            Marshal.FreeHGlobal(clientToken.pBuffers);
            clientToken.pBuffers = IntPtr.Zero;
        }
    }

    /// <summary>Overwrites an unmanaged buffer with zeros before it is freed (best-effort defense in
    /// depth for the NTLM/Kerberos token bytes these buffers carry - see <see cref="MarshaledAuthIdentity"/>
    /// for the analogous, more critical case of the plaintext password).</summary>
    private static void ZeroBuffer(IntPtr ptr, int length)
    {
        if (ptr == IntPtr.Zero || length <= 0) return;
        for (var i = 0; i < length; i++) Marshal.WriteByte(ptr, i, 0);
    }

    private static void DisposeSecBuffer(SecurityBuffer thisSecBuffer)
    {
        if (thisSecBuffer.pvBuffer != IntPtr.Zero)
        {
            ZeroBuffer(thisSecBuffer.pvBuffer, thisSecBuffer.cbBuffer);
            Marshal.FreeHGlobal(thisSecBuffer.pvBuffer);
            thisSecBuffer.pvBuffer = IntPtr.Zero;
        }
    }

    /// <summary>
    ///     Validates that the current WinAuth state of the connection matches the
    ///     expectation, used to detect failed authentication
    /// </summary>
    /// <param name="data"></param>
    /// <param name="expectedAuthState"></param>
    /// <returns></returns>
    internal static bool ValidateWinAuthState(InternalDataStore data, State.WinAuthState expectedAuthState)
    {
        var stateExists = data.TryGetValueAs(AuthStateKey, out State? state);

        if (expectedAuthState == State.WinAuthState.Unauthorized)
            return !stateExists ||
                   state!.AuthState == State.WinAuthState.Unauthorized ||
                   state.AuthState ==
                   State.WinAuthState.Authorized; // Server may require re-authentication on an open connection

        if (expectedAuthState == State.WinAuthState.InitialToken)
            return stateExists &&
                   (state!.AuthState == State.WinAuthState.InitialToken ||
                    state.AuthState ==
                    State.WinAuthState.Authorized); // Server may require re-authentication on an open connection

        if (expectedAuthState == State.WinAuthState.FinalToken)
            return stateExists &&
                   (state!.AuthState == State.WinAuthState.FinalToken ||
                    state.AuthState == State.WinAuthState.Authorized);

        throw new Exception("Unsupported validation of WinAuthState");
    }

    /// <summary>
    ///     Set the AuthState to authorized and update the connection state lifetime
    /// </summary>
    /// <param name="data"></param>
    internal static void AuthenticatedResponse(InternalDataStore data)
    {
        if (data.TryGetValueAs(AuthStateKey, out State? state))
        {
            state!.AuthState = State.WinAuthState.Authorized;
            state.UpdatePresence();
        }
    }

    #region Native calls to secur32.dll

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int InitializeSecurityContext(ref SecurityHandle phCredential, // PCredHandle
        IntPtr phContext, // PCtxtHandle
        string pszTargetName,
        int fContextReq,
        int reserved1,
        int targetDataRep,
        ref SecurityBufferDescription pInput, // PSecBufferDesc SecBufferDesc
        int reserved2,
        out SecurityHandle phNewContext, // PCtxtHandle
        out SecurityBufferDescription pOutput, // PSecBufferDesc SecBufferDesc
        out uint pfContextAttr, // managed ulong == 64 bits!!!
        out SecurityInteger ptsExpiry); // PTimeStamp

    [DllImport("secur32", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int InitializeSecurityContext(ref SecurityHandle phCredential, // PCredHandle
        ref SecurityHandle phContext, // PCtxtHandle
        string pszTargetName,
        int fContextReq,
        int reserved1,
        int targetDataRep,
        ref SecurityBufferDescription secBufferDesc, // PSecBufferDesc SecBufferDesc
        int reserved2,
        out SecurityHandle phNewContext, // PCtxtHandle
        out SecurityBufferDescription pOutput, // PSecBufferDesc SecBufferDesc
        out uint pfContextAttr, // managed ulong == 64 bits!!!
        out SecurityInteger ptsExpiry); // PTimeStamp

    [DllImport("secur32.dll", CharSet = CharSet.Auto, SetLastError = false)]
    private static extern int AcquireCredentialsHandle(
        string? pszPrincipal, // SEC_CHAR*
        string pszPackage, // SEC_CHAR* // "Kerberos","NTLM","Negotiative"
        int fCredentialUse,
        IntPtr pAuthenticationId, // _LUID AuthenticationID,//pvLogonID, // PLUID
        IntPtr pAuthData, // PVOID
        int pGetKeyFn, // SEC_GET_KEY_FN
        IntPtr pvGetKeyArgument, // PVOID
        ref SecurityHandle phCredential, // SecHandle // PCtxtHandle ref
        ref SecurityInteger ptsExpiry); // PTimeStamp // TimeStamp ref

    /// <summary>
    ///     Field types are deliberately <see cref="IntPtr" /> rather than <see langword="string" />: see
    ///     <see cref="MarshaledAuthIdentity" /> for why automatic string marshaling cannot satisfy the
    ///     "zero the plaintext password before freeing it" requirement.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecWinntAuthIdentity
    {
        public IntPtr User;
        public int UserLength;
        public IntPtr Domain;
        public int DomainLength;
        public IntPtr Password;
        public int PasswordLength;
        public int Flags;
    }

    #endregion
}