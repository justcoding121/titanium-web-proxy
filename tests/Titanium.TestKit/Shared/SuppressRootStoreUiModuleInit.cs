using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.TestKit.Shared;

/// <summary>
///     Stops Windows CryptUI, macOS Keychain, and Linux polkit root-store dialogs from hanging
///     a local or CI test run. Compiled into every test assembly. The environment variable stays
///     set even if a test clears <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/>.
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
