using System;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Crypto.Parameters;

namespace Titanium.Web.Proxy.Network
{

    public class CertificateMaker
    {
        private AsymmetricKeyParameter privateKey;

        public CertificateMaker()
        {
        }

        public X509Certificate2 MakeCertificate(string sSubjectCN, bool isRoot,X509Certificate2 signingCert=null)
        {
			return this.MakeCertificateInternal (sSubjectCN, isRoot, true, signingCert);
        }

        private X509Certificate2 MakeCertificate(bool IsRoot, string SubjectCN, string FullSubject, int PrivateKeyLength, string HashAlg, DateTime ValidFrom, DateTime ValidTo, X509Certificate2 SigningCertificate)
        {
            if (IsRoot != (null == SigningCertificate))
            {
                throw new ArgumentException("You must specify a Signing Certificate if and only if you are not creating a root.", "oSigningCertificate");
            }

			return MakeCertificateMono (IsRoot, SubjectCN, FullSubject, PrivateKeyLength, HashAlg, ValidFrom, ValidTo, SigningCertificate);
        }

		private X509Certificate2 MakeCertificateMono(bool IsRoot, string SubjectCN, string FullSubject, int PrivateKeyLength, string HashAlg, DateTime ValidFrom, DateTime ValidTo, X509Certificate2 SigningCertificate) {
			// Generating Random Numbers
			var randomGenerator = new CryptoApiRandomGenerator();
			var random = new SecureRandom(randomGenerator);

			// The Certificate Generator
			var certificateGenerator = new X509V3CertificateGenerator();

			// Serial Number
			var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
			certificateGenerator.SetSerialNumber(serialNumber);

			// Signature Algorithm
			certificateGenerator.SetSignatureAlgorithm(HashAlg);		

            if (IsRoot)
            {
                var subjectDN = new X509Name(FullSubject);
                var issuerDN = subjectDN;
                certificateGenerator.SetIssuerDN(issuerDN);
                certificateGenerator.SetSubjectDN(subjectDN);
            }
            else
            {
                // Issuer and Subject Name
                var subjectDN = new X509Name(FullSubject);
                var issuerDN = new X509Name("CN=Titanium Root Certificate Authority");
                certificateGenerator.SetIssuerDN(issuerDN);
                certificateGenerator.SetSubjectDN(subjectDN);
            }

			certificateGenerator.SetNotBefore(ValidFrom);
			certificateGenerator.SetNotAfter(ValidTo);		

			int keyStrength = 2048;

			// Subject Public Key
			AsymmetricCipherKeyPair subjectKeyPair;
			var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
			var keyPairGenerator = new RsaKeyPairGenerator();
			keyPairGenerator.Init(keyGenerationParameters);
			subjectKeyPair = keyPairGenerator.GenerateKeyPair();

			certificateGenerator.SetPublicKey(subjectKeyPair.Public);

			// Generating the Certificate
			var issuerKeyPair = subjectKeyPair;

            if (IsRoot)
            {
                // selfsign certificate
                var certificate = certificateGenerator.Generate(issuerKeyPair.Private, random);
                var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.GetEncoded());
                privateKey = issuerKeyPair.Private;

                return x509;
            }
            else
            {
                var certificate = certificateGenerator.Generate(privateKey, random);

                // correcponding private key
                PrivateKeyInfo info = PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);

                // merge into X509Certificate2
                var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.GetEncoded());

                var seq = (Asn1Sequence)Asn1Object.FromByteArray(info.PrivateKey.GetDerEncoded());
                if (seq.Count != 9)
                    throw new PemException("malformed sequence in RSA private key");

                var rsa = new RsaPrivateKeyStructure(seq);
                RsaPrivateCrtKeyParameters rsaparams = new RsaPrivateCrtKeyParameters(
                    rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent, rsa.Prime1, rsa.Prime2, rsa.Exponent1, rsa.Exponent2, rsa.Coefficient);

                x509.PrivateKey = DotNetUtilities.ToRSA(rsaparams);
                return x509;
            }                
		}

        private X509Certificate2 MakeCertificateInternal(string sSubjectCN, bool isRoot, bool switchToMTAIfNeeded,X509Certificate2 signingCert=null)
        {			
            X509Certificate2 rCert=null;

            if (switchToMTAIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                ManualResetEvent manualResetEvent = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem((object o) =>
                {
                    rCert = this.MakeCertificateInternal(sSubjectCN, isRoot, false,signingCert);
                    manualResetEvent.Set();
                });
                manualResetEvent.WaitOne();
                manualResetEvent.Close();
                return rCert;
            }

            string fullSubject = string.Format("CN={0}{1}", sSubjectCN, "");//Subject
            string HashAlgo = "SHA256WithRSA";  //Sig Algo
            int GraceDays = -366; //Grace Days
            int ValidDays = 1825; //ValiDays
            int keyLength = 2048; //KeyLength

            DateTime graceTime = DateTime.Now.AddDays((double)GraceDays);
            DateTime now = DateTime.Now;
            try
            {
                if (!isRoot)
                {
                    rCert = this.MakeCertificate(false, sSubjectCN, fullSubject, keyLength, HashAlgo, graceTime, now.AddDays((double)ValidDays), signingCert);
                }
                else
                {
                    rCert = this.MakeCertificate(true, sSubjectCN, fullSubject, keyLength, HashAlgo, graceTime, now.AddDays((double)ValidDays), null);
                }
            }
            catch (Exception e)
            {
                throw e;
            }
            return rCert;
        }
    }

}
