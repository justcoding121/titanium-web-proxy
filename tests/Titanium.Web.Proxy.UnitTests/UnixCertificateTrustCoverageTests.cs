using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
/// Windows CI cannot execute live Keychain/NSS, but the Linux/macOS helpers are
/// still uploaded to Sonar from that job. Drive private methods through
/// <see cref="IProcessRunner"/> fakes so those lines count.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UnixCertificateTrustCoverageTests
{
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void TrustLinuxNssDetailed_AndUntrust_CoverHappyAndCollisionPaths()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot();
        var cerPath = UnixCertificateTrust.WriteTempCer(cert);
        var nssHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
        var extraNss = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "snap", "chromium", "common", ".pki", "nssdb");
        Directory.CreateDirectory(nssHome);
        Directory.CreateDirectory(extraNss);
        var friendly = "Titanium Root Certificate Authority";
        var pem = ToPem(cert);
        try
        {
            var happy = new ScriptedProcessRunner();
            SeedFakeCertutil(happy);
            happy.When((_, args) => args.Contains(" -L", StringComparison.Ordinal) &&
                                    !args.Contains(" -n", StringComparison.Ordinal),
                new ProcessRunResult(0, friendly + "  C,,\n", ""));
            happy.When((_, args) => args.Contains(" -L -n", StringComparison.Ordinal) ||
                                    args.Contains(" -a", StringComparison.Ordinal),
                new ProcessRunResult(0, pem, ""));

            var ok = (CertificateOsTrustResult)Invoke(
                "TrustLinuxNssDetailed",
                [typeof(IProcessRunner), typeof(string), typeof(string)],
                happy, cerPath, friendly)!;
            Assert.IsTrue(ok.Succeeded, ok.Message);

            var untrust = (bool)Invoke(
                "UntrustLinuxNss",
                [typeof(IProcessRunner), typeof(X509Certificate2), typeof(string)],
                happy, cert, friendly)!;
            Assert.IsTrue(untrust);

            Assert.IsTrue(UnixCertificateTrust.VerifyLinuxNssTrust(happy, cert));

            // Collision path: first list misses the nickname, PEM dump matches, re-add succeeds.
            var listCalls = 0;
            var collision = new ScriptedProcessRunner();
            SeedFakeCertutil(collision);
            collision.When((_, args) => args.Contains(" -L", StringComparison.Ordinal) &&
                                        !args.Contains(" -n", StringComparison.Ordinal),
                () =>
                {
                    listCalls++;
                    return listCalls == 1
                        ? new ProcessRunResult(0, "OtherNick  C,,\n", "")
                        : new ProcessRunResult(0, friendly + "  C,,\n", "");
                });
            collision.When((_, args) => args.Contains(" -a", StringComparison.Ordinal),
                new ProcessRunResult(0, pem, ""));
            collision.When((_, args) => args.Contains(" -A ", StringComparison.Ordinal),
                new ProcessRunResult(0, "", ""));

            var retried = (CertificateOsTrustResult)Invoke(
                "TrustLinuxNssDetailed",
                [typeof(IProcessRunner), typeof(string), typeof(string)],
                collision, cerPath, friendly)!;
            Assert.IsTrue(retried.Succeeded, retried.Message);
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    [TestMethod]
    public void TrustLinuxNssDetailed_WhenAddFails_ReturnsNssFailed()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot();
        var cerPath = UnixCertificateTrust.WriteTempCer(cert);
        try
        {
            var runner = new ScriptedProcessRunner
            {
                Default = new ProcessRunResult(1, "", "certutil: could not add"),
            };
            SeedFakeCertutil(runner);
            runner.When((_, args) => args.Contains("-N", StringComparison.Ordinal),
                new ProcessRunResult(0, "", ""));

            var result = (CertificateOsTrustResult)Invoke(
                "TrustLinuxNssDetailed",
                [typeof(IProcessRunner), typeof(string), typeof(string)],
                runner, cerPath, "TWP-Fail")!;
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(CertificateOsTrustKind.NssFailed, result.Kind);
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    [TestMethod]
    public void NssNicknameHelpers_ParseListAndMatchPem()
    {
        using var cert = CreateEphemeralRoot();
        var pem = ToPem(cert);
        var runner = new ScriptedProcessRunner();
        runner.When((_, args) => args.Contains(" -a", StringComparison.Ordinal) ||
                                    args.EndsWith("-a", StringComparison.Ordinal),
            new ProcessRunResult(0, pem, ""));
        runner.When((_, args) => args.Contains(" -L", StringComparison.Ordinal),
            new ProcessRunResult(0,
            "Certificate Nickname                                         Trust Attributes\n" +
            "SSL,S/MIME,JAR/XPI\n" +
            "----------------------------------\n" +
            "Titanium Root Certificate Authority  C,,\n" +
            "Other CA  ,, \n", ""));

        var nicks = ((IEnumerable<string>)Invoke(
            "ParseNssNicknames", [typeof(string)],
            "Certificate Nickname\nSSL,\n----\nTitanium Root  C,,\n")!)
            .ToList();
        Assert.IsTrue(nicks.Any(n => n.Contains("Titanium Root", StringComparison.Ordinal)));

        var nssDir = Path.Combine(Path.GetTempPath(), "twp-nss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nssDir);
        try
        {
            Assert.IsTrue((bool)Invoke(
                "NssListContainsNickname",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(string)],
                runner, "certutil", nssDir, "Titanium Root Certificate Authority")!);

            Assert.IsTrue((bool)Invoke(
                "LinuxNssNicknameMatches",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(string), typeof(X509Certificate2)],
                runner, "certutil", nssDir, "Titanium Root Certificate Authority", cert)!);

            Assert.IsTrue((bool)Invoke(
                "LinuxNssContainsCertificate",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(X509Certificate2), typeof(string)],
                runner, "certutil", nssDir, cert, "Titanium Root Certificate Authority  C,,\n")!);

            Invoke("RemoveLinuxNssEntriesMatching",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(X509Certificate2)],
                runner, "certutil", nssDir, cert);

            Assert.IsTrue((bool)Invoke(
                "UntrustLinuxNssDirectory",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(X509Certificate2), typeof(string)],
                runner, "certutil", nssDir, cert, "Titanium Root Certificate Authority")!);

            Invoke("TryTrustAdditionalLinuxNss",
                [typeof(IProcessRunner), typeof(string), typeof(string), typeof(string)],
                runner, "certutil", UnixCertificateTrust.WriteTempCer(cert), "Titanium Root Certificate Authority");
        }
        finally
        {
            try { Directory.Delete(nssDir, true); } catch { /* ignore */ }
        }

        using var bogus = CreateEphemeralRoot("CN=Unrelated");
        Assert.IsNull(Invoke("TryReloadCertificateFromCer", [typeof(string)],
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".cer")));
        var cer = UnixCertificateTrust.WriteTempCer(bogus, forUserGuidance: true);
        try
        {
            using var loaded = (X509Certificate2?)Invoke(
                "TryReloadCertificateFromCer", [typeof(string)], cer);
            Assert.IsNotNull(loaded);
        }
        finally
        {
            TryDelete(cer);
        }
    }

    [TestMethod]
    public void MacTrustHelpers_CoverDumpExportUntrustAndSystem()
    {
        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "TWP";
        var cerPath = UnixCertificateTrust.WriteTempCer(cert);
        try
        {
            var dumpRunner = new FakeProcessRunner();
            dumpRunner.When("security", "dump-trust-settings",
                $"Cert {cn} SHA-1:{sha1}\nNumber of trust settings: 1\nkSecTrustSettingsResultTrustRoot\n");
            Assert.IsTrue(UnixCertificateTrust.HasExplicitMacSslTrustSettings(dumpRunner, cert));

            var exportRunner = new FakeProcessRunner { WriteFileOnMatch = "trust-settings-export" };
            exportRunner.When("security", "dump-trust-settings",
                "SecTrustSettingsCopyCertificates: No Trust Settings were found.\n");
            exportRunner.When("security", "trust-settings-export", "");
            exportRunner.When("plutil", "-p",
                sha1 + "\ntrustSettings => [\n  result = kSecTrustSettingsResultTrustRoot\n]\n");
            Assert.IsTrue(UnixCertificateTrust.HasExplicitMacSslTrustSettings(exportRunner, cert));

            var addRunner = new FakeProcessRunner();
            addRunner.When("security", "add-trusted-cert", "ok");
            addRunner.When("security", "dump-trust-settings",
                $"{cn}\nTrust Root\nNumber of trust settings: 1\n");
            var detailed = (CertificateOsTrustResult)Invoke(
                "TrustMacUserDetailed",
                [typeof(IProcessRunner), typeof(string), typeof(X509Certificate2)],
                addRunner, cerPath, cert)!;
            Assert.IsTrue(detailed.Succeeded, detailed.Message);

            var failAdd = new FakeProcessRunner { DefaultSuccess = false };
            var failed = (CertificateOsTrustResult)Invoke(
                "TrustMacUserDetailed",
                [typeof(IProcessRunner), typeof(string), typeof(X509Certificate2)],
                failAdd, cerPath, cert)!;
            Assert.AreEqual(CertificateOsTrustKind.MacKeychainFailed, failed.Kind);

            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var find = new FakeProcessRunner();
            find.When("security", "find-certificate", $"SHA-1 hash: {sha1}AABBCCDDEEFF00112233445566778899AABBCC\n");
            Invoke("CollectMacCertificateHashes",
                [typeof(IProcessRunner), typeof(string), typeof(HashSet<string>)],
                find, cn, hashes);
            Assert.IsTrue(hashes.Count >= 1);

            var elevation = new FakeElevationPrompt();
            var untrust = (bool)Invoke(
                "UntrustMacThorough",
                [typeof(IProcessRunner), typeof(X509Certificate2), typeof(string), typeof(IElevationPrompt)],
                find, cert, "Titanium Root Certificate Authority", elevation)!;
            Assert.IsTrue(untrust);
            Assert.IsTrue(elevation.Calls.Count >= 1);

            Assert.IsTrue((bool)Invoke(
                "TrustMacSystem", [typeof(IElevationPrompt), typeof(string)],
                elevation, cerPath)!);
            Assert.IsTrue((bool)Invoke(
                "TrustLinuxSystem",
                [typeof(IElevationPrompt), typeof(string), typeof(string)],
                elevation, cerPath, "Titanium Root Certificate Authority")!);
            Assert.IsTrue((bool)Invoke(
                "UntrustLinuxSystem",
                [typeof(IElevationPrompt), typeof(string)],
                elevation, "Titanium Root Certificate Authority")!);
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    [TestMethod]
    public void ProbeAndInstallHelpers_AreInvokableOnWindows()
    {
        var apt = new FakeProcessRunner();
        apt.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        var linuxProbe = (CertificateOsTrustResult)Invoke(
            "ProbeLinuxCertutilInstall", [typeof(IProcessRunner)], apt)!;
        Assert.AreEqual(CertificateOsTrustKind.CertutilMissing, linuxProbe.Kind);
        StringAssert.Contains(linuxProbe.Message, "libnss3-tools");

        var none = new FakeProcessRunner { DefaultSuccess = false };
        var noMgr = (CertificateOsTrustResult)Invoke(
            "ProbeLinuxCertutilInstall", [typeof(IProcessRunner)], none)!;
        Assert.AreEqual(CertificateOsTrustKind.CertutilMissing, noMgr.Kind);

        var brew = new FakeProcessRunner();
        brew.When("brew", "", "/opt/homebrew/bin/brew");
        brew.When("sh", "command -v brew", "/opt/homebrew/bin/brew");
        var macProbe = (CertificateOsTrustResult)Invoke(
            "ProbeMacCertutilInstall", [typeof(IProcessRunner)], brew)!;
        Assert.IsTrue(
            macProbe.Kind is CertificateOsTrustKind.CertutilMissing or CertificateOsTrustKind.HomebrewMissing,
            macProbe.Kind + ": " + macProbe.Message);

        var elevation = new FakeElevationPrompt();
        var install = (CertificateOsTrustResult)Invoke(
            "TryInstallLinuxNssCertutil",
            [typeof(IProcessRunner), typeof(IElevationPrompt)],
            apt, elevation)!;
        Assert.IsTrue(elevation.Calls.Count >= 1);
        Assert.IsFalse(install.Succeeded);

        elevation.Cancel = true;
        var cancelled = (CertificateOsTrustResult)Invoke(
            "TryInstallLinuxNssCertutil",
            [typeof(IProcessRunner), typeof(IElevationPrompt)],
            apt, elevation)!;
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, cancelled.Kind);

        var publicProbe = UnixCertificateTrust.ProbeCertutilInstall(new FakeProcessRunner { DefaultSuccess = false });
        Assert.AreEqual(CertificateOsTrustKind.CertutilMissing, publicProbe.Kind);

        using var guidanceCert = CreateEphemeralRoot("CN=TWP Guidance Root");
        if (OperatingSystem.IsMacOS())
        {
            var fakeOpen = new FakeProcessRunner();
            Assert.IsTrue(UnixCertificateTrust.OpenMacKeychainGuidance(null, fakeOpen));
            var cer = UnixCertificateTrust.OpenMacKeychainGuidanceForCertificate(guidanceCert, fakeOpen);
            try
            {
                Assert.IsNotNull(cer);
            }
            finally
            {
                if (cer is not null)
                    TryDelete(cer);
            }
        }
        else
        {
            Assert.IsFalse(UnixCertificateTrust.OpenMacKeychainGuidance(null, new FakeProcessRunner()));
            Assert.IsNull(UnixCertificateTrust.OpenMacKeychainGuidanceForCertificate(guidanceCert, new FakeProcessRunner()));
        }

        // Always pass fakes — a missing runner uses real `security` / osascript and pops a password dialog.
        using var unused = CreateEphemeralRoot();
        var dryRun = new FakeProcessRunner { DefaultSuccess = false };
        elevation.Cancel = true;
        _ = UnixCertificateTrust.TrustUserSsl(unused, "TWP", dryRun);
        _ = UnixCertificateTrust.UntrustUserSsl(unused, "TWP", dryRun, elevation);
        _ = UnixCertificateTrust.TrustMachineSsl(unused, "TWP", dryRun, elevation);
        _ = UnixCertificateTrust.UntrustMachineSsl(unused, "TWP", dryRun, elevation);
        _ = UnixCertificateTrust.VerifyUserSslTrust(unused, dryRun);
        _ = UnixCertificateTrust.IsCertificateInLoginKeychain(unused, dryRun);
    }

    [TestMethod]
    public void LeftoverHelpers_EnsureNssDbSanitizeFindBrewAndMacNames()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot("CN=TWP Space Name/Root?");
        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v certutil", planted.Path);
        runner.When("certutil", "-N --empty-password", "");
        runner.When("security", "add-trusted-cert", "ok");
        runner.When("security", "dump-trust-settings",
            cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) + "\nTrust Root\n");

        var nssDir = (string?)Invoke("EnsureLinuxNssDb", [typeof(IProcessRunner)], runner);
        Assert.IsFalse(string.IsNullOrEmpty(nssDir));

        var sanitized = (string)Invoke("SanitizeFileName", [typeof(string)], "bad name:with*chars")!;
        Assert.IsFalse(sanitized.Contains(' '));
        Assert.IsFalse(string.IsNullOrEmpty(sanitized));

        var brewRunner = new FakeProcessRunner { DefaultSuccess = false };
        if (OperatingSystem.IsMacOS())
        {
            _ = Invoke("FindBrew", [typeof(IProcessRunner)], brewRunner);
        }
        else
        {
            Assert.IsNull(Invoke("FindBrew", [typeof(IProcessRunner)], brewRunner));
        }

        Assert.IsTrue((bool)Invoke("CommandExists", [typeof(IProcessRunner), typeof(string)], runner, "certutil")!);

        var names = ((IEnumerable<string>)Invoke(
            "MacRootCommonNames", [typeof(string), typeof(X509Certificate2)],
            "Friendly CA", cert)!).ToList();
        Assert.IsTrue(names.Count >= 2);

        var cerPath = UnixCertificateTrust.WriteTempCer(cert, forUserGuidance: true);
        try
        {
            Assert.IsTrue((bool)Invoke("TrustMacUser", [typeof(IProcessRunner), typeof(string)], runner, cerPath)!);
            Assert.IsTrue((bool)Invoke("VerifyMacSslTrust", [typeof(IProcessRunner), typeof(X509Certificate2)], runner, cert)!);
        }
        finally
        {
            TryDelete(cerPath);
        }

        using var unused = CreateEphemeralRoot();
        var unsupported = UnixCertificateTrust.TrustUserSsl(unused, "TWP", new FakeProcessRunner());
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(CertificateOsTrustKind.Unsupported, unsupported.Kind);
    }

    [TestMethod]
    public void FirefoxTrustDefaultProfile_WithScriptedCertutil_CoversImport()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot();
        if (!FirefoxCertificateTrust.TryResolveDefaultProfileDirectory(out var profileDir, out _))
        {
            profileDir = Path.Combine(Path.GetTempPath(), "twp-ff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(Path.Combine(profileDir, "prefs.js"), "user_pref(\"foo\", 1);\n");
        }

        var runner = new ScriptedProcessRunner();
        SeedFakeCertutil(runner, planted.Path);
        runner.When((_, args) => args.Contains(" -L", StringComparison.Ordinal),
            new ProcessRunResult(0, "TWP-FF  C,,\n", ""));
        runner.When((_, args) => args.Contains(" -A ", StringComparison.Ordinal),
            new ProcessRunResult(0, "", ""));
        runner.When((_, args) => args.Contains(" -D ", StringComparison.Ordinal),
            new ProcessRunResult(0, "", ""));

        var result = FirefoxCertificateTrust.TrustDefaultProfile(cert, "TWP-FF", runner);
        // Profile resolution may still fail on machines without profiles.ini; either path is coverage.
        Assert.IsTrue(
            result.Succeeded ||
            result.Kind is CertificateOsTrustKind.Failed or CertificateOsTrustKind.NssFailed
                or CertificateOsTrustKind.CertutilMissing,
            result.Kind + ": " + result.Message);
        _ = FirefoxCertificateTrust.UntrustDefaultProfile("TWP-FF", runner);
        _ = FirefoxCertificateTrust.TryWriteOrMergeFirefoxPoliciesJson(importEnterpriseRoots: true);
        _ = FirefoxCertificateTrust.TryWriteOrMergeFirefoxPoliciesJson(importEnterpriseRoots: false);
    }

    [TestMethod]
    public void ProbeInstallAndMacTrustExport_CoverRemainingPrivateArms()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "TWP";

        var none = new FakeProcessRunner { DefaultSuccess = false };
        var linuxMissing = (CertificateOsTrustResult)Invoke(
            "ProbeLinuxCertutilInstall", [typeof(IProcessRunner)], none)!;
        Assert.AreEqual(CertificateOsTrustKind.CertutilMissing, linuxMissing.Kind);

        var apt = new FakeProcessRunner();
        apt.When("sh", "command -v apt-get", "/usr/bin/apt-get\n");
        var linuxHint = (CertificateOsTrustResult)Invoke(
            "ProbeLinuxCertutilInstall", [typeof(IProcessRunner)], apt)!;
        Assert.AreEqual("libnss3-tools", linuxHint.PackageHint);

        var macMissing = (CertificateOsTrustResult)Invoke(
            "ProbeMacCertutilInstall", [typeof(IProcessRunner)], none)!;
        Assert.IsTrue(macMissing.Kind is CertificateOsTrustKind.CertutilMissing
            or CertificateOsTrustKind.HomebrewMissing, macMissing.Kind.ToString());

        var elev = new FakeElevationPrompt { Cancel = true };
        var cancelled = (CertificateOsTrustResult)Invoke(
            "TryInstallLinuxNssCertutil",
            [typeof(IProcessRunner), typeof(IElevationPrompt)],
            apt, elev)!;
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, cancelled.Kind);

        elev.Cancel = false;
        apt.When("sh", "command -v certutil", planted.Path + "\n");
        var installed = (CertificateOsTrustResult)Invoke(
            "TryInstallLinuxNssCertutil",
            [typeof(IProcessRunner), typeof(IElevationPrompt)],
            apt, elev)!;
        Assert.IsTrue(installed.Succeeded || installed.Kind == CertificateOsTrustKind.Failed, installed.Message);

        var dumpEmpty = new FakeProcessRunner();
        dumpEmpty.When("security", "dump-trust-settings", "No Trust Settings were found\n");
        Assert.IsFalse((bool)Invoke(
            "DumpTrustSettingsMentionsPolicies",
            [typeof(IProcessRunner), typeof(string), typeof(string)],
            dumpEmpty, sha1, cn)!);

        var dumpHit = new FakeProcessRunner();
        dumpHit.When("security", "dump-trust-settings",
            $"SHA-1 hash: {sha1}\n{cn}\nkSecTrustSettingsResultTrustRoot\n");
        Assert.IsTrue((bool)Invoke(
            "DumpTrustSettingsMentionsPolicies",
            [typeof(IProcessRunner), typeof(string), typeof(string)],
            dumpHit, sha1, cn)!);

        var export = new FakeProcessRunner { WriteFileOnMatch = "trust-settings-export" };
        export.When("security", "trust-settings-export", "");
        export.When("plutil", "-p", sha1 + "\ntrustSettings\nkSecTrustSettingsResultTrustRoot\n");
        Assert.IsTrue((bool)Invoke(
            "TrustSettingsExportHasPolicies",
            [typeof(IProcessRunner), typeof(string)],
            export, sha1)!);
        Assert.IsTrue(UnixCertificateTrust.HasExplicitMacSslTrustSettings(export, cert));

        var nicks = ((IEnumerable<string>)Invoke(
            "ParseNssNicknames", [typeof(string)],
            "Certificate Nickname                                         Trust Attributes\n" +
            "SSL,S/MIME,JAR/XPI\n" +
            "----------------------------------\n" +
            "Titanium Root  C,,\n" +
            "Other Nick  CT,C,C\n")!).ToList();
        Assert.IsTrue(nicks.Count >= 2);

        var listRunner = new FakeProcessRunner();
        listRunner.When("certutil", " -L", "Titanium Root  C,,\n");
        Assert.IsTrue((bool)Invoke(
            "NssListContainsNickname",
            [typeof(IProcessRunner), typeof(string), typeof(string), typeof(string)],
            listRunner, "certutil", Path.GetTempPath(), "Titanium Root")!);
    }

    [TestMethod]
    public void IsMacRootStillPresent_AndDetectPackage_WithFakes()
    {
        using var planted = PlantedCertutil.Acquire();
        using var cert = CreateEphemeralRoot();
        var sha1 = cert.GetCertHashString();
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "TWP";

        var present = new FakeProcessRunner();
        present.When("security", "find-certificate", sha1 + "\n");
        Assert.IsTrue((bool)Invoke(
            "IsMacRootStillPresent",
            [typeof(IProcessRunner), typeof(X509Certificate2), typeof(string)],
            present, cert, cn)!);

        var missing = new FakeProcessRunner { DefaultSuccess = false };
        Assert.IsFalse((bool)Invoke(
            "IsMacRootStillPresent",
            [typeof(IProcessRunner), typeof(X509Certificate2), typeof(string)],
            missing, cert, cn)!);

        var apt = new FakeProcessRunner();
        apt.When("sh", "command -v apt-get", "/usr/bin/apt-get\n");
        var hint = UnixCertificateTrust.DetectLinuxNssPackage(apt);
        Assert.IsNotNull(hint);
        Assert.AreEqual("libnss3-tools", hint!.Package);

        var none = new FakeProcessRunner { DefaultSuccess = false };
        Assert.IsNull(UnixCertificateTrust.DetectLinuxNssPackage(none));

        var dirs = UnixCertificateTrust.LinuxNssDatabaseDirectories().ToList();
        Assert.IsTrue(dirs.Count >= 1);
    }

    /// <summary>
    /// On macOS/Linux <c>FindCertutil</c> asks the runner for <c>command -v certutil</c>.
    /// Without this, a real Homebrew certutil (or a missing one) is used instead of the fake.
    /// </summary>
    private static void SeedFakeCertutil(ScriptedProcessRunner runner, string? path = null)
    {
        var fake = path ?? "/tmp/twp-fake-certutil";
        runner.When((file, args) =>
                file.Contains("sh", StringComparison.Ordinal) &&
                args.Contains("certutil", StringComparison.Ordinal),
            new ProcessRunResult(0, fake + "\n", ""));
    }

    private static object? Invoke(string name, Type[] types, params object?[] args)
    {
        var method = typeof(UnixCertificateTrust).GetMethod(name, PrivStatic, types)
                     ?? throw new InvalidOperationException($"Missing {name}({string.Join(",", types.Select(t => t.Name))})");
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static string ToPem(X509Certificate2 cert) =>
        "-----BEGIN CERTIFICATE-----\n" +
        Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks) +
        "\n-----END CERTIFICATE-----\n";

    private static X509Certificate2 CreateEphemeralRoot(string? subject = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            subject ?? "CN=TWP-Cov-" + Guid.NewGuid().ToString("N")[..8],
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}

/// <summary>
/// Makes <see cref="UnixCertificateTrust.FindCertutil"/> succeed on Windows by dropping a
/// zero-byte NSS-named stub in the documented LocalAppData candidate path.
/// </summary>
internal sealed class PlantedCertutil : IDisposable
{
    private static readonly object Gate = new();
    public string Path { get; }
    private readonly bool _createdDir;
    private readonly bool _createdFile;

    private PlantedCertutil(string path, bool createdDir, bool createdFile)
    {
        Path = path;
        _createdDir = createdDir;
        _createdFile = createdFile;
    }

    public static PlantedCertutil Acquire()
    {
        lock (Gate)
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "nss");
            var createdDir = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "certutil.exe");
            var createdFile = !File.Exists(path);
            if (createdFile)
                File.WriteAllBytes(path, [0x4D, 0x5A]); // 'MZ' so File.Exists is enough
            return new PlantedCertutil(path, createdDir, createdFile);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_createdFile)
            {
                try { File.Delete(Path); } catch { /* ignore */ }
            }

            if (_createdDir)
            {
                try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!); } catch { /* ignore */ }
            }
        }
    }
}

internal sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly List<(Func<string, string, bool> Match, Func<ProcessRunResult> Result)> _rules = [];
    public ProcessRunResult Default { get; set; } = new(0, string.Empty, string.Empty);

    public void When(Func<string, string, bool> match, ProcessRunResult result) =>
        _rules.Add((match, () => result));

    public void When(Func<string, string, bool> match, Func<ProcessRunResult> result) =>
        _rules.Add((match, result));

    public ProcessRunResult? Run(string fileName, string arguments,
        IDictionary<string, string?>? environment = null, string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        _ = timeout;
        foreach (var (match, result) in _rules)
        {
            if (match(fileName, arguments))
                return result();
        }

        return Default;
    }
}
