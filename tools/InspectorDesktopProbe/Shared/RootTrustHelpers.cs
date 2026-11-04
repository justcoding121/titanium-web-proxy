using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
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

    /// <summary>Silent Windows cleanup. Registry delete avoids the Root-store CryptUI prompt.</summary>
    public static void UntrustRootSilent(InterceptionService interception)
    {
        if (!OperatingSystem.IsWindows())
        {
            interception.UntrustRootCertificate(machineStore: false);
            return;
        }

        var thumb = interception.RootCertificate?.Thumbprint;
        if (!string.IsNullOrEmpty(thumb))
        {
            DeleteRootRegistryKey(Registry.CurrentUser, thumb);
            try
            {
                DeleteRootRegistryKey(Registry.LocalMachine, thumb);
            }
            catch
            {
                // LocalMachine is best-effort when the process is not elevated.
            }
        }

        if (!string.IsNullOrEmpty(thumb) && IsTitaniumRootInCurrentUserStore(interception))
        {
            var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
            RunCertUtil(certutil, "-user -delstore Root " + thumb, timeoutMs: 3000);
        }

        interception.VerifyOsUserSslTrust();
    }

    /// <summary>
    /// Import a PFX's public certificate into the Windows user and machine Root stores.
    /// Used when Inspector has already written its root and CryptUI cannot be answered.
    /// </summary>
    public static bool TrustPfxInWindowsRootStore(string pfxPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(pfxPath))
            return false;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(pfxPath);
        }
        catch
        {
            return false;
        }

        using var cert = X509CertificateLoader.LoadPkcs12(bytes, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
        TrustDerInWindowsRootStore(cert.Export(X509ContentType.Cert), cert.Thumbprint);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
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

        return TrustDerInWindowsRootStore(cert.Export(X509ContentType.Cert), cert.Thumbprint)
               && IsTitaniumRootInCurrentUserStore(interception)
               && interception.VerifyOsUserSslTrust();
    }

    private static bool TrustDerInWindowsRootStore(byte[] der, string thumbprint)
    {
        var path = Path.Combine(Path.GetTempPath(), "titanium-ci-root-" + thumbprint + ".cer");
        File.WriteAllBytes(path, der);
        try
        {
            var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
            RunCertUtil(certutil, "-user -addstore -f Root \"" + path + "\"", timeoutMs: 20000);
            RunCertUtil(certutil, "-addstore -f Root \"" + path + "\"", timeoutMs: 20000);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp cert */ }
        }

        return true;
    }

    private static void DeleteRootRegistryKey(RegistryKey hive, string thumbprint)
    {
        hive.DeleteSubKeyTree(
            @"Software\Microsoft\SystemCertificates\Root\Certificates\" + thumbprint,
            throwOnMissingSubKey: false);
    }

    private static void RunCertUtil(string certutil, string arguments, int timeoutMs)
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
        if (process is null)
            return;

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* dialog hung */ }
        }
    }
}
