using System;
using System.Net;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// An upstream proxy this proxy uses if any
    /// </summary>
    public class ExternalProxy
    {
        private static readonly Lazy<NetworkCredential> DefaultCredentials = new Lazy<NetworkCredential>(() => CredentialCache.DefaultNetworkCredentials);

        private string userName;
        private string password;
        
        /// <summary>
        /// Use default windows credentials?
        /// </summary>
        public bool UseDefaultCredentials { get; set; }

        /// <summary>
        /// Username.
        /// </summary>
        public string UserName {
            get { return UseDefaultCredentials ? DefaultCredentials.Value.UserName : userName; } 
            set
            {
                userName = value;

                if (DefaultCredentials.Value.UserName != userName)
                {
                    UseDefaultCredentials = false;
                }
            }
        }

        /// <summary>
        /// Password.
        /// </summary>
        public string Password
        {
            get { return UseDefaultCredentials ? DefaultCredentials.Value.Password : password; }
            set
            {
                password = value;

                if (DefaultCredentials.Value.Password != password)
                {
                    UseDefaultCredentials = false;
                }
            }
        }

        /// <summary>
        /// Host name.
        /// </summary>
        public string HostName { get; set; }
        /// <summary>
        /// Port.
        /// </summary>
        public int Port { get; set; }
    }
}
