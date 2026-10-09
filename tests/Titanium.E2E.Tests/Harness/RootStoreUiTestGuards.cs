using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Guard for tests that would need Windows CryptUI / interactive Root trust. Automated runs have no one to
/// answer those dialogs, so such tests are always inconclusive here; run the live walk with
/// <c>dotnet run --project tools/InspectorDesktopProbe -- all</c> instead.
/// </summary>
internal static class RootStoreUiTestGuards
{
    /// <summary>
    ///     E2E always runs unattended; tests that mutate machine-wide OS state (Root store, WinINET proxy)
    ///     check this and skip.
    /// </summary>
    public static bool IsAutomatedCiOrSkipEnv() => true;

    /// <summary>Always inconclusive: interactive Root-store trust is never exercised from <c>dotnet test</c>.</summary>
    public static void RequireInteractiveRootTrustAvailable() =>
        Assert.Inconclusive(
            "Skipped: interactive Root-store trust needs a person to answer OS dialogs. " +
            "Use tools/InspectorDesktopProbe for the live walk.");
}
