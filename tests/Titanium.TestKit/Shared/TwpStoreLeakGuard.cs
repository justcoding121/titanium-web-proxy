using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.TestKit.Shared;

/// <summary>
///     Fails the assembly if a test left a Titanium or TWP certificate in a real Windows store
///     that cannot be removed under the suppressed-UI path.
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
        if (after <= _before)
        {
            return;
        }

        var subjects = ListTwpSubjects();
        // Linked into each test host; those projects reference Titanium.Web.Proxy.
        CertificateManager.ScrubTestHostUserStores();
        ScrubCurrentUserStoresLocally();

        var remaining = CountTwpCertificates();
        if (remaining <= _before)
        {
            // SslStreamCertificateContext.Create can leave CurrentUser\CA copies that the
            // CertificateManager finally-block already tries to drop. A successful scrub means
            // the run did not leave durable trust entries behind.
            return;
        }

        Assert.Fail(
            $"Tests added Titanium/TWP certificate(s) that could not be scrubbed " +
            $"(before {_before}, after {after}, remaining {remaining}): {string.Join("; ", subjects)}. " +
            "Remove them in the test instead of writing to the user store.");
    }

    private static void ScrubCurrentUserStoresLocally()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var name in new[] { StoreName.My, StoreName.CertificateAuthority, StoreName.Root })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var cert in store.Certificates)
                {
                    if (!IsTwpCertificate(cert))
                    {
                        continue;
                    }

                    try
                    {
                        store.Remove(cert);
                    }
                    catch (Exception)
                    {
                        // Root remove can raise CryptUI; remaining count still fails the run.
                    }
                }
            }
            catch (Exception)
            {
                // ignore locked stores
            }
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
                        if (IsTwpCertificate(cert))
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

    private static List<string> ListTwpSubjects()
    {
        var subjects = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return subjects;
        }

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
                        if (IsTwpCertificate(cert))
                        {
                            subjects.Add($"{location}/{name}:{cert.Subject}");
                        }
                    }
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }

        return subjects;
    }

    private static bool IsTwpCertificate(X509Certificate2 cert)
    {
        var text = cert.Subject + " " + cert.Issuer;
        return text.Contains("Titanium", StringComparison.Ordinal) ||
               text.Contains("twp-", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TWP", StringComparison.Ordinal) ||
               text.Contains("Acme Corp", StringComparison.Ordinal) ||
               text.Contains("Acme Root CA", StringComparison.Ordinal);
    }
}
