using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.TestKit.Shared;

/// <summary>
///     Fails the assembly if a test left a Titanium or TWP certificate in a real Windows store.
///     Root-store adds are already suppressed. Personal and intermediate stores are not.
/// </summary>
[TestClass]
public static class TwpStoreLeakGuard
{
    private static int _before;

    [AssemblyInitialize]
    public static void RememberStoreCount(TestContext context)
    {
        _ = context;
        _before = CountTwpCertificates();
    }

    [AssemblyCleanup]
    public static void StoreCountDidNotGrow()
    {
        var after = CountTwpCertificates();
        if (after > _before)
        {
            Assert.Fail(
                $"Tests added {after - _before} Titanium/TWP certificate(s) to the real Windows stores " +
                $"(before {_before}, after {after}). Remove them in the test instead of writing to the user store.");
        }
    }

    private static int CountTwpCertificates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var count = 0;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            foreach (var name in new[] { StoreName.Root, StoreName.My, StoreName.CertificateAuthority })
            {
                try
                {
                    using var store = new X509Store(name, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    foreach (var cert in store.Certificates)
                    {
                        var text = cert.Subject + " " + cert.Issuer;
                        if (text.Contains("Titanium", StringComparison.Ordinal) ||
                            text.Contains("twp-", StringComparison.Ordinal) ||
                            text.Contains("TWP", StringComparison.Ordinal) ||
                            text.Contains("Acme Corp", StringComparison.Ordinal))
                        {
                            count++;
                        }
                    }
                }
                catch (Exception)
                {
                    // A store the process cannot open (LocalMachine without elevation) is not a leak signal.
                }
            }
        }

        return count;
    }
}
