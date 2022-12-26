using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class SslExtensions
    {
        internal static readonly List<SslApplicationProtocol> Http11ProtocolAsList =
            new() { SslApplicationProtocol.Http11 };

        internal static readonly List<SslApplicationProtocol> Http2ProtocolAsList =
            new() { SslApplicationProtocol.Http2 };

        internal static string? GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null &&
                clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
                return serverNameExtension.Data;

            return null;
        }

#if NET6_0_OR_GREATER
        internal static List<SslApplicationProtocol>? GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("ALPN", out var alpnExtension))
            {
                var alpn = alpnExtension.Alpns;
                if (alpn.Count != 0)
                {
                    return alpn;
                }
            }

            return null;
        }

        internal static List<string>? GetSslProtocols(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("supported_versions", out var versions))
            {
                var protocols = versions.Protocols;
                if (protocols.Count != 0)
                {
                    return protocols;
                }
            }

            return null;
        }
#else
        internal static List<SslApplicationProtocol> GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            return Http11ProtocolAsList;
        }

        internal static Task AuthenticateAsClientAsync(this SslStream sslStream, SslClientAuthenticationOptions option,
            CancellationToken token)
        {
            return sslStream.AuthenticateAsClientAsync(option.TargetHost, option.ClientCertificates,
                option.EnabledSslProtocols, option.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }

        internal static Task AuthenticateAsServerAsync(this SslStream sslStream, SslServerAuthenticationOptions options,
            CancellationToken token)
        {
            return sslStream.AuthenticateAsServerAsync(options.ServerCertificate, options.ClientCertificateRequired,
                options.EnabledSslProtocols, options.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }
#endif
    }
}

#if !NET6_0_OR_GREATER
namespace System.Net.Security
{
    internal struct SslApplicationProtocol
    {
        public static readonly SslApplicationProtocol Http11 = new SslApplicationProtocol(SslExtension.Http11Utf8);

        public static readonly SslApplicationProtocol Http2 = new SslApplicationProtocol(SslExtension.Http2Utf8);
        
        public static readonly SslApplicationProtocol Http3 = new SslApplicationProtocol(SslExtension.Http3Utf8);

        private readonly byte[] readOnlyProtocol;

        public ReadOnlyMemory<byte> Protocol => readOnlyProtocol;

        public SslApplicationProtocol(byte[] protocol)
        {
            readOnlyProtocol = protocol;
        }

        public bool Equals(SslApplicationProtocol other) => Protocol.Span.SequenceEqual(other.Protocol.Span);

        public override bool Equals(object? obj) => obj is SslApplicationProtocol protocol && Equals(protocol);

        public override int GetHashCode()
        {
            var arr = Protocol;
            if (arr.Length == 0)
            {
                return 0;
            }

            int hash = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                hash = ((hash << 5) + hash) ^ arr.Span[i];
            }

            return hash;
        }

        public override string ToString()
        {
            return Encoding.UTF8.GetString(readOnlyProtocol);
        }

        public static bool operator ==(SslApplicationProtocol left, SslApplicationProtocol right) =>
            left.Equals(right);

        public static bool operator !=(SslApplicationProtocol left, SslApplicationProtocol right) =>
            !(left == right);
    }

    [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType", Justification =
        "Reviewed.")]
    internal class SslClientAuthenticationOptions
    {
        internal bool AllowRenegotiation { get; set; }

        internal string? TargetHost { get; set; }

        internal X509CertificateCollection? ClientCertificates { get; set; }

        internal LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get; set; }

        internal SslProtocols EnabledSslProtocols { get; set; }

        internal X509RevocationMode CertificateRevocationCheckMode { get; set; }

        internal List<SslApplicationProtocol>? ApplicationProtocols { get; set; }

        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        internal EncryptionPolicy EncryptionPolicy { get; set; }
    }

    internal class SslServerAuthenticationOptions
    {
        internal bool AllowRenegotiation { get; set; }

        internal X509Certificate? ServerCertificate { get; set; }

        internal bool ClientCertificateRequired { get; set; }

        internal SslProtocols EnabledSslProtocols { get; set; }

        internal X509RevocationMode CertificateRevocationCheckMode { get; set; }

        internal List<SslApplicationProtocol>? ApplicationProtocols { get; set; }

        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        internal EncryptionPolicy EncryptionPolicy { get; set; }
    }
}
#endif