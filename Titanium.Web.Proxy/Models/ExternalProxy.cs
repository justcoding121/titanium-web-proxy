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
        
        public bool UseDefaultCredentials { get; set; }

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

        public string HostName { get; set; }
        public int Port { get; set; }
    }
}
