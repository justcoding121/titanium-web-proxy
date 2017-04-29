using System;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Titanium.Web.Proxy.Network.Certificate
{

    /// <summary>
    /// Certificate Maker - uses MakeCert
    /// </summary>
    public class WinCertificateMaker: ICertificateMaker
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

        private readonly string sProviderName = "Microsoft Enhanced Cryptographic Provider v1.0";

        private object _SharedPrivateKey;

        /// <summary>
        /// Constructor.
        /// </summary>
        public WinCertificateMaker()
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
        }

        /// <summary>
        /// Make certificate.
        /// </summary>
        /// <param name="sSubjectCN"></param>
        /// <param name="isRoot"></param>
        /// <param name="signingCert"></param>
        /// <returns></returns>
        public X509Certificate2 MakeCertificate(string sSubjectCN, bool isRoot,X509Certificate2 signingCert=null)
        {
            return MakeCertificateInternal(sSubjectCN, isRoot, true, signingCert);
        }

        private X509Certificate2 MakeCertificate(bool IsRoot, string FullSubject, int PrivateKeyLength, string HashAlg, DateTime ValidFrom, DateTime ValidTo, X509Certificate2 SigningCertificate)
        {
            if (IsRoot != (null == SigningCertificate))
            {
                throw new ArgumentException("You must specify a Signing Certificate if and only if you are not creating a root.", nameof(IsRoot));
            }
            var x500DN = Activator.CreateInstance(typeX500DN);
            var subject = new object[] { FullSubject, 0 };
            typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500DN, subject);
            var x500DN2 = Activator.CreateInstance(typeX500DN);
            if (!IsRoot)
            {
                subject[0] = SigningCertificate.Subject;
            }
            typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500DN2, subject);
            object sharedPrivateKey = null;
            if (!IsRoot)
            {
                sharedPrivateKey = _SharedPrivateKey;
            }
            if (sharedPrivateKey == null)
            {
                sharedPrivateKey = Activator.CreateInstance(typeX509PrivateKey);
                subject = new object[] { sProviderName };
                typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                subject[0] = 2;
                typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                subject = new object[] { (IsRoot ? 2 : 1) };
                typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                if (!IsRoot)
                {
                    subject = new object[] { 176 };
                    typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                }
                subject[0] = PrivateKeyLength;
                typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);
                if (!IsRoot)
                {
                    _SharedPrivateKey = sharedPrivateKey;
                }
            }
            subject = new object[1];
            var obj3 = Activator.CreateInstance(typeOID);
            subject[0] = "1.3.6.1.5.5.7.3.1";
            typeOID.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, obj3, subject);
            var obj4 = Activator.CreateInstance(typeOIDS);
            subject[0] = obj3;
            typeOIDS.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj4, subject);
            var obj5 = Activator.CreateInstance(typeEKUExt);
            subject[0] = obj4;
            typeEKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj5, subject);
            var obj6 = Activator.CreateInstance(typeRequestCert);
            subject = new[] { 1, sharedPrivateKey, string.Empty };
            typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, obj6, subject);
            subject = new[] { x500DN };
            typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = x500DN;
            typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = ValidFrom;
            typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = ValidTo;
            typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, obj6, subject);
            var obj7 = Activator.CreateInstance(typeKUExt);
            subject[0] = 176;
            typeKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj7, subject);
            var obj8 = typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, obj6, null);
            subject = new object[1];
            if (!IsRoot)
            {
                subject[0] = obj7;
                typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);
            }
            subject[0] = obj5;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);

            if (!IsRoot)
            {
                var obj12 = Activator.CreateInstance(typeSignerCertificate);
                subject = new object[] { 0, 0, 12, SigningCertificate.Thumbprint };
                typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, obj12, subject);
                subject = new[] { obj12 };
                typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, obj6, subject);
            }
            else
            {
                var obj13 = Activator.CreateInstance(typeBasicConstraints);
                subject = new object[] { "true", "0" };
                typeBasicConstraints.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj13, subject);
                subject = new[] { obj13 };
                typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);
            }
            var obj14 = Activator.CreateInstance(typeOID);
            subject = new object[] { 1, 0, 0, HashAlg };
            typeOID.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, obj14, subject);
            subject = new[] { obj14 };
            typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, obj6, subject);
            typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, obj6, null);
            var obj15 = Activator.CreateInstance(typeX509Enrollment);
            subject[0] = obj6;
            typeX509Enrollment.InvokeMember("InitializeFromRequest", BindingFlags.InvokeMethod, null, obj15, subject);
            if (IsRoot)
            {
                subject[0] = "DO_NOT_TRUST_TitaniumProxy-CE";
                typeX509Enrollment.InvokeMember("CertificateFriendlyName", BindingFlags.PutDispProperty, null, obj15, subject);
            }
            subject[0] = 0;
            var obj16 = typeX509Enrollment.InvokeMember("CreateRequest", BindingFlags.InvokeMethod, null, obj15, subject);
            subject = new[] { 2, obj16, 0, string.Empty };
            typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, obj15, subject);
            subject = new object[] { null, 0, 1 };
            try
            {
                var empty = (string)typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null, obj15, subject);
                return new X509Certificate2(Convert.FromBase64String(empty), string.Empty, X509KeyStorageFlags.Exportable);
            }
            catch (Exception)
            {
                // ignored
            }
            return null;
        }

        private X509Certificate2 MakeCertificateInternal(string sSubjectCN, bool isRoot, bool switchToMTAIfNeeded,X509Certificate2 signingCert=null)
        {
            X509Certificate2 rCert=null;
            if (switchToMTAIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                var manualResetEvent = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(o =>
                {
                    rCert = MakeCertificateInternal(sSubjectCN, isRoot, false,signingCert);
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
            rCert = !isRoot ? MakeCertificate(false, fullSubject, keyLength, HashAlgo, graceTime, now.AddDays(ValidDays), signingCert) : 
                MakeCertificate(true, fullSubject, keyLength, HashAlgo, graceTime, now.AddDays(ValidDays), null);
            return rCert;
        }
    }

}
