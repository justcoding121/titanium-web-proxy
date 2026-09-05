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
            return false;

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
}
