using System;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Titanium.Web.Proxy.Network.Certificate
{
    /// <summary>
    /// Certificate Maker - uses MakeCert
    /// Calls COM objects using reflection
    /// </summary>
    internal class WinCertificateMaker : ICertificateMaker
    {
        private readonly Type typeX500DN;

        private readonly Type typeX509PrivateKey;

        private readonly Type typeOID;

        private readonly Type typeOIDS;

        private readonly Type typeKUExt;

        private readonly Type typeEKUExt;

        private readonly Type typeRequestCert;

        private readonly Type typeX509Extensions;

        private readonly Type typeBasicConstraints;

        private readonly Type typeSignerCertificate;

        private readonly Type typeX509Enrollment;

        private readonly Type typeAltNamesCollection;

        private readonly Type typeExtNames;

        private readonly Type typeCAlternativeName;

        private readonly string sProviderName = "Microsoft Enhanced Cryptographic Provider v1.0";

        private object sharedPrivateKey;

        /// <summary>
        /// Constructor.
        /// </summary>
        internal WinCertificateMaker()
        {
            typeX500DN = Type.GetTypeFromProgID("X509Enrollment.CX500DistinguishedName", true);
            typeX509PrivateKey = Type.GetTypeFromProgID("X509Enrollment.CX509PrivateKey", true);
            typeOID = Type.GetTypeFromProgID("X509Enrollment.CObjectId", true);
            typeOIDS = Type.GetTypeFromProgID("X509Enrollment.CObjectIds.1", true);
            typeEKUExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionEnhancedKeyUsage");
            typeKUExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionKeyUsage");
            typeRequestCert = Type.GetTypeFromProgID("X509Enrollment.CX509CertificateRequestCertificate");
            typeX509Extensions = Type.GetTypeFromProgID("X509Enrollment.CX509Extensions");
            typeBasicConstraints = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionBasicConstraints");
            typeSignerCertificate = Type.GetTypeFromProgID("X509Enrollment.CSignerCertificate");
            typeX509Enrollment = Type.GetTypeFromProgID("X509Enrollment.CX509Enrollment");

            //for alternative names
            typeAltNamesCollection = Type.GetTypeFromProgID("X509Enrollment.CAlternativeNames");
            typeExtNames = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionAlternativeNames");
            typeCAlternativeName = Type.GetTypeFromProgID("X509Enrollment.CAlternativeName");
        }


        /// <summary>
        /// Make certificate.
        /// </summary>
        /// <param name="sSubjectCN"></param>
        /// <param name="isRoot"></param>
        /// <param name="signingCert"></param>
        /// <returns></returns>
        public X509Certificate2 MakeCertificate(string sSubjectCN, bool isRoot, X509Certificate2 signingCert = null)
        {
            return MakeCertificateInternal(sSubjectCN, isRoot, true, signingCert);
        }

        private X509Certificate2 MakeCertificate(bool isRoot, string subject, string fullSubject,
            int privateKeyLength, string hashAlg, DateTime validFrom, DateTime validTo,
            X509Certificate2 signingCertificate)
        {
            if (isRoot != (null == signingCertificate))
            {
                throw new ArgumentException("You must specify a Signing Certificate if and only if you are not creating a root.", nameof(isRoot));
            }

            var x500CertDN = Activator.CreateInstance(typeX500DN);
            var typeValue = new object[] { fullSubject, 0 };
            typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500CertDN, typeValue);

            var x500RootCertDN = Activator.CreateInstance(typeX500DN);

            if (!isRoot)
            {
                typeValue[0] = signingCertificate.Subject;
            }

            typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500RootCertDN, typeValue);
            object sharedPrivateKey = null;

            if (!isRoot)
            {
                sharedPrivateKey = this.sharedPrivateKey;
            }

            if (sharedPrivateKey == null)
            {
                sharedPrivateKey = Activator.CreateInstance(typeX509PrivateKey);
                typeValue = new object[] { sProviderName };
                typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey, typeValue);
                typeValue[0] = 2;
                typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey, typeValue);
                typeValue = new object[] { isRoot ? 2 : 1 };
                typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey, typeValue);

                if (!isRoot)
                {
                    typeValue = new object[] { 176 };
                    typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey, typeValue);
                }

                typeValue[0] = privateKeyLength;
                typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey, typeValue);
                typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);

                if (!isRoot)
                {
                    this.sharedPrivateKey = sharedPrivateKey;
                }
            }

            typeValue = new object[1];

            var oid = Activator.CreateInstance(typeOID);
            typeValue[0] = "1.3.6.1.5.5.7.3.1";
            typeOID.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, oid, typeValue);

            var oids = Activator.CreateInstance(typeOIDS);
            typeValue[0] = oid;
            typeOIDS.InvokeMember("Add", BindingFlags.InvokeMethod, null, oids, typeValue);

            var ekuExt = Activator.CreateInstance(typeEKUExt);
            typeValue[0] = oids;
            typeEKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, ekuExt, typeValue);

            var requestCert = Activator.CreateInstance(typeRequestCert);

            typeValue = new[] { 1, sharedPrivateKey, string.Empty };
            typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, requestCert, typeValue);
            typeValue = new[] { x500CertDN };
            typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, requestCert, typeValue);
            typeValue[0] = x500RootCertDN;
            typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, requestCert, typeValue);
            typeValue[0] = validFrom;
            typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, requestCert, typeValue);
            typeValue[0] = validTo;
            typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, requestCert, typeValue);

            var kuExt = Activator.CreateInstance(typeKUExt);

            typeValue[0] = 176;
            typeKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, kuExt, typeValue);

            var certificate = typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, requestCert, null);
            typeValue = new object[1];

            if (!isRoot)
            {
                typeValue[0] = kuExt;
                typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
            }

            typeValue[0] = ekuExt;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);

            if (!isRoot)
            {
                //add alternative names 
                // https://forums.iis.net/t/1180823.aspx

                var altNameCollection = Activator.CreateInstance(typeAltNamesCollection);
                var extNames = Activator.CreateInstance(typeExtNames);
                var altDnsNames = Activator.CreateInstance(typeCAlternativeName);

                typeValue = new object[] { 3, subject };
                typeCAlternativeName.InvokeMember("InitializeFromString", BindingFlags.InvokeMethod, null, altDnsNames, typeValue);

                typeValue = new[] { altDnsNames };
                typeAltNamesCollection.InvokeMember("Add", BindingFlags.InvokeMethod, null, altNameCollection, typeValue);


                typeValue = new[] { altNameCollection };
                typeExtNames.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, extNames, typeValue);

                typeValue[0] = extNames;
                typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
            }

            if (!isRoot)
            {
                var signerCertificate = Activator.CreateInstance(typeSignerCertificate);

                typeValue = new object[] { 0, 0, 12, signingCertificate.Thumbprint };
                typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, signerCertificate, typeValue);
                typeValue = new[] { signerCertificate };
                typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, requestCert, typeValue);
            }
            else
            {
                var basicConstraints = Activator.CreateInstance(typeBasicConstraints);

                typeValue = new object[] { "true", "0" };
                typeBasicConstraints.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, basicConstraints, typeValue);
                typeValue = new[] { basicConstraints };
                typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
            }

            oid = Activator.CreateInstance(typeOID);

            typeValue = new object[] { 1, 0, 0, hashAlg };
            typeOID.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, oid, typeValue);

            typeValue = new[] { oid };
            typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, requestCert, typeValue);
            typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, requestCert, null);

            var x509Enrollment = Activator.CreateInstance(typeX509Enrollment);

            typeValue[0] = requestCert;
            typeX509Enrollment.InvokeMember("InitializeFromRequest", BindingFlags.InvokeMethod, null, x509Enrollment, typeValue);

            if (isRoot)
            {
                typeValue[0] = fullSubject;
                typeX509Enrollment.InvokeMember("CertificateFriendlyName", BindingFlags.PutDispProperty, null, x509Enrollment, typeValue);
            }

            var members = typeX509Enrollment.GetMembers();

            typeValue[0] = 0;

            var createCertRequest = typeX509Enrollment.InvokeMember("CreateRequest", BindingFlags.InvokeMethod, null, x509Enrollment, typeValue);
            typeValue = new[] { 2, createCertRequest, 0, string.Empty };

            typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, x509Enrollment, typeValue);
            typeValue = new object[] { null, 0, 1 };

            try
            {
                var empty = (string)typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null, x509Enrollment, typeValue);
                return new X509Certificate2(Convert.FromBase64String(empty), string.Empty, X509KeyStorageFlags.Exportable);
            }
            catch (Exception)
            {
                // ignored
            }

            return null;
        }

        private X509Certificate2 MakeCertificateInternal(string sSubjectCN, bool isRoot,
            bool switchToMTAIfNeeded,
            X509Certificate2 signingCert = null)
        {
            X509Certificate2 rCert = null;
            if (switchToMTAIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                var manualResetEvent = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(o =>
                {
                    rCert = MakeCertificateInternal(sSubjectCN, isRoot, false, signingCert);
                    manualResetEvent.Set();
                });
                manualResetEvent.WaitOne();
                manualResetEvent.Close();
                return rCert;
            }

            //Subject
            var fullSubject = $"CN={sSubjectCN}";
            //Sig Algo
            var HashAlgo = "SHA256";
            //Grace Days
            var GraceDays = -366;
            //ValiDays
            var ValidDays = 1825;
            //KeyLength
            var keyLength = 2048;

            var graceTime = DateTime.Now.AddDays(GraceDays);
            var now = DateTime.Now;
            rCert = MakeCertificate(isRoot, sSubjectCN, fullSubject, keyLength, HashAlgo, graceTime, now.AddDays(ValidDays), isRoot ? null : signingCert);
            return rCert;
        }
    }
}
