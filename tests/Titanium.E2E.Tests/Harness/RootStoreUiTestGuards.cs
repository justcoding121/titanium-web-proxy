using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Guards for tests that must open Windows CryptUI / interactive Root trust.
/// CI and <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c> always suppress Root Add/Remove — those
/// environments must never run interactive trust or they hang on system dialogs.
/// </summary>
internal static class RootStoreUiTestGuards
{
    /// <summary>
    ///     Inconclusive when Root-store UI cannot be shown (CI / skip env / still suppressed
    ///     after clearing the in-process flag). Call before intentional interactive Install CA.
    /// </summary>
    public static void RequireInteractiveRootTrustAvailable()
    {
        if (IsAutomatedCiOrSkipEnv())
        {
            Assert.Inconclusive(
                "Skipped: CI / TITANIUM_SKIP_ROOT_STORE_UI would hang on Windows Root CryptUI or certutil prompts");
        }

        // ModuleInitializer sets Suppress=true; clear it for this intentional interactive path.
        CertificateManager.SuppressInteractiveRootStoreMutations = false;

        if (CertificateManager.AreInteractiveRootStoreMutationsSuppressed)
        {
            Assert.Inconclusive(
                "Skipped: Root-store UI remains suppressed (CI env still set); refusing interactive trust");
        }
    }

    public static bool IsAutomatedCiOrSkipEnv() =>
        IsTruthy("CI")
        || IsTruthy("GITHUB_ACTIONS")
        || IsTruthy("TF_BUILD")
        || string.Equals(Environment.GetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI"), "1",
            StringComparison.Ordinal);

    private static bool IsTruthy(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(v)
               && !string.Equals(v, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }
}
