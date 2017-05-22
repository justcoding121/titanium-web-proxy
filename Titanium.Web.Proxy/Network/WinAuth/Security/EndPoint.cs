// http://pinvoke.net/default.aspx/secur32/InitializeSecurityContext.html

namespace Titanium.Web.Proxy.Network.WinAuth.Security
{
    using System;
    using System.Runtime.InteropServices;
    using System.Security.Principal;
    using static Common;

    internal class EndPoint
    {
        internal static byte[] AcquireInitialSecurityToken(string hostname)
        {
            byte[] token = null;

            //null for initial call
            SecurityBufferDesciption serverToken
                = new SecurityBufferDesciption();

            SecurityBufferDesciption clientToken
                = new SecurityBufferDesciption(MaximumTokenSize);

            try
            {
                int result;

                var state = new State();

                result = AcquireCredentialsHandle(
                    WindowsIdentity.GetCurrent().Name,
                    "NTLM",
                    Common.SecurityCredentialsOutbound,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    ref state.Credentials, 
                    ref NewLifeTime);

                if (result != SuccessfulResult)
                {
                    // Credentials acquire operation failed.
                    return null;
                }

                result = InitializeSecurityContext(ref state.Credentials,
                    IntPtr.Zero,
                    hostname,
                    StandardContextAttributes,
                    0,
                    SecurityNativeDataRepresentation,
                    ref serverToken,
                    0,
                    out state.Context, 
                    out clientToken,
                    out NewContextAttributes,                   
                    out NewLifeTime);


                if (result != IntermediateResult)
                {
                    // Client challenge issue operation failed.
                    return null;
                }

                token = clientToken.GetBytes();
            }
            finally
            {
                clientToken.Dispose();
                serverToken.Dispose();
            }

            return token;
        }

        /// <summary>
        /// Acquire the final token to send
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="serverChallenge"></param>
        /// <returns></returns>
        internal static byte[] AcquireFinalSecurityToken(string hostname, byte[] serverChallenge)
        {
            byte[] token = null;

            //user server challenge
            SecurityBufferDesciption serverToken
                = new SecurityBufferDesciption(serverChallenge);

            SecurityBufferDesciption clientToken
                = new SecurityBufferDesciption(MaximumTokenSize);

            try
            {
                int result;

                var state = new State();

                result = AcquireCredentialsHandle(
                    WindowsIdentity.GetCurrent().Name,
                    "NTLM",
                    SecurityCredentialsOutbound,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    ref state.Credentials,
                    ref NewLifeTime);

                if (result != SuccessfulResult)
                {
                    // Credentials acquire operation failed.
                    return null;
                }

                result = InitializeSecurityContext(ref state.Credentials,
                    IntPtr.Zero,
                    hostname,
                    StandardContextAttributes,
                    0,
                    SecurityNativeDataRepresentation,
                    ref serverToken,
                    0,
                    out state.Context,
                    out clientToken,
                    out NewContextAttributes,
                    out NewLifeTime);


                if (result != SuccessfulResult)
                {
                    // Client challenge issue operation failed.
                    return null;
                }

                token = clientToken.GetBytes();
            }
            finally
            {
                clientToken.Dispose();
                serverToken.Dispose();
            }

            return token;
        }
        #region Native calls to secur32.dll

        [DllImport("secur32.dll", SetLastError = true)]
        static extern int InitializeSecurityContext(ref SecurityHandle phCredential,//PCredHandle
        IntPtr phContext, //PCtxtHandle
        string pszTargetName,
        int fContextReq,
        int Reserved1,
        int TargetDataRep,
        ref SecurityBufferDesciption pInput, //PSecBufferDesc SecBufferDesc
        int Reserved2,
        out SecurityHandle phNewContext, //PCtxtHandle
        out Common.SecurityBufferDesciption pOutput, //PSecBufferDesc SecBufferDesc
        out uint pfContextAttr, //managed ulong == 64 bits!!!
        out Common.SecurityInteger ptsExpiry); //PTimeStamp

        [DllImport("secur32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern int AcquireCredentialsHandle(
            string pszPrincipal,                            //SEC_CHAR*
            string pszPackage,                              //SEC_CHAR* //"Kerberos","NTLM","Negotiative"
            int fCredentialUse,
            IntPtr PAuthenticationID,                       //_LUID AuthenticationID,//pvLogonID, //PLUID
            IntPtr pAuthData,                               //PVOID
            int pGetKeyFn,                                  //SEC_GET_KEY_FN
            IntPtr pvGetKeyArgument,                        //PVOID
            ref Common.SecurityHandle phCredential,                        //SecHandle //PCtxtHandle ref
            ref Common.SecurityInteger ptsExpiry);                         //PTimeStamp //TimeStamp ref

  
        #endregion
    }
}
