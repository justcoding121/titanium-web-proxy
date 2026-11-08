using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.TestKit.Shared;

/// <summary>
///     Fails the assembly if a test left a Titanium or TWP certificate in a real Windows trust store
///     (Root or Personal). Root is only counted: opening it for write shows a Trusted Root install
///     or delete dialog. Intermediate CA and Personal copies are scrubbed best-effort.
/// </summary>
[TestClass]
public static class TwpStoreLeakGuard
{
    private static int _beforeTrust;

    [AssemblyInitialize]
    public static void RememberStoreCount(TestContext context)
    {
        _ = context;
        ScrubLeakedCertificates();
        _beforeTrust = CountTrustStoreCertificates();
    }

    [AssemblyCleanup]
    public static void StoreCountDidNotGrow()
    {
        ScrubLeakedCertificates();
        var afterTrust = CountTrustStoreCertificates();
        if (afterTrust <= _beforeTrust)
        {
            return;
        }

        Assert.Fail(
            $"Tests added {afterTrust - _beforeTrust} Titanium/TWP certificate(s) to Root or Personal " +
            $"(before {_beforeTrust}, after {afterTrust}): {string.Join("; ", ListTrustSubjects())}. " +
            "Remove them in the test instead of writing to the user store.");
    }

    /// <summary>
    ///     Best-effort removal of Titanium/TWP entries from CurrentUser (and LocalMachine when writable)
    ///     My, CA, and Root stores. Safe to call from a CLI harness Dispose.
    /// </summary>
    public static void ScrubLeakedCertificates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            // Root Remove shows a Trusted Root delete dialog. Count that store read-only
            // and fail the run if it grew; never open it for write from a test host.
            foreach (var name in new[] { StoreName.My, StoreName.CertificateAuthority })
            {
                try
                {
                    using var store = new X509Store(name, location);
                    store.Open(OpenFlags.ReadWrite);
                    var doomed = new List<X509Certificate2>();
                    foreach (var cert in store.Certificates)
                    {
                        if (IsTwpCertificate(cert))
                        {
                            doomed.Add(cert);
                        }
                    }

                    foreach (var cert in doomed)
                    {
                        try
                        {
                            store.Remove(cert);
                        }
                        catch (Exception)
                        {
                            // Root / LocalMachine remove can require elevation or CryptUI.
                        }
                    }
                }
                catch (Exception)
                {
                    // LocalMachine without elevation, or a locked store.
                }
            }
        }
    }

    private static int CountTrustStoreCertificates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var count = 0;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            foreach (var name in new[] { StoreName.Root, StoreName.My })
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
                    // A store the process cannot open is not a leak signal.
                }
            }
        }

        return count;
    }

    private static List<string> ListTrustSubjects()
    {
        var subjects = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return subjects;
        }

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            foreach (var name in new[] { StoreName.Root, StoreName.My })
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
