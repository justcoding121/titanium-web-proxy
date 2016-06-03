using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// Used to pass in Session object for ServerCertificateValidation Callback
    /// </summary>
    internal class CustomSslStream : SslStream
    {
        /// <summary>
        /// Holds the current session
        /// </summary>
        internal SessionEventArgs Session { get; set; }

        internal CustomSslStream(Stream innerStream, bool leaveInnerStreamOpen, RemoteCertificateValidationCallback userCertificateValidationCallback) 
            :base(innerStream, leaveInnerStreamOpen, userCertificateValidationCallback)
        {

        }
    }
}
