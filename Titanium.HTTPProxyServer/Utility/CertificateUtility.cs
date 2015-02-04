using System;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;

namespace Titanium.HTTPProxyServer
{
    public partial class ProxyServer
    {
        static X509Store store;
        private static X509Certificate2 getCertificate(string hostname)
        {
           
                if (store == null)
                {
                    store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadOnly);
                }
                var CachedCertificate = getCertifiateFromStore(hostname); 
                
                if(CachedCertificate==null) 
                CreateClientCertificate(hostname);

                return getCertifiateFromStore(hostname);

           
        }
        public static X509Certificate2 getCertifiateFromStore(string hostname)
        {
   
              X509Certificate2Collection certificateCollection = store.Certificates.Find(X509FindType.FindBySubjectName, hostname, true);

                foreach(var certificate in certificateCollection)
                {
                    if (certificate.SubjectName.Name.StartsWith("CN=" + hostname) && certificate.IssuerName.Name.StartsWith("CN=Titanium_Proxy_Test_Root"))
                        return certificate;
                }
                return null;
        }

        public static void CreateClientCertificate(string hostname)
        {
           
            Process process = new Process();

            // Stop the process from opening a new window
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            // Setup executable and parameters
            process.StartInfo.FileName = @"makecert.exe";
            process.StartInfo.Arguments = @"-pe -ss my -n ""CN=" + hostname + @""" -sky exchange -in Titanium_Proxy_Test_Root -is my -eku 1.3.6.1.5.5.7.3.1 -cy end -a sha1 -m 132 -b 10/07/2012";

            // Go
            process.Start();
            process.WaitForExit();
           

        }
        #region old
        //public  System.Security.Cryptography.X509Certificates.X509Certificate2 CreateClientCertificate(string hostname,  System.Security.Cryptography.X509Certificates.X509Certificate2 rootCertificate)
        //{
        //    // get the server certificate
        //    Org.BouncyCastle.X509.X509Certificate CA = DotNetUtilities.FromX509Certificate(rootCertificate);

        //    AsymmetricKeyParameter bouncyCastlePrivateKey = TransformRSAPrivateKey(rootCertificate.PrivateKey);

        //    var kpgen = new RsaKeyPairGenerator();
        //    kpgen.Init(new KeyGenerationParameters(new SecureRandom(new CryptoApiRandomGenerator()), 1024));

        //    var kp = kpgen.GenerateKeyPair();

        //    // generate the client certificate
        //    X509V3CertificateGenerator generator = new X509V3CertificateGenerator();

        //    generator.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        //    generator.SetIssuerDN(CA.SubjectDN);
        //    generator.SetNotBefore(DateTime.Now);
        //    generator.SetNotAfter(DateTime.Now.AddYears(5));
        //    generator.SetSubjectDN(new X509Name("CN=" + hostname));
        //    generator.SetPublicKey(kp.Public);
        //    generator.SetSignatureAlgorithm("SHA1withRSA");
        //    generator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, new AuthorityKeyIdentifierStructure(CA));
        //    generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, new SubjectKeyIdentifierStructure(kp.Public));

        //    var newClientCert = generator.Generate(bouncyCastlePrivateKey);
                  
        //  //  newClientCert.Verify(CA.GetPublicKey());
         

        //   var cert = new X509Certificate2(Org.BouncyCastle.Security.DotNetUtilities.ToX509Certificate(newClientCert));


        //   cert.PrivateKey = getDotNetPrivateKey(kp.Private);
        //   return cert;
        //}
        //private  AsymmetricKeyParameter TransformRSAPrivateKey(AsymmetricAlgorithm privateKey)
        //{
        //    RSACryptoServiceProvider prov = privateKey as RSACryptoServiceProvider;
        //    RSAParameters parameters = prov.ExportParameters(true);

        //    return new RsaPrivateCrtKeyParameters(
        //        new BigInteger(1, parameters.Modulus),
        //        new BigInteger(1, parameters.Exponent),
        //        new BigInteger(1, parameters.D),
        //        new BigInteger(1, parameters.P),
        //        new BigInteger(1, parameters.Q),
        //        new BigInteger(1, parameters.DP),
        //        new BigInteger(1, parameters.DQ),
        //        new BigInteger(1, parameters.InverseQ));
        //}

        //private  RSACryptoServiceProvider getDotNetPrivateKey(AsymmetricKeyParameter kp)
        //{

        //    // Apparently, using DotNetUtilities to convert the private key is a little iffy. Have to do some init up front.
        //    RSACryptoServiceProvider tempRcsp = (RSACryptoServiceProvider)DotNetUtilities.ToRSA((RsaPrivateCrtKeyParameters)kp);
        //    RSACryptoServiceProvider rcsp = new RSACryptoServiceProvider(new CspParameters(1, "Microsoft Strong Cryptographic Provider",
        //                new Guid().ToString(),
        //                new CryptoKeySecurity(), null));

        //    rcsp.ImportCspBlob(tempRcsp.ExportCspBlob(true));
        //    return rcsp;
        //}
        #endregion
    }
}
