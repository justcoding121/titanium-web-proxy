using System;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <inheritdoc />
/// <summary>
///     Certificate Maker - uses MakeCert
///     Calls COM objects using reflection
/// </summary>
[SupportedOSPlatform("windows")]
internal class WinCertificateMaker : ICertificateMaker
{
    private const string InitializeEncode = "InitializeEncode";
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
    private readonly int certificateGraceDays;

    private object? sharedPrivateKey;

    /// <summary>
    ///     Constructor.
    /// </summary>
    internal WinCertificateMaker(int certificateValidDays, int certificateGraceDays)
    {
        this.certificateValidDays = certificateValidDays;
        this.certificateGraceDays = certificateGraceDays;

        typeX500Dn = GetComType("X509Enrollment.CX500DistinguishedName");
        typeX509PrivateKey = GetComType("X509Enrollment.CX509PrivateKey");
        typeOid = GetComType("X509Enrollment.CObjectId");
        typeOids = GetComType("X509Enrollment.CObjectIds.1");
        typeEkuExt = GetComType("X509Enrollment.CX509ExtensionEnhancedKeyUsage");
        typeKuExt = GetComType("X509Enrollment.CX509ExtensionKeyUsage");
        typeRequestCert = GetComType("X509Enrollment.CX509CertificateRequestCertificate");
        typeX509Extensions = GetComType("X509Enrollment.CX509Extensions");
        typeBasicConstraints = GetComType("X509Enrollment.CX509ExtensionBasicConstraints");
        typeSignerCertificate = GetComType("X509Enrollment.CSignerCertificate");
        typeX509Enrollment = GetComType("X509Enrollment.CX509Enrollment");

        // for alternative names
        typeAltNamesCollection = GetComType("X509Enrollment.CAlternativeNames");
        typeExtNames = GetComType("X509Enrollment.CX509ExtensionAlternativeNames");
        typeCAlternativeName = GetComType("X509Enrollment.CAlternativeName");
    }

    private static Type GetComType(string progId)
    {
        return Type.GetTypeFromProgID(progId, true)
               ?? throw new PlatformNotSupportedException($"COM type '{progId}' is unavailable.");
    }

    private static object CreateComObject(Type type)
    {
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException($"Could not create COM type '{type.FullName}'.");
    }

    /// <summary>
    ///     Make certificate.
    /// </summary>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert)
    {
        return MakeCertificate(sSubjectCn, true, signingCert);
    }

    private X509Certificate2 MakeCertificate(string sSubjectCn,
        bool switchToMtaIfNeeded, X509Certificate2? signingCertificate = null,
        CancellationToken cancellationToken = default)
    {
        if (switchToMtaIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
        {
            var task = Task.Run(
                () => MakeCertificate(sSubjectCn, false, signingCertificate, cancellationToken),
                cancellationToken);
            task.Wait(cancellationToken);
            return task.Result;
        }

        // Subject
        var fullSubject = $"CN={sSubjectCn}";

        // Sig Algo
        const string hashAlgo = "SHA256";

        // KeyLength
        const int keyLength = 2048;

        var now = DateTime.UtcNow;
        var graceTime = now.AddDays(-certificateGraceDays);
        var certificate = MakeCertificate(sSubjectCn, fullSubject, keyLength, hashAlgo, graceTime,
            now.AddDays(certificateValidDays), signingCertificate);
        return certificate;
    }

    private X509Certificate2 MakeCertificate(string subject, string fullSubject, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        int privateKeyLength, string hashAlg, DateTime validFrom, DateTime validTo,
        X509Certificate2? signingCertificate)
    {
        var x500CertDn = CreateComObject(typeX500Dn);
        object?[] typeValue = { fullSubject, 0 };
        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500CertDn, typeValue);

        var x500RootCertDn = CreateComObject(typeX500Dn);

        if (signingCertificate != null) typeValue[0] = signingCertificate.Subject;

        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500RootCertDn, typeValue);

        object? sharedPrivateKey = null;
        if (signingCertificate != null) sharedPrivateKey = this.sharedPrivateKey;

        if (sharedPrivateKey == null)
        {
            sharedPrivateKey = CreateComObject(typeX509PrivateKey);
            typeValue = new object?[] { sProviderName };
            typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue[0] = 2;
            typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue = new object?[] { signingCertificate == null ? 2 : 1 };
            typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);

            if (signingCertificate != null)
            {
                typeValue = new object?[] { 176 };
                typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                    typeValue);
            }

            typeValue[0] = privateKeyLength;
            typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);

            if (signingCertificate != null) this.sharedPrivateKey = sharedPrivateKey;
        }

        typeValue = new object?[1];

        var oid = CreateComObject(typeOid);
        typeValue[0] = "1.3.6.1.5.5.7.3.1";
        typeOid.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, oid, typeValue);

        var oids = CreateComObject(typeOids);
        typeValue[0] = oid;
        typeOids.InvokeMember("Add", BindingFlags.InvokeMethod, null, oids, typeValue);

        var ekuExt = CreateComObject(typeEkuExt);
        typeValue[0] = oids;
        typeEkuExt.InvokeMember(InitializeEncode, BindingFlags.InvokeMethod, null, ekuExt, typeValue);

        var requestCert = CreateComObject(typeRequestCert);

        typeValue = new object?[] { 1, sharedPrivateKey, string.Empty };
        typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, requestCert,
            typeValue);
        typeValue = new object?[] { x500CertDn };
        typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = x500RootCertDn;
        typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validFrom;
        typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validTo;
        typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, requestCert, typeValue);

        var kuExt = CreateComObject(typeKuExt);

        typeValue[0] = 176;
        typeKuExt.InvokeMember(InitializeEncode, BindingFlags.InvokeMethod, null, kuExt, typeValue);

        var certificate =
            typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, requestCert, null)
            ?? throw new InvalidOperationException("The enrollment request did not return X509 extensions.");
        typeValue = new object?[1];

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

            var altNameCollection = CreateComObject(typeAltNamesCollection);
            var extNames = CreateComObject(typeExtNames);
            var altDnsNames = CreateComObject(typeCAlternativeName);

            if (IPAddress.TryParse(subject, out var ip))
            {
                var ipBase64 = Convert.ToBase64String(ip.GetAddressBytes());
                typeValue = new object?[]
                    { AlternativeNameType.XcnCertAltNameIpAddress, EncodingType.XcnCryptStringBase64, ipBase64 };
                typeCAlternativeName.InvokeMember("InitializeFromRawData", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }
            else
            {
                typeValue = new object?[] { 3, subject }; //3==DNS, 8==IP ADDR
                typeCAlternativeName.InvokeMember("InitializeFromString", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }

            typeValue = new object?[] { altDnsNames };
            typeAltNamesCollection.InvokeMember("Add", BindingFlags.InvokeMethod, null, altNameCollection,
                typeValue);


            typeValue = new object?[] { altNameCollection };
            typeExtNames.InvokeMember(InitializeEncode, BindingFlags.InvokeMethod, null, extNames, typeValue);

            typeValue[0] = extNames;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        if (signingCertificate != null)
        {
            var signerCertificate = CreateComObject(typeSignerCertificate);

            typeValue = new object?[] { 0, 0, 12, signingCertificate.Thumbprint };
            typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, signerCertificate,
                typeValue);
            typeValue = new object?[] { signerCertificate };
            typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, requestCert,
                typeValue);
        }
        else
        {
            var basicConstraints = CreateComObject(typeBasicConstraints);

            typeValue = new object?[] { "true", "0" };
            typeBasicConstraints.InvokeMember(InitializeEncode, BindingFlags.InvokeMethod, null, basicConstraints,
                typeValue);
            typeValue = new object?[] { basicConstraints };
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        oid = CreateComObject(typeOid);

        typeValue = new object?[] { 1, 0, 0, hashAlg };
        typeOid.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, oid, typeValue);

        typeValue = new object?[] { oid };
        typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, requestCert, null);

        var x509Enrollment = CreateComObject(typeX509Enrollment);

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
            x509Enrollment, typeValue)
            ?? throw new InvalidOperationException("The enrollment request could not be created.");
        typeValue = new object?[] { 2, createCertRequest, 0, string.Empty };

        typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, x509Enrollment,
            typeValue);
        typeValue = new object?[] { null, 0, 1 };

        var empty = typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null,
                        x509Enrollment, typeValue) as string
                    ?? throw new InvalidOperationException("The enrollment API did not return a PFX.");

        return CertificateLoader.LoadPkcs12(Convert.FromBase64String(empty), string.Empty,
            X509KeyStorageFlags.Exportable);
    }
}

internal enum EncodingType
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

internal enum AlternativeNameType
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