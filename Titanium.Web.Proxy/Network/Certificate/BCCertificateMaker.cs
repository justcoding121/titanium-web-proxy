using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace Titanium.Web.Proxy.Network.Certificate
{
    /// <summary>
    /// Implements certificate generation operations.
    /// </summary>
    internal class BCCertificateMaker : ICertificateMaker
    {
        private const int certificateValidDays = 1825;
        private const int certificateGraceDays = 366;

        /// <summary>
        /// Makes the certificate.
        /// </summary>
        /// <param name="sSubjectCn">The s subject cn.</param>
        /// <param name="isRoot">if set to <c>true</c> [is root].</param>
        /// <param name="signingCert">The signing cert.</param>
        /// <returns>X509Certificate2 instance.</returns>
        public X509Certificate2 MakeCertificate(string sSubjectCn, bool isRoot, X509Certificate2 signingCert = null)
        {
            return MakeCertificateInternal(sSubjectCn, isRoot, true, signingCert);
        }

        /// <summary>
        /// Generates the certificate.
        /// </summary>
        /// <param name="subjectName">Name of the subject.</param>
        /// <param name="issuerName">Name of the issuer.</param>
        /// <param name="validFrom">The valid from.</param>
        /// <param name="validTo">The valid to.</param>
        /// <param name="keyStrength">The key strength.</param>
        /// <param name="signatureAlgorithm">The signature algorithm.</param>
        /// <param name="issuerPrivateKey">The issuer private key.</param>
        /// <param name="hostName">The host name</param>
        /// <returns>X509Certificate2 instance.</returns>
        /// <exception cref="PemException">Malformed sequence in RSA private key</exception>
        private static X509Certificate2 GenerateCertificate(string hostName,
            string subjectName,
            string issuerName, DateTime validFrom,
            DateTime validTo, int keyStrength = 2048,
            string signatureAlgorithm = "SHA256WithRSA",
            AsymmetricKeyParameter issuerPrivateKey = null)
        {
            // Generating Random Numbers
            var randomGenerator = new CryptoApiRandomGenerator();
            var secureRandom = new SecureRandom(randomGenerator);

            // The Certificate Generator
            var certificateGenerator = new X509V3CertificateGenerator();

            // Serial Number
            var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), secureRandom);
            certificateGenerator.SetSerialNumber(serialNumber);

            // Issuer and Subject Name
            var subjectDn = new X509Name(subjectName);
            var issuerDn = new X509Name(issuerName);
            certificateGenerator.SetIssuerDN(issuerDn);
            certificateGenerator.SetSubjectDN(subjectDn);

            certificateGenerator.SetNotBefore(validFrom);
            certificateGenerator.SetNotAfter(validTo);

            if (hostName != null)
            {
                //add subject alternative names
                var subjectAlternativeNames = new Asn1Encodable[]
                {
                    new GeneralName(GeneralName.DnsName, hostName),
                };

                var subjectAlternativeNamesExtension = new DerSequence(subjectAlternativeNames);
                certificateGenerator.AddExtension(
                    X509Extensions.SubjectAlternativeName.Id, false, subjectAlternativeNamesExtension);
            }
            // Subject Public Key
            var keyGenerationParameters = new KeyGenerationParameters(secureRandom, keyStrength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

            certificateGenerator.SetPublicKey(subjectKeyPair.Public);

            // Set certificate intended purposes to only Server Authentication
            certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage.Id, false, new ExtendedKeyUsage(KeyPurposeID.IdKPServerAuth));

            var signatureFactory = new Asn1SignatureFactory(signatureAlgorithm, issuerPrivateKey ?? subjectKeyPair.Private, secureRandom);

            // Self-sign the certificate
            var certificate = certificateGenerator.Generate(signatureFactory);
            var x509Certificate = new X509Certificate2(certificate.GetEncoded());

            // Corresponding private key
            var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);

            var seq = (Asn1Sequence)Asn1Object.FromByteArray(privateKeyInfo.ParsePrivateKey().GetDerEncoded());

            if (seq.Count != 9)
            {
                throw new PemException("Malformed sequence in RSA private key");
            }

            var rsa = RsaPrivateKeyStructure.GetInstance(seq);
            var rsaparams = new RsaPrivateCrtKeyParameters(rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent, rsa.Prime1, rsa.Prime2, rsa.Exponent1, rsa.Exponent2, rsa.Coefficient);

            // Set private key onto certificate instance
            x509Certificate.PrivateKey = DotNetUtilities.ToRSA(rsaparams);
            x509Certificate.FriendlyName = subjectName;

            return x509Certificate;
        }

        /// <summary>
        /// Makes the certificate internal.
        /// </summary>
        /// <param name="isRoot">if set to <c>true</c> [is root].</param>
        /// <param name="hostName">hostname for certificate</param>
        /// <param name="subjectName">The full subject.</param>
        /// <param name="validFrom">The valid from.</param>
        /// <param name="validTo">The valid to.</param>
        /// <param name="signingCertificate">The signing certificate.</param>
        /// <returns>X509Certificate2 instance.</returns>
        /// <exception cref="System.ArgumentException">You must specify a Signing Certificate if and only if you are not creating a root.</exception>
        private X509Certificate2 MakeCertificateInternal(bool isRoot,
            string hostName, string subjectName,
            DateTime validFrom, DateTime validTo, X509Certificate2 signingCertificate)
        {
            if (isRoot != (null == signingCertificate))
            {
                throw new ArgumentException("You must specify a Signing Certificate if and only if you are not creating a root.", nameof(signingCertificate));
            }

            return isRoot
                ? GenerateCertificate(null, subjectName, subjectName, validFrom, validTo)
                : GenerateCertificate(hostName, subjectName, signingCertificate.Subject, validFrom, validTo, issuerPrivateKey: DotNetUtilities.GetKeyPair(signingCertificate.PrivateKey).Private);
        }

        /// <summary>
        /// Makes the certificate internal.
        /// </summary>
        /// <param name="subject">The s subject cn.</param>
        /// <param name="isRoot">if set to <c>true</c> [is root].</param>
        /// <param name="switchToMtaIfNeeded">if set to <c>true</c> [switch to MTA if needed].</param>
        /// <param name="signingCert">The signing cert.</param>
        /// <param name="cancellationToken">Task cancellation token</param>
        /// <returns>X509Certificate2.</returns>
        private X509Certificate2 MakeCertificateInternal(string subject, bool isRoot,
            bool switchToMtaIfNeeded, X509Certificate2 signingCert = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            X509Certificate2 certificate = null;

            if (switchToMtaIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                using (var manualResetEvent = new ManualResetEventSlim(false))
                {
                    ThreadPool.QueueUserWorkItem(o =>
                    {
                        certificate = MakeCertificateInternal(subject, isRoot, false, signingCert);

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            manualResetEvent?.Set();
                        }
                    });

                    manualResetEvent.Wait(TimeSpan.FromMinutes(1), cancellationToken);
                }

                return certificate;
            }

            return MakeCertificateInternal(isRoot, subject, $"CN={subject}", DateTime.UtcNow.AddDays(-certificateGraceDays), DateTime.UtcNow.AddDays(certificateValidDays), isRoot ? null : signingCert);
        }
    }
}
