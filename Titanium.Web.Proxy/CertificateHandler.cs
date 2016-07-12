using System;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        /// <summary>
        /// Call back to override server certificate validation
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="sslPolicyErrors"></param>
        /// <returns></returns>
        internal bool ValidateServerCertificate(
          object sender,
          X509Certificate certificate,
          X509Chain chain,
          SslPolicyErrors sslPolicyErrors)
        {
            //if user callback is registered then do it
            if (ServerCertificateValidationCallback != null)
            {
                var args = new CertificateValidationEventArgs();

                args.Certificate = certificate;
                args.Chain = chain;
                args.SslPolicyErrors = sslPolicyErrors;


                Delegate[] invocationList = ServerCertificateValidationCallback.GetInvocationList();
                Task[] handlerTasks = new Task[invocationList.Length];

                for (int i = 0; i < invocationList.Length; i++)
                {
                    handlerTasks[i] = ((Func<object, CertificateValidationEventArgs, Task>)invocationList[i])(null, args);
                }

                Task.WhenAll(handlerTasks).Wait();

                return args.IsValid;
            }

            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            //By default
            //do not allow this client to communicate with unauthenticated servers.
            return false;
        }

        /// <summary>
        /// Call back to select client certificate used for mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="sslPolicyErrors"></param>
        /// <returns></returns>
        internal  X509Certificate SelectClientCertificate(
            object sender,
            string targetHost,
            X509CertificateCollection localCertificates,
            X509Certificate remoteCertificate,
            string[] acceptableIssuers)
        {
            X509Certificate clientCertificate = null;
            var customSslStream = sender as SslStream;

            if (acceptableIssuers != null &&
                acceptableIssuers.Length > 0 &&
                localCertificates != null &&
                localCertificates.Count > 0)
            {
                // Use the first certificate that is from an acceptable issuer.
                foreach (X509Certificate certificate in localCertificates)
                {
                    string issuer = certificate.Issuer;
                    if (Array.IndexOf(acceptableIssuers, issuer) != -1)
                    {
                        clientCertificate = certificate;
                    }
                }
            }

            if (localCertificates != null &&
                localCertificates.Count > 0)
            {
                clientCertificate = localCertificates[0];
            }

            //If user call back is registered
            if (ClientCertificateSelectionCallback != null)
            {
                var args = new CertificateSelectionEventArgs();

                args.targetHost = targetHost;
                args.localCertificates = localCertificates;
                args.remoteCertificate = remoteCertificate;
                args.acceptableIssuers = acceptableIssuers;
                args.clientCertificate = clientCertificate;

                Delegate[] invocationList = ClientCertificateSelectionCallback.GetInvocationList();
                Task[] handlerTasks = new Task[invocationList.Length];

                for (int i = 0; i < invocationList.Length; i++)
                {
                    handlerTasks[i] = ((Func<object, CertificateSelectionEventArgs, Task>)invocationList[i])(null, args);
                }

                Task.WhenAll(handlerTasks).Wait();

                return args.clientCertificate;
            }

            return clientCertificate;
        }
    }
}
