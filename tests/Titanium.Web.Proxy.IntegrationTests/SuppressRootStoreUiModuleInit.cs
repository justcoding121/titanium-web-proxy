using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging local/CI test runs.
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
