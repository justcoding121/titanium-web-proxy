using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Guard for tests that would need Windows CryptUI / interactive Root trust. A developer machine stays
/// inconclusive. An ephemeral CI VM that already installed machine trust sets
/// <c>TITANIUM_CI_MACHINE_TRUST=1</c> and <c>TITANIUM_EPHEMERAL_VM=1</c> and the tests run.
/// </summary>
internal static class RootStoreUiTestGuards
{
    /// <summary>
    ///     True when this process must not change the user trust store or WinINET proxy.
    ///     Ephemeral CI with machine trust already installed is the exception.
    /// </summary>
    public static bool IsAutomatedCiOrSkipEnv() => !MachineTrustRun();

    /// <summary>
    ///     Inconclusive unless this is an ephemeral VM whose machine CA was installed before the test.
    /// </summary>
    public static void RequireInteractiveRootTrustAvailable()
    {
        if (MachineTrustRun())
        {
            return;
        }

        Assert.Inconclusive(
            "Skipped: interactive Root-store trust needs a person to answer OS dialogs. " +
            "CI sets TITANIUM_CI_MACHINE_TRUST=1 and TITANIUM_EPHEMERAL_VM=1 after machine-trust install.");
    }

    private static bool MachineTrustRun() =>
        string.Equals(Environment.GetEnvironmentVariable("TITANIUM_CI_MACHINE_TRUST"), "1", StringComparison.Ordinal)
        && string.Equals(Environment.GetEnvironmentVariable("TITANIUM_EPHEMERAL_VM"), "1", StringComparison.Ordinal);
}
