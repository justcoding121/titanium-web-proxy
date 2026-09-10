using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Prevent OS cert UI from hanging Inspector unit tests. Prefer
///     <c>UseInMemoryTrustState</c> for Install CA paths; <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c>
///     is mandatory so clearing the static suppress flag still cannot open CryptUI/Keychain/polkit.
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
