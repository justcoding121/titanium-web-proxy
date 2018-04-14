using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class SslExtensions
    {
        public static readonly List<SslApplicationProtocol> Http11ProtocolAsList = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };

        public static string GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null &&
                clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
            {
                return serverNameExtension.Data;
            }

            return null;
        }

#if NETCOREAPP2_1
        public static List<SslApplicationProtocol> GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("ALPN", out var alpnExtension))
            {
                var alpn = alpnExtension.Data.Split(',');
                if (alpn.Length != 0)
                {
                    var result = new List<SslApplicationProtocol>(alpn.Length);
                    foreach (string p in alpn)
                    {
                        string protocol = p.Trim();
                        if (protocol.Equals("http/1.1"))
                        {
                            result.Add(SslApplicationProtocol.Http11);
                        }
                        else if (protocol.Equals("h2"))
                        {
                            result.Add(SslApplicationProtocol.Http2);
                        }
                    }

                    return result;
                }
            }

            return null;
        }
#else
        public static List<SslApplicationProtocol> GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            return Http11ProtocolAsList;
        }

        public static Task AuthenticateAsClientAsync(this SslStream sslStream, SslClientAuthenticationOptions option, CancellationToken token)
        {
            return sslStream.AuthenticateAsClientAsync(option.TargetHost, option.ClientCertificates, option.EnabledSslProtocols, option.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }

        public static Task AuthenticateAsServerAsync(this SslStream sslStream, SslServerAuthenticationOptions options, CancellationToken token)
        {
            return sslStream.AuthenticateAsServerAsync(options.ServerCertificate, options.ClientCertificateRequired, options.EnabledSslProtocols, options.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }
#endif
    }

#if !NETCOREAPP2_1
    public enum SslApplicationProtocol
    {
        Http11, Http2
    }

    public class SslClientAuthenticationOptions
    {
        public bool AllowRenegotiation { get; set; }

        public string TargetHost { get; set; }

        public X509CertificateCollection ClientCertificates { get; set; }

        public LocalCertificateSelectionCallback LocalCertificateSelectionCallback { get; set; }

        public SslProtocols EnabledSslProtocols { get; set; }

        public X509RevocationMode CertificateRevocationCheckMode { get; set; }

        public List<SslApplicationProtocol> ApplicationProtocols { get; set; }

        public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; }

        public EncryptionPolicy EncryptionPolicy { get; set; }
    }

    public class SslServerAuthenticationOptions
    {
        public bool AllowRenegotiation { get; set; }

        public X509Certificate ServerCertificate { get; set; }

        public bool ClientCertificateRequired { get; set; }

        public SslProtocols EnabledSslProtocols { get; set; }

        public X509RevocationMode CertificateRevocationCheckMode { get; set; }

        public List<SslApplicationProtocol> ApplicationProtocols { get; set; }

        public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; }

        public EncryptionPolicy EncryptionPolicy { get; set; }
    }
#endif
}
