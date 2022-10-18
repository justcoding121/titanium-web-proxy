using System;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <inheritdoc />
/// <summary>
///     Certificate Maker - uses MakeCert
///     Calls COM objects using reflection
/// </summary>
internal class WinCertificateMaker : ICertificateMaker
{
    private readonly ExceptionHandler? exceptionFunc;

    private readonly string sProviderName = "Microsoft Enhanced Cryptographic Provider v1.0";

    private readonly Type typeAltNamesCollection;

    private readonly Type typeBasicConstraints;

    private readonly Type typeCAlternativeName;

    private readonly Type typeEkuExt;

    private readonly Type typeExtNames;

    private readonly Type typeKuExt;

    private readonly Type typeOid;

    private readonly Type typeOids;

    private readonly Type typeRequestCert;

    private readonly Type typeSignerCertificate;
    private readonly Type typeX500Dn;

    private readonly Type typeX509Enrollment;

    private readonly Type typeX509Extensions;

    private readonly Type typeX509PrivateKey;

    // Validity Days for Root Certificates Generated.
    private readonly int certificateValidDays;

    private object? sharedPrivateKey;

    /// <summary>
    ///     Constructor.
    /// </summary>
    internal WinCertificateMaker(ExceptionHandler? exceptionFunc, int certificateValidDays)
    {
        this.certificateValidDays = certificateValidDays;
        this.exceptionFunc = exceptionFunc;

        typeX500Dn = Type.GetTypeFromProgID("X509Enrollment.CX500DistinguishedName", true);
        typeX509PrivateKey = Type.GetTypeFromProgID("X509Enrollment.CX509PrivateKey", true);
        typeOid = Type.GetTypeFromProgID("X509Enrollment.CObjectId", true);
        typeOids = Type.GetTypeFromProgID("X509Enrollment.CObjectIds.1", true);
        typeEkuExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionEnhancedKeyUsage");
        typeKuExt = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionKeyUsage");
        typeRequestCert = Type.GetTypeFromProgID("X509Enrollment.CX509CertificateRequestCertificate");
        typeX509Extensions = Type.GetTypeFromProgID("X509Enrollment.CX509Extensions");
        typeBasicConstraints = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionBasicConstraints");
        typeSignerCertificate = Type.GetTypeFromProgID("X509Enrollment.CSignerCertificate");
        typeX509Enrollment = Type.GetTypeFromProgID("X509Enrollment.CX509Enrollment");

        // for alternative names
        typeAltNamesCollection = Type.GetTypeFromProgID("X509Enrollment.CAlternativeNames");
        typeExtNames = Type.GetTypeFromProgID("X509Enrollment.CX509ExtensionAlternativeNames");
        typeCAlternativeName = Type.GetTypeFromProgID("X509Enrollment.CAlternativeName");
    }


    /// <summary>
    ///     Make certificate.
    /// </summary>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert = null)
    {
        return MakeCertificate(sSubjectCn, true, signingCert);
    }

    private X509Certificate2 MakeCertificate(string sSubjectCn,
        bool switchToMtaIfNeeded, X509Certificate2? signingCertificate = null,
        CancellationToken cancellationToken = default)
    {
        if (switchToMtaIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            return Task.Run(() => MakeCertificate(sSubjectCn, false, signingCertificate),
                cancellationToken).Result;

        // Subject
        var fullSubject = $"CN={sSubjectCn}";

        // Sig Algo
        const string hashAlgo = "SHA256";

        // Grace Days
        const int graceDays = -366;

        // KeyLength
        const int keyLength = 2048;

        var now = DateTime.UtcNow;
        var graceTime = now.AddDays(graceDays);
        var certificate = MakeCertificate(sSubjectCn, fullSubject, keyLength, hashAlgo, graceTime,
            now.AddDays(certificateValidDays), signingCertificate);
        return certificate;
    }

    private X509Certificate2 MakeCertificate(string subject, string fullSubject,
        int privateKeyLength, string hashAlg, DateTime validFrom, DateTime validTo,
        X509Certificate2? signingCertificate)
    {
        var x500CertDn = Activator.CreateInstance(typeX500Dn);
        var typeValue = new object[] { fullSubject, 0 };
        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500CertDn, typeValue);

        var x500RootCertDn = Activator.CreateInstance(typeX500Dn);

        if (signingCertificate != null) typeValue[0] = signingCertificate.Subject;

        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500RootCertDn, typeValue);

        object? sharedPrivateKey = null;
        if (signingCertificate != null) sharedPrivateKey = this.sharedPrivateKey;

        if (sharedPrivateKey == null)
        {
            sharedPrivateKey = Activator.CreateInstance(typeX509PrivateKey);
            typeValue = new object[] { sProviderName };
            typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue[0] = 2;
            typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue = new object[] { signingCertificate == null ? 2 : 1 };
            typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);

            if (signingCertificate != null)
            {
                typeValue = new object[] { 176 };
                typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                    typeValue);
            }

            typeValue[0] = privateKeyLength;
            typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);

            if (signingCertificate != null) this.sharedPrivateKey = sharedPrivateKey;
        }

        typeValue = new object[1];

        var oid = Activator.CreateInstance(typeOid);
        typeValue[0] = "1.3.6.1.5.5.7.3.1";
        typeOid.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, oid, typeValue);

        var oids = Activator.CreateInstance(typeOids);
        typeValue[0] = oid;
        typeOids.InvokeMember("Add", BindingFlags.InvokeMethod, null, oids, typeValue);

        var ekuExt = Activator.CreateInstance(typeEkuExt);
        typeValue[0] = oids;
        typeEkuExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, ekuExt, typeValue);

        var requestCert = Activator.CreateInstance(typeRequestCert);

        typeValue = new[] { 1, sharedPrivateKey, string.Empty };
        typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, requestCert,
            typeValue);
        typeValue = new[] { x500CertDn };
        typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = x500RootCertDn;
        typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validFrom;
        typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validTo;
        typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, requestCert, typeValue);

        var kuExt = Activator.CreateInstance(typeKuExt);

        typeValue[0] = 176;
        typeKuExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, kuExt, typeValue);

        var certificate =
            typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, requestCert, null);
        typeValue = new object[1];

        if (signingCertificate != null)
        {
            typeValue[0] = kuExt;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        typeValue[0] = ekuExt;
        typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);

        if (signingCertificate != null)
        {
            // add alternative names 
            // https://forums.iis.net/t/1180823.aspx

            var altNameCollection = Activator.CreateInstance(typeAltNamesCollection);
            var extNames = Activator.CreateInstance(typeExtNames);
            var altDnsNames = Activator.CreateInstance(typeCAlternativeName);

            IPAddress ip;
            if (IPAddress.TryParse(subject, out ip))
            {
                var ipBase64 = Convert.ToBase64String(ip.GetAddressBytes());
                typeValue = new object[]
                    { AlternativeNameType.XcnCertAltNameIpAddress, EncodingType.XcnCryptStringBase64, ipBase64 };
                typeCAlternativeName.InvokeMember("InitializeFromRawData", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }
            else
            {
                typeValue = new object[] { 3, subject }; //3==DNS, 8==IP ADDR
                typeCAlternativeName.InvokeMember("InitializeFromString", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }

            typeValue = new[] { altDnsNames };
            typeAltNamesCollection.InvokeMember("Add", BindingFlags.InvokeMethod, null, altNameCollection,
                typeValue);


            typeValue = new[] { altNameCollection };
            typeExtNames.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, extNames, typeValue);

            typeValue[0] = extNames;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        if (signingCertificate != null)
        {
            var signerCertificate = Activator.CreateInstance(typeSignerCertificate);

            typeValue = new object[] { 0, 0, 12, signingCertificate.Thumbprint };
            typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, signerCertificate,
                typeValue);
            typeValue = new[] { signerCertificate };
            typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, requestCert,
                typeValue);
        }
        else
        {
            var basicConstraints = Activator.CreateInstance(typeBasicConstraints);

            typeValue = new object[] { "true", "0" };
            typeBasicConstraints.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, basicConstraints,
                typeValue);
            typeValue = new[] { basicConstraints };
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        oid = Activator.CreateInstance(typeOid);

        typeValue = new object[] { 1, 0, 0, hashAlg };
        typeOid.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, oid, typeValue);

        typeValue = new[] { oid };
        typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, requestCert, null);

        var x509Enrollment = Activator.CreateInstance(typeX509Enrollment);

        typeValue[0] = requestCert;
        typeX509Enrollment.InvokeMember("InitializeFromRequest", BindingFlags.InvokeMethod, null, x509Enrollment,
            typeValue);

        if (signingCertificate == null)
        {
            typeValue[0] = fullSubject;
            typeX509Enrollment.InvokeMember("CertificateFriendlyName", BindingFlags.PutDispProperty, null,
                x509Enrollment, typeValue);
        }

        typeValue[0] = 0;

        var createCertRequest = typeX509Enrollment.InvokeMember("CreateRequest", BindingFlags.InvokeMethod, null,
            x509Enrollment, typeValue);
        typeValue = new[] { 2, createCertRequest, 0, string.Empty };

        typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, x509Enrollment,
            typeValue);
        typeValue = new object[] { null!, 0, 1 };

        var empty = (string)typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null,
            x509Enrollment, typeValue);

        return new X509Certificate2(Convert.FromBase64String(empty), string.Empty, X509KeyStorageFlags.Exportable);
    }
}

public enum EncodingType
{
    XcnCryptStringAny = 7,
    XcnCryptStringBase64 = 1,
    XcnCryptStringBase64Any = 6,
    XcnCryptStringBase64Header = 0,
    XcnCryptStringBase64Requestheader = 3,
    XcnCryptStringBase64Uri = 13,
    XcnCryptStringBase64X509Crlheader = 9,
    XcnCryptStringBinary = 2,
    XcnCryptStringChain = 0x100,
    XcnCryptStringEncodemask = 0xff,
    XcnCryptStringHashdata = 0x10000000,
    XcnCryptStringHex = 4,
    XcnCryptStringHexAny = 8,
    XcnCryptStringHexaddr = 10,
    XcnCryptStringHexascii = 5,
    XcnCryptStringHexasciiaddr = 11,
    XcnCryptStringHexraw = 12,
    XcnCryptStringNocr = -2147483648,
    XcnCryptStringNocrlf = 0x40000000,
    XcnCryptStringPercentescape = 0x8000000,
    XcnCryptStringStrict = 0x20000000,
    XcnCryptStringText = 0x200
}

public enum AlternativeNameType
{
    XcnCertAltNameDirectoryName = 5,
    XcnCertAltNameDnsName = 3,
    XcnCertAltNameGuid = 10,
    XcnCertAltNameIpAddress = 8,
    XcnCertAltNameOtherName = 1,
    XcnCertAltNameRegisteredId = 9,
    XcnCertAltNameRfc822Name = 2,
    XcnCertAltNameUnknown = 0,
    XcnCertAltNameUrl = 7,
    XcnCertAltNameUserPrincipleName = 11
}