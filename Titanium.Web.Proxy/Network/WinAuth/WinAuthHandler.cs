using Titanium.Web.Proxy.Network.WinAuth.Security;
using System;

namespace Titanium.Web.Proxy.Network.WinAuth
{
    /// <summary>
    /// A handler for NTLM/Kerberos windows authentication challenge from server
    ///  NTLM process details below
    /// https://blogs.msdn.microsoft.com/chiranth/2013/09/20/ntlm-want-to-know-how-it-works/
    /// </summary>
    public static class WinAuthHandler
    {
        /// <summary>
        /// Get the initial client token for server
        /// using credentials of user running the proxy server process
        /// </summary>
        /// <param name="serverHostname"></param>
        /// <param name="authScheme"></param>
        /// <param name="requestId"></param>
        /// <returns></returns>
        public static string GetInitialAuthToken(string serverHostname, 
            string authScheme, Guid requestId)
        {
           var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(serverHostname, authScheme, requestId);
           return string.Concat(" ", Convert.ToBase64String(tokenBytes));
        }


        /// <summary>
        /// Get the final token given the server challenge token
        /// </summary>
        /// <param name="serverHostname"></param>
        /// <param name="serverToken"></param>
        /// <param name="requestId"></param>
        /// <returns></returns>
        public static string GetFinalAuthToken(string serverHostname, 
            string serverToken, Guid requestId)
        {
            var tokenBytes = WinAuthEndPoint.AcquireFinalSecurityToken(serverHostname,
                Convert.FromBase64String(serverToken), requestId);

            return string.Concat(" ", Convert.ToBase64String(tokenBytes));
        }

    }
}
