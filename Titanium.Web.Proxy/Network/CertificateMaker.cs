using System;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Titanium.Web.Proxy.Network
{

    public class CertificateMaker
    {
        private Type typeX500DN;

        private Type typeX509PrivateKey;

        private Type typeOID;

        private Type typeOIDS;

        private Type typeKUExt;

        private Type typeEKUExt;

        private Type typeRequestCert;

        private Type typeX509Extensions;

        private Type typeBasicConstraints;

        private Type typeSignerCertificate;

        private Type typeX509Enrollment;

        private Type typeAlternativeName;

        private Type typeAlternativeNames;

        private Type typeAlternativeNamesExt;

        private string sProviderName = "Microsoft Enhanced Cryptographic Provider v1.0";

        private object _SharedPrivateKey;

        public CertificateMaker()
        {
            this.typeX500DN = Type.GetTypeFromProgID("X509Enrollment.CX500DistinguishedName", true);
            this.typeX509PrivateKey = Type.GetTypeFromProgID("X509Enrollment.CX509PrivateKey", true);
            this.typeOID = Type.GetTypeFromProgID("X509Enrollment.CObjectId", true);
            this.typeOIDS = Type.GetTypeFromProgID("X509Enrollment.CObjectIds.1", true);
            this.typeEKUExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionEnhancedKeyUsage");
            this.typeKUExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionKeyUsage");
            this.typeRequestCert = Type.GetTypeFromProgID("X509Enrollment.CX509CertificateRequestCertificate");
            this.typeX509Extensions = Type.GetTypeFromProgID("X509Enrollment.CX509Extensions");
            this.typeBasicConstraints = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionBasicConstraints");
            this.typeSignerCertificate = Type.GetTypeFromProgID("X509Enrollment.CSignerCertificate");
            this.typeX509Enrollment = Type.GetTypeFromProgID("X509Enrollment.CX509Enrollment");
            this.typeAlternativeName = Type.GetTypeFromProgID("X509Enrollment.CAlternativeName");
            this.typeAlternativeNames = Type.GetTypeFromProgID("X509Enrollment.CAlternativeNames");
            this.typeAlternativeNamesExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionAlternativeNames");
        }

        public X509Certificate2 MakeCertificate(string sSubjectCN, bool isRoot,X509Certificate2 signingCert=null)
        {
            return this.MakeCertificateInternal(sSubjectCN, isRoot, true, signingCert);
        }

        private X509Certificate2 MakeCertificate(bool IsRoot, string SubjectCN, string FullSubject, int PrivateKeyLength, string HashAlg, DateTime ValidFrom, DateTime ValidTo, X509Certificate2 SigningCertificate)
        {
            X509Certificate2 cert;
            if (IsRoot != (null == SigningCertificate))
            {
                throw new ArgumentException("You must specify a Signing Certificate if and only if you are not creating a root.", "oSigningCertificate");
            }
            object x500DN = Activator.CreateInstance(this.typeX500DN);
            object[] subject = new object[] { FullSubject, 0 };
            this.typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500DN, subject);
            object x500DN2 = Activator.CreateInstance(this.typeX500DN);
            if (!IsRoot)
            {
                subject[0] = SigningCertificate.Subject;
            }
            this.typeX500DN.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500DN2, subject);
            object sharedPrivateKey = null;
            if (!IsRoot)
            {
                sharedPrivateKey = this._SharedPrivateKey;
            }
            if (sharedPrivateKey == null)
            {
                sharedPrivateKey = Activator.CreateInstance(this.typeX509PrivateKey);
                subject = new object[] { this.sProviderName };
                this.typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                subject[0] = 2;
                this.typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                subject = new object[] { (IsRoot ? 2 : 1) };
                this.typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                if (!IsRoot)
                {
                    subject = new object[] { 176 };
                    this.typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                }
                subject[0] = PrivateKeyLength;
                this.typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey, subject);
                this.typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);
                if (!IsRoot)
                {
                    this._SharedPrivateKey = sharedPrivateKey;
                }
            }
            subject = new object[1];
            object obj3 = Activator.CreateInstance(this.typeOID);
            subject[0] = "1.3.6.1.5.5.7.3.1";
            this.typeOID.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, obj3, subject);
            object obj4 = Activator.CreateInstance(this.typeOIDS);
            subject[0] = obj3;
            this.typeOIDS.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj4, subject);
            object obj5 = Activator.CreateInstance(this.typeEKUExt);
            subject[0] = obj4;
            this.typeEKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj5, subject);
            object obj6 = Activator.CreateInstance(this.typeRequestCert);
            subject = new object[] { 1, sharedPrivateKey, string.Empty };
            this.typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, obj6, subject);
            subject = new object[] { x500DN };
            this.typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = x500DN;
            this.typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = ValidFrom;
            this.typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, obj6, subject);
            subject[0] = ValidTo;
            this.typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, obj6, subject);
            object obj7 = Activator.CreateInstance(this.typeKUExt);
            subject[0] = 176;
            this.typeKUExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj7, subject);
            object obj8 = this.typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, obj6, null);
            subject = new object[1];
            if (!IsRoot)
            {
                subject[0] = obj7;
                this.typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);
            }
            subject[0] = obj5;
            this.typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);

            if (!IsRoot)
            {
                object obj12 = Activator.CreateInstance(this.typeSignerCertificate);
                subject = new object[] { 0, 0, 12, SigningCertificate.Thumbprint };
                this.typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, obj12, subject);
                subject = new object[] { obj12 };
                this.typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, obj6, subject);
            }
            else
            {
                object obj13 = Activator.CreateInstance(this.typeBasicConstraints);
                subject = new object[] { "true", "0" };
                this.typeBasicConstraints.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, obj13, subject);
                subject = new object[] { obj13 };
                this.typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, obj8, subject);
            }
            object obj14 = Activator.CreateInstance(this.typeOID);
            subject = new object[] { 1, 0, 0, HashAlg };
            this.typeOID.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, obj14, subject);
            subject = new object[] { obj14 };
            this.typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, obj6, subject);
            this.typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, obj6, null);
            object obj15 = Activator.CreateInstance(this.typeX509Enrollment);
            subject[0] = obj6;
            this.typeX509Enrollment.InvokeMember("InitializeFromRequest", BindingFlags.InvokeMethod, null, obj15, subject);
            if (IsRoot)
            {
                subject[0] = "DO_NOT_TRUST_TitaniumProxy-CE";
                this.typeX509Enrollment.InvokeMember("CertificateFriendlyName", BindingFlags.PutDispProperty, null, obj15, subject);
            }
            subject[0] = 0;
            object obj16 = this.typeX509Enrollment.InvokeMember("CreateRequest", BindingFlags.InvokeMethod, null, obj15, subject);
            subject = new object[] { 2, obj16, 0, string.Empty };
            this.typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, obj15, subject);
            subject = new object[] { null, 0, 1 };
            string empty = string.Empty;
            try
            {
                empty = (string)this.typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null, obj15, subject);
                return new X509Certificate2(Convert.FromBase64String(empty), string.Empty, X509KeyStorageFlags.Exportable);
            }
            catch (Exception exception1)
            {
                Exception exception = exception1;
                cert = null;
            }
            return cert;
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
            string HashAlgo = "SHA256";  //Sig Algo
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
