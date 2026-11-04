using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe.Shared;

public static class RootTrustHelpers
{
    public static bool IsTitaniumRootInCurrentUserStore(InterceptionService interception)
    {
        var thumb = interception.RootCertificate?.Thumbprint;
        if (string.IsNullOrEmpty(thumb))
            return false;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false).Count > 0;
    }

    /// <summary>
    /// Trust via InstallRootCertificate (may show CryptUI Yes/No). Refuses when suppress is on.
    /// </summary>
    public static bool TrustRootInteractively(InterceptionService interception)
    {
        if (CertificateManager.AreInteractiveRootStoreMutationsSuppressed)
        {
            // GitHub Actions always suppresses CryptUI. An ephemeral VM may still import with certutil.
            if (OperatingSystem.IsWindows() && IsEphemeralCiMachineTrust())
                return TrustRootWithCertUtil(interception);
            return false;
        }

        if (OperatingSystem.IsWindows() && IsTitaniumRootInCurrentUserStore(interception))
        {
            interception.VerifyOsUserSslTrust();
            return true;
        }

        return interception.InstallRootCertificate(machineStore: false)
               && (!OperatingSystem.IsWindows() || IsTitaniumRootInCurrentUserStore(interception)
                   || interception.VerifyOsUserSslTrust());
    }

    /// <summary>Silent Windows cleanup via certutil -delstore (no Add CryptUI).</summary>
    public static void UntrustRootSilent(InterceptionService interception)
    {
        if (!OperatingSystem.IsWindows())
        {
            interception.UntrustRootCertificate(machineStore: false);
            return;
        }

        var name = interception.RootCertificateName;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "certutil",
                Arguments = $"-user -delstore Root \"{name}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch
        {
            // ignore
        }

        interception.VerifyOsUserSslTrust();
    }

    private static bool IsEphemeralCiMachineTrust() =>
        string.Equals(Environment.GetEnvironmentVariable("TITANIUM_EPHEMERAL_VM"), "1", StringComparison.Ordinal)
        && string.Equals(Environment.GetEnvironmentVariable("TITANIUM_CI_MACHINE_TRUST"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Import the current root with certutil. No CryptUI. Only for the ephemeral CI job.
    /// </summary>
    private static bool TrustRootWithCertUtil(InterceptionService interception)
    {
        var cert = interception.RootCertificate;
        if (cert is null)
            return false;

        var path = Path.Combine(Path.GetTempPath(), "titanium-ci-root-" + cert.Thumbprint + ".cer");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        try
        {
            var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
            RunCertUtil(certutil, "-user -addstore -f Root \"" + path + "\"");
            RunCertUtil(certutil, "-addstore -f Root \"" + path + "\"");
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp cert */ }
        }

        if (!IsTitaniumRootInCurrentUserStore(interception))
            return false;

        interception.VerifyOsUserSslTrust();
        return true;
    }

    private static void RunCertUtil(string certutil, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = certutil,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process?.WaitForExit(20000);
    }
}
