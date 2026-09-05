using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Prevent Windows CryptUI / macOS Keychain / Linux polkit Root-store dialogs from hanging
///     local or CI unit test runs. <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c> is mandatory for unit
///     suites: even if a test clears <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/>,
///     the env check still blocks interactive Root Add/Remove.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
        // Belt-and-suspenders: env is checked even when the static flag is cleared.
        Environment.SetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI", "1");
    }
}
