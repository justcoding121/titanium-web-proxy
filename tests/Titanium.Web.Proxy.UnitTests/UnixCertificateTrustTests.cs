using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UnixCertificateTrustTests
{
    [TestMethod]
    public void DetectLinuxNssPackage_PrefersAptThenDnfThenZypper()
    {
        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        var hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.IsNotNull(hint);
        Assert.AreEqual("libnss3-tools", hint!.Package);
        Assert.AreEqual("apt-get", hint.FileName);

        runner = new FakeProcessRunner();
        runner.When("sh", "command -v dnf", "/usr/bin/dnf");
        hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.AreEqual("nss-tools", hint!.Package);

        runner = new FakeProcessRunner();
        runner.When("sh", "command -v zypper", "/usr/bin/zypper");
        hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.AreEqual("mozilla-nss-tools", hint!.Package);
    }

    [TestMethod]
    public void DetectLinuxNssPackage_WhenNoManager_ReturnsNull()
    {
        var runner = new FakeProcessRunner { DefaultSuccess = false };
        Assert.IsNull(UnixCertificateTrust.DetectLinuxNssPackage(runner));
    }

    [TestMethod]
    public void TryInstallNssCertutil_Linux_UsesElevationForAptPackage()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        // certutil remains missing (no When for command -v certutil)
        var elevation = new FakeElevationPrompt();
        var result = UnixCertificateTrust.TryInstallNssCertutil(runner, elevation);
        Assert.AreEqual(1, elevation.Calls.Count);
        Assert.IsTrue(elevation.Calls[0].Arguments.Contains("libnss3-tools", StringComparison.Ordinal));
        Assert.AreEqual(CertificateOsTrustKind.Failed, result.Kind);
    }

    [TestMethod]
    public void TryInstallNssCertutil_Linux_CancelElevation_ReturnsCancelled()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        var elevation = new FakeElevationPrompt { Cancel = true };
        var result = UnixCertificateTrust.TryInstallNssCertutil(runner, elevation);
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, result.Kind);
    }

    [TestMethod]
    public void CertificateOsTrustResult_OkAndFail_Helpers()
    {
        Assert.IsTrue(CertificateOsTrustResult.Ok().Succeeded);
        var fail = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.CertutilMissing, "missing", "libnss3-tools");
        Assert.IsFalse(fail.Succeeded);
        Assert.AreEqual("libnss3-tools", fail.PackageHint);
    }

    [TestMethod]
    public void IsCertificateInLoginKeychain_NonMac_ReturnsFalse()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS uses real keychain path");
            return;
        }

        using var cert = CreateEphemeralRoot();
        Assert.IsFalse(UnixCertificateTrust.IsCertificateInLoginKeychain(cert, new FakeProcessRunner()));
    }

    [TestMethod]
    public void IsCertificateInLoginKeychain_WhenSecurityListsHash_ReturnsTrue()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var runner = new FakeProcessRunner();
        runner.When("security", "find-certificate", $"SHA-1 hash: {sha1}\n");
        Assert.IsTrue(UnixCertificateTrust.IsCertificateInLoginKeychain(cert, runner));
    }

    [TestMethod]
    public void IsCertificateInLoginKeychain_WhenSecurityMisses_ReturnsFalse()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var cert = CreateEphemeralRoot();
        var runner = new FakeProcessRunner { DefaultSuccess = false };
        Assert.IsFalse(UnixCertificateTrust.IsCertificateInLoginKeychain(cert, runner));
    }

    [TestMethod]
    public void HasExplicitMacSslTrustSettings_NoTrustSettings_ReturnsFalse()
    {
        using var cert = CreateEphemeralRoot();
        var runner = new FakeProcessRunner { DefaultSuccess = false };
        runner.When("security", "dump-trust-settings",
            "SecTrustSettingsCopyCertificates: No Trust Settings were found.\n");
        Assert.IsFalse(UnixCertificateTrust.HasExplicitMacSslTrustSettings(runner, cert));
    }

    [TestMethod]
    public void HasExplicitMacSslTrustSettings_TrustListStubWithoutPolicies_ReturnsFalse()
    {
        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var runner = new FakeProcessRunner { DefaultSuccess = false };
        runner.When("security", "dump-trust-settings",
            "SecTrustSettingsCopyCertificates: No Trust Settings were found.\n");
        // add-trusted-cert stub: SHA-1 in trustList, no trustSettings policies.
        runner.When("security", "trust-settings-export", "ok\n");
        runner.WriteFileOnMatch = "trust-settings-export";
        runner.When("plutil", "-p",
            "{\n  \"trustList\" => {\n    \"" + sha1 + "\" => {\n" +
            "      \"issuerName\" => {length = 48, bytes = 0xab}\n" +
            "      \"modDate\" => 2026-09-04 22:15:04 +0000\n" +
            "      \"serialNumber\" => {length = 8, bytes = 0xcd}\n" +
            "    }\n  }\n  \"trustVersion\" => 1\n}\n");
        Assert.IsFalse(UnixCertificateTrust.HasExplicitMacSslTrustSettings(runner, cert));
    }

    [TestMethod]
    public void HasExplicitMacSslTrustSettings_TrustRootPolicy_ReturnsTrue()
    {
        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var dump = $"""
            Number of trusted certs = 1
            Cert 0: Titanium Inspector Root Certificate
               SHA-1 hash: {sha1}
               Number of trust settings : 1
               Trust Setting 0:
                  Policy OID            : 1.2.840.113635.100.1.3
                  Result Type           : kSecTrustSettingsResultTrustRoot
            """;
        var runner = new FakeProcessRunner();
        runner.When("security", "dump-trust-settings -d", dump);
        Assert.IsTrue(UnixCertificateTrust.HasExplicitMacSslTrustSettings(runner, cert));
    }

    [TestMethod]
    public void UntrustUserSsl_OnMac_DeletesByHashWithTrustFlag_AndElevatesSystemCleanup()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var runner = new FakeProcessRunner();
        runner.When("security", "find-certificate",
            $"SHA-1 hash: {sha1}\nkeychain: \"/Library/Keychains/System.keychain\"\n");
        runner.When("security", "delete-certificate", "ok\n");
        runner.When("security", "remove-trusted-cert", "ok\n");
        var elevation = new FakeElevationPrompt();

        Assert.IsTrue(UnixCertificateTrust.UntrustUserSsl(
            cert, "Titanium Root Certificate Authority", runner, elevation));

        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("delete-certificate -Z " + sha1, StringComparison.Ordinal) &&
            c.Contains("-t", StringComparison.Ordinal)));
        Assert.IsTrue(elevation.Calls.Count >= 1);
        Assert.IsTrue(elevation.Calls.Exists(c =>
            c.Arguments.Contains("System.keychain", StringComparison.Ordinal) ||
            c.Arguments.Contains("remove-trusted-cert", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UntrustUserSsl_OnLinux_DeletesAliasNicknameMatchingCertificate()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        using var cert = CreateEphemeralRoot();
        var pem = ToPem(cert);
        var runner = new FakeProcessRunner { DefaultSuccess = true };
        runner.When("sh", "command -v certutil", "/usr/bin/certutil\n");
        runner.When("certutil", "-L -n", pem);
        runner.When("certutil", "-L", """
            Certificate Nickname                                         Trust Attributes
                                                                         SSL,S/MIME,JAR/XPI

            Titanium Inspector Root Certificate                          C,,
            """);

        Assert.IsTrue(UnixCertificateTrust.UntrustUserSsl(
            cert, "Titanium Root Certificate Authority", runner));
        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("-D", StringComparison.Ordinal) &&
            c.Contains("Titanium Inspector Root Certificate", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("E2E-UI-Linux")]
    public void TrustAndUntrustUserSsl_LinuxNss_RemovesAliasNickname()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        var runner = new ProcessRunner();
        if (UnixCertificateTrust.FindCertutil(runner) is null)
        {
            Assert.Inconclusive("certutil (libnss3-tools) required");
            return;
        }

        using var cert = CreateEphemeralRoot();
        var alias = "TWP-NssAlias-" + Guid.NewGuid().ToString("N")[..8];
        var cerPath = UnixCertificateTrust.WriteTempCer(cert);
        try
        {
            var nssDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
            Directory.CreateDirectory(nssDir);
            var add = runner.Run("certutil",
                $"-d sql:{nssDir} -A -t \"C,,\" -n \"{alias}\" -i \"{cerPath}\"");
            Assert.IsNotNull(add);
            Assert.IsTrue(add!.Succeeded, add.StandardError);
            Assert.IsTrue(UnixCertificateTrust.VerifyUserSslTrust(cert, runner),
                "Chrome NSS db should contain the imported test CA");

            Assert.IsTrue(UnixCertificateTrust.UntrustUserSsl(
                cert, "Titanium Root Certificate Authority", runner),
                "Remove CA must delete the leftover alias nickname, not only the official CN");
            Assert.IsFalse(UnixCertificateTrust.VerifyUserSslTrust(cert, runner),
                "NSS still lists the test CA after Untrust");
        }
        finally
        {
            try { File.Delete(cerPath); } catch { /* best-effort */ }
            UnixCertificateTrust.UntrustUserSsl(cert, alias, runner);
        }
    }

    [TestMethod]
    public void VerifyUserSslTrust_OnMac_UsesTrustSettingsNotVerifyCertAlone()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var cert = CreateEphemeralRoot();
        // Fake: verify-cert would succeed, but no trust settings → must be false.
        var runner = new FakeProcessRunner();
        runner.When("security", "verify-cert", "...certificate verification successful.\n");
        runner.When("security", "dump-trust-settings",
            "SecTrustSettingsCopyCertificates: No Trust Settings were found.\n");
        Assert.IsFalse(UnixCertificateTrust.VerifyUserSslTrust(cert, runner));
    }

    private static string ToPem(System.Security.Cryptography.X509Certificates.X509Certificate2 cert) =>
        "-----BEGIN CERTIFICATE-----\n" +
        Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks) +
        "\n-----END CERTIFICATE-----\n";

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateEphemeralRoot()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=TWP-NssRoundTrip-" + Guid.NewGuid().ToString("N")[..8],
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}

[TestClass]
public class FirefoxCertificateTrustTests
{
    [TestMethod]
    public void ParseDefaultProfilePath_PrefersDefaultFlag()
    {
        var ini = """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd.default-release
            Default=1

            [Profile1]
            Name=old
            IsRelative=1
            Path=Profiles/old.default
            """;
        var path = FirefoxCertificateTrust.ParseDefaultProfilePath(ini);
        Assert.AreEqual("Profiles/abcd.default-release", path);
    }

    [TestMethod]
    public void ParseDefaultProfilePath_FallsBackToFirstPath()
    {
        var ini = """
            [Profile0]
            Name=only
            IsRelative=1
            Path=Profiles/only.default
            """;
        var path = FirefoxCertificateTrust.ParseDefaultProfilePath(ini);
        Assert.AreEqual("Profiles/only.default", path);
    }

    [TestMethod]
    public void TryEnableWindowsEnterpriseRoots_OnWindows_WritesOrSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            var nonWindows = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
            // Linux/macOS may succeed via user-writable policies.json or profile user.js.
            if (nonWindows.Succeeded)
                FirefoxCertificateTrust.TryClearWindowsEnterpriseRoots();
            else
            {
                Assert.IsTrue(
                    nonWindows.Kind is CertificateOsTrustKind.Unsupported or CertificateOsTrustKind.Failed,
                    nonWindows.Kind + ": " + nonWindows.Message);
            }

            return;
        }

        var result = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
        if (!result.Succeeded &&
            result.Message.Contains("Firefox profile", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("No Firefox profile on this machine");
            return;
        }

        Assert.IsTrue(result.Succeeded, result.Message);
        FirefoxCertificateTrust.TryClearWindowsEnterpriseRoots();
    }

    [TestMethod]
    public void BuildOrMergeFirefoxPoliciesJson_PreservesExistingPolicies()
    {
        const string existing =
            """
            {
              "policies": {
                "BlockAboutConfig": true,
                "Certificates": {
                  "ImportEnterpriseRoots": false
                }
              }
            }
            """;
        var merged = FirefoxCertificateTrust.BuildOrMergeFirefoxPoliciesJson(existing, importEnterpriseRoots: true);
        Assert.IsTrue(FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson(merged, out var err), err);
        using var doc = System.Text.Json.JsonDocument.Parse(merged);
        Assert.IsTrue(doc.RootElement.GetProperty("policies").GetProperty("BlockAboutConfig").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("policies").GetProperty("Certificates")
            .GetProperty("ImportEnterpriseRoots").GetBoolean());
    }

    [TestMethod]
    public void BuildOrMergeFirefoxPoliciesJson_RecoversFromCorruptExisting()
    {
        var merged = FirefoxCertificateTrust.BuildOrMergeFirefoxPoliciesJson("{not-json", importEnterpriseRoots: true);
        Assert.IsTrue(FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson(merged, out var err), err);
    }

    [TestMethod]
    public void EnsureEnterpriseRootsUserPref_DoesNotRewriteCommentsOrLockPref()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-userjs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var userJs = Path.Combine(dir, "user.js");
            File.WriteAllText(userJs,
                "// security.enterprise_roots.enabled\n" +
                "lockPref(\"security.enterprise_roots.enabled\", false);\n" +
                "user_pref(\"security.enterprise_roots.enabled\", false);\n");

            FirefoxCertificateTrust.EnsureEnterpriseRootsUserPref(dir);
            Assert.IsTrue(FirefoxCertificateTrust.VerifyEnterpriseRootsUserPref(dir));
            var text = File.ReadAllText(userJs);
            StringAssert.Contains(text, "// security.enterprise_roots.enabled");
            StringAssert.Contains(text, "lockPref(\"security.enterprise_roots.enabled\", false);");
            StringAssert.Contains(text, "user_pref(\"security.enterprise_roots.enabled\", true);");
            Assert.IsFalse(text.Contains("user_pref(\"security.enterprise_roots.enabled\", false);",
                StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ParseDefaultProfileEntry_HonorsIsRelativeZero()
    {
        var ini = """
            [Profile0]
            Name=abs
            IsRelative=0
            Path=/opt/firefox-profile
            Default=1
            """;
        var entry = FirefoxCertificateTrust.ParseDefaultProfileEntry(ini);
        Assert.IsNotNull(entry);
        Assert.AreEqual("/opt/firefox-profile", entry!.Value.Path);
        Assert.IsFalse(entry.Value.IsRelative);
    }

    [TestMethod]
    public void GetFirefoxRoots_IncludesSnapAndFlatpakOnLinuxLayout()
    {
        var roots = FirefoxCertificateTrust.GetFirefoxRoots();
        Assert.IsTrue(roots.Length >= 1);
        if (!OperatingSystem.IsLinux())
            return;

        Assert.IsTrue(roots.Any(r => r.Contains(".mozilla", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(roots.Any(r => r.Contains("snap", StringComparison.OrdinalIgnoreCase) &&
                                     r.Contains("firefox", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(roots.Any(r => r.Contains(".var", StringComparison.Ordinal) &&
                                     r.Contains("org.mozilla.firefox", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GetFirefoxRoots_IncludesDeveloperEditionAndNightlyOnMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        var roots = FirefoxCertificateTrust.GetFirefoxRoots();
        Assert.IsTrue(roots.Any(r => r.EndsWith("Firefox", StringComparison.Ordinal)));
        Assert.IsTrue(roots.Any(r => r.Contains("FirefoxDeveloperEdition", StringComparison.Ordinal)));
        Assert.IsTrue(roots.Any(r => r.Contains("Firefox Nightly", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GetFirefoxPoliciesJsonPaths_OnMac_DoesNotIncludeAppBundle()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        var paths = FirefoxCertificateTrust.GetFirefoxPoliciesJsonPaths().ToList();
        Assert.IsFalse(paths.Any(p => p.Contains("Firefox.app", StringComparison.Ordinal)),
            "Writing policies.json into Firefox.app breaks code signing");
        Assert.IsTrue(paths.Any(p => p.Contains("Application Support", StringComparison.Ordinal) &&
                                     p.EndsWith("policies.json", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EnsureEnterpriseRootsPrefFile_WritesAndClearsUserJs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FirefoxCertificateTrust.EnsureEnterpriseRootsUserPref(dir);
            Assert.IsTrue(FirefoxCertificateTrust.VerifyEnterpriseRootsUserPref(dir));
            FirefoxCertificateTrust.EnsureEnterpriseRootsPrefFile(Path.Combine(dir, "prefs.js"));
            var prefs = File.ReadAllText(Path.Combine(dir, "prefs.js"));
            StringAssert.Contains(prefs, "security.enterprise_roots.enabled");
            StringAssert.Contains(prefs, "true");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void TryRequestFirefoxQuit_WhenNotRunning_ReturnsTrue()
    {
        if (FirefoxCertificateTrust.IsFirefoxProcessRunning())
        {
            Assert.Inconclusive("Firefox is running on this machine");
            return;
        }

        Assert.IsTrue(FirefoxCertificateTrust.TryRequestFirefoxQuit(TimeSpan.FromMilliseconds(100)));
    }
}
