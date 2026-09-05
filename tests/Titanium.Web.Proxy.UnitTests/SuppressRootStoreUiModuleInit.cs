using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging local/CI test runs.
///     Sets both the in-process flag and <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c> so a buggy test
///     that flips <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/> back to
///     false still cannot open Root-store UI in this process.
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
