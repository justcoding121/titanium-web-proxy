using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
///     Automated E2E must never open OS certificate UI (Windows CryptUI, Keychain, polkit): nobody is there
///     to answer it. <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c> is mandatory so clearing the static flag in a test
///     still cannot open a prompt. Live Install CA / browser trust walks belong to
///     <c>tools/InspectorDesktopProbe</c>, not to <c>dotnet test</c>.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
        Environment.SetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI", "1");
    }
}
