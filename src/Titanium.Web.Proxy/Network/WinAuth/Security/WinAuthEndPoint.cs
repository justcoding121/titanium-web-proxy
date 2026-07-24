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
        var authDataPtr = IntPtr.Zero;

        try
        {
            var state = new State();

            if (credentials != null)
                authDataPtr = MarshalAuthIdentity(credentials);

            var result = AcquireCredentialsHandle(
                credentials == null ? WindowsIdentity.GetCurrent().Name : null!,
                authScheme,
                SecurityCredentialsOutbound,
                IntPtr.Zero,
                authDataPtr,
                0,
                IntPtr.Zero,
                ref state.Credentials,
                ref NewLifeTime);

            if (result != SuccessfulResult) return null;

            result = InitializeSecurityContext(ref state.Credentials,
                IntPtr.Zero,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context,
                out clientToken,
                out NewContextAttributes,
                out NewLifeTime);

            if (result != IntermediateResult && result != SuccessfulResult) return null;

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.FinalToken
                : State.WinAuthState.InitialToken;
            token = clientToken.GetBytes();
            data.Add(AuthStateKey, state);
        }
        finally
        {
            if (authDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(authDataPtr);
            DisposeToken(clientToken);
            DisposeToken(serverToken);
        }

        return token;
    }

    private static IntPtr MarshalAuthIdentity(WinAuthCredentials credentials)
    {
        var user = credentials.UserName ?? string.Empty;
        var domain = credentials.Domain ?? string.Empty;
        var password = credentials.Password ?? string.Empty;
        var identity = new SecWinntAuthIdentity
        {
            User = user,
            UserLength = user.Length,
            Domain = domain,
            DomainLength = domain.Length,
            Password = password,
            PasswordLength = password.Length,
            Flags = SecWinntAuthIdentityUnicode
        };

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<SecWinntAuthIdentity>());
        Marshal.StructureToPtr(identity, ptr, false);
        return ptr;
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

        try
        {
            var state = data.GetAs<State>(AuthStateKey);

            state.UpdatePresence();

            var result = InitializeSecurityContext(ref state.Credentials,
                ref state.Context,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context,
                out clientToken,
                out NewContextAttributes,
                out NewLifeTime);

            // SuccessfulResult => authentication complete.
            // IntermediateResult => another leg is required (multi-round Negotiate).
            if (result != SuccessfulResult && result != IntermediateResult) return null;

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.Authorized
                : State.WinAuthState.FinalToken;
            token = clientToken.GetBytes();
        }
        finally
        {
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
                    var secBufferpvBuffer = Marshal.ReadIntPtr(clientToken.pBuffers,
                        currentOffset + Marshal.SizeOf(typeof(int)) + Marshal.SizeOf(typeof(int)));
                    Marshal.FreeHGlobal(secBufferpvBuffer);
                }
            }

            Marshal.FreeHGlobal(clientToken.pBuffers);
            clientToken.pBuffers = IntPtr.Zero;
        }
    }

    private static void DisposeSecBuffer(SecurityBuffer thisSecBuffer)
    {
        if (thisSecBuffer.pvBuffer != IntPtr.Zero)
        {
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SecWinntAuthIdentity
    {
        public string User;
        public int UserLength;
        public string Domain;
        public int DomainLength;
        public string Password;
        public int PasswordLength;
        public int Flags;
    }

    #endregion
}